using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Gapo.Optimiser.Game;
using Gapo.Optimiser.Hardware;
using Gapo.Optimiser.Monitoring;
using Gapo.Shared;

namespace Gapo.Optimiser.Engine
{
    public enum Phase { Idle, Preparing, Baseline, Diagnosing, Testing, Pacing, Validating, Done, Failed, Cancelled }

    public sealed class EngineStatus
    {
        public Phase Phase;
        public string Title = "", Detail = "";
        public double Progress;          // 0..1
        public bool Measuring;           // a measurement window is open (UI should stay quiet)
        public ExperimentLog LastLog;
        public Diagnosis Diagnosis;
    }

    /// <summary>
    /// The closed loop from the spec: Observe → Diagnose → Predict → Change one setting → Measure →
    /// Compare → Keep or revert → Learn. Every change is transactional (snapshot, apply, measure,
    /// commit or roll back) and every decision is recorded with its numbers.
    /// </summary>
    public sealed class OptimisationEngine
    {
        readonly GameLink _link;
        readonly SystemMonitor _monitor;
        readonly HardwareProfile _hw;
        Thread _thread;
        volatile bool _cancel;
        public volatile EngineStatus Status = new EngineStatus { Phase = Phase.Idle };
        public OptimisationReport Report { get; private set; }
        public event Action<EngineStatus> Changed;
        public bool Running { get { var t = _thread; return t != null && t.IsAlive; } }

        // Measurement tuning
        const int DiscardMs = 650, WindowMs = 2600, MinFrames = 45, MaxWindowMs = 7000;

        SandboxSettings _current, _original;
        FrameStats _reference;
        double _noise = 0.03;
        int _windows, _frames;
        double _selfInWindows; int _selfSamples;
        int _stepsPlanned = 12, _stepsDone;
        ProcessPriorityClass _originalPriority = ProcessPriorityClass.Normal;
        bool _priorityChanged;

        public OptimisationEngine(GameLink link, SystemMonitor monitor, HardwareProfile hw)
        {
            _link = link; _monitor = monitor; _hw = hw;
        }

        public void Start(Goal goal)
        {
            if (Running) return;
            _cancel = false;
            _thread = new Thread(() => Run(goal)) { IsBackground = true, Name = "GAPO engine", Priority = ThreadPriority.BelowNormal };
            _thread.Start();
        }

        public void Cancel() { _cancel = true; }

        void Publish(Phase phase, string title, string detail, bool measuring = false, ExperimentLog log = null, Diagnosis diag = null)
        {
            var s = new EngineStatus
            {
                Phase = phase, Title = title, Detail = detail, Measuring = measuring, LastLog = log ?? Status.LastLog, Diagnosis = diag ?? Status.Diagnosis,
                Progress = ProgressFor(phase),
            };
            Status = s;
            var h = Changed; if (h != null) try { h(s); } catch { }
        }

        double ProgressFor(Phase phase)
        {
            switch (phase)
            {
                case Phase.Idle: case Phase.Preparing: return 0.02;
                case Phase.Baseline: return 0.06;
                case Phase.Diagnosing: return 0.16;
                case Phase.Testing:
                    double done = _experimentsDone, left = Math.Max(1, _experimentsLeft);
                    return 0.2 + 0.62 * done / (done + left);
                case Phase.Pacing: return 0.85;
                case Phase.Validating: return 0.9;
                case Phase.Done: return 1;
                default: return Status.Progress;
            }
        }

        int _experimentsDone, _experimentsLeft = 6;

        void Check() { if (_cancel) throw new OperationCanceledException(); if (!_link.Connected) throw new InvalidOperationException("The game was closed."); }

        // ================================================================= main loop

        void Run(Goal goal)
        {
            var rep = new OptimisationReport { Started = DateTime.Now, Goal = goal, Hardware = _hw };
            Report = rep;
            var self = Process.GetCurrentProcess();
            ProcessPriorityClass selfPrio = ProcessPriorityClass.Normal;
            try { selfPrio = self.PriorityClass; self.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
            double selfCpuStart = self.TotalProcessorTime.TotalMilliseconds;
            var wall = Stopwatch.StartNew();
            try
            {
                _stepsDone = 0; _experimentsDone = 0; _experimentsLeft = 6; _hitchFixAt = -1; _windows = 0; _frames = 0; _selfInWindows = 0; _selfSamples = 0;
                Publish(Phase.Preparing, "Getting ready", "Connecting to the game and waiting until it has finished loading.");
                _monitor.SetGame(_link.Pid);
                _original = _link.ReadSettings();
                if (_original == null) throw new InvalidOperationException("Could not read the game's settings file.");
                _current = _original.Clone();
                rep.OriginalSettings = _original.Clone();
                try { using (var gp = Process.GetProcessById(_link.Pid)) _originalPriority = gp.PriorityClass; } catch { }
                WaitForWarmup();

                // Target: match the display's refresh rate (the spec's frame budget, §12).
                double refresh = _hw.EffectiveRefreshHz;
                rep.TargetFps = goal == Goal.MaxFps ? Math.Max(refresh, 144) : Math.Min(refresh, 144);
                rep.TargetReason = goal == Goal.MaxFps ? "as high as possible" : (_hw.Display.RefreshUnknown ? "refresh rate unknown, assumed 60 Hz" : "your " + refresh + " Hz display");
                if (!_link.SendCommand(SandboxCommand.BenchmarkCamera, 1)) throw new InvalidOperationException("The game did not respond to the benchmark command.");
                _link.Toast("GAPO is measuring — benchmark flight");

                // ---------------- Observe: A/A baseline gives the noise floor.
                Publish(Phase.Baseline, "Measuring the baseline", "Two identical benchmark runs. How much they differ tells GAPO how precise its measurements are on this PC.", true);
                FrameStats a, b;
                var baseline = MeasurePair("Baseline", out a, out b, rep.BaselineTrace);
                _noise = Math.Abs(a.MedianWorkMs - b.MedianWorkMs) / Math.Max(0.01, (a.MedianWorkMs + b.MedianWorkMs) / 2);
                _noise = Math.Max(0.025, Math.Min(0.10, _noise * 2.5));
                rep.NoiseFloorPct = _noise * 100;
                _reference = baseline;
                rep.Baseline = _reference;
                _stepsDone++;

                // ---------------- Diagnose: extra evidence gathered between windows (never inside one).
                Publish(Phase.Diagnosing, "Diagnosing", "Checking per-core load, the game's busiest thread and background apps.");
                _monitor.ThreadSampling = true;
                var snapA = ProcessSnapshot.Take(_link.Pid);
                var sw = Stopwatch.StartNew();
                SleepChecked(2300);
                var snapB = ProcessSnapshot.Take(_link.Pid);
                _monitor.ThreadSampling = false;
                var monSamples = _monitor.History(_monitor.Now - 9000);
                rep.BaselineMonitor = MonitorSummary.From(monSamples);
                rep.BackgroundApps = Advisor.Contention(snapA, snapB, sw.Elapsed.TotalSeconds, _link.Pid, self.Id);
                EnsureHitchObservation();
                var origFrames = _link.FramesSince(_link.LatestGameTimeMs - 60000);
                origFrames.RemoveAll(f => f.Revision != _current.Revision || (f.Flags & (int)FrameFlags.Warmup) != 0);
                rep.BaselineHitches = FrameStats.Compute(origFrames);
                bool memPressure = rep.BaselineMonitor.MemLoadPct >= 88;
                var diag = Diagnosis.Run(_reference, rep.BaselineMonitor, _hw, _current, rep.TargetFps, rep.BaselineHitches);
                rep.BaselineDiagnosis = diag;
                rep.NotTouched.AddRange(KnowledgeBase.NotWorthTouching(_current, _reference, memPressure));
                RecordHardwareDecisions(rep, memPressure);
                _stepsPlanned = 3 + Math.Min(12, KnowledgeBase.Generate(_current, _reference, _hw, memPressure).Count) + 2;
                _stepsDone++;
                Publish(Phase.Diagnosing, diag.Title, diag.Summary, false, null, diag);
                SleepChecked(900);

                // ---------------- Hitch fix first: sporadic hitches would contaminate every later measurement.
                if (rep.BaselineHitches.AutosaveHitches > 0 && _current.AutosaveSeconds > 0 && _current.AutosaveSeconds < 120)
                {
                    var c = new Candidate
                    {
                        Setting = "Autosave interval", From = _current.AutosaveSeconds + " s", To = "5 min", Kind = ChangeKind.Hitch, Resource = Resource.Storage, VisualLoss = 0,
                        PredictedMs = 0,
                        Hypothesis = "Every " + _current.AutosaveSeconds + " s the game saves the world on its main thread and waits for the disk, freezing for ~" + rep.BaselineHitches.AutosaveHitchMs.ToString("0") + " ms. Saving every 5 minutes removes almost all of those freezes.",
                        Apply = s => s.AutosaveSeconds = 300,
                    };
                    ApplyHitchFix(rep, c);
                }

                // ---------------- Background contention: only then is process priority worth testing.
                if (rep.BaselineMonitor.BackgroundCpuPct >= 15 && (_originalPriority == ProcessPriorityClass.Normal || _originalPriority == ProcessPriorityClass.BelowNormal || _originalPriority == ProcessPriorityClass.Idle))
                    TestPriority(rep);
                else if (rep.BaselineMonitor.BackgroundCpuPct < 15)
                    rep.NotTouched.Add("Process priority — background apps used only " + rep.BaselineMonitor.BackgroundCpuPct.ToString("0") + "% CPU, so raising the game's priority has nothing to win (and random priority changes are a classic 'booster' mistake).");
                else
                    rep.NotTouched.Add("Process priority — the game already runs at " + _originalPriority + " priority; GAPO doesn't raise it further.");

                // ---------------- Experiments: best predicted gain per unit of visual cost first.
                var tried = new HashSet<string>();
                var blocked = new HashSet<string>();
                double budget = 1000.0 / rep.TargetFps;
                while (true)
                {
                    Check();
                    var cands = KnowledgeBase.Generate(_current, _reference, _hw, memPressure)
                        .Where(c => !tried.Contains(c.Key) && !blocked.Contains(c.Setting))
                        .Where(c => c.Resource == Resource.Memory || c.PredictedMs >= Math.Max(0.25, _reference.MedianWorkMs * 0.025))
                        .OrderByDescending(c => c.Value).ToList();
                    _experimentsLeft = Math.Min(6, cands.Count(c => goal == Goal.MaxFps || c.VisualLoss <= 2)) + 1;
                    if (cands.Count == 0) break;
                    var next = cands[0];
                    bool targetMet = _reference.MedianWorkMs <= budget * 0.88 && _reference.P95FrameMs <= budget * 1.25;
                    if (goal != Goal.MaxFps && targetMet && next.VisualLoss > 0)
                    {
                        rep.NotTouched.Add("Stopped lowering visuals: the " + rep.TargetFps.ToString("0") + " FPS target is met with headroom (" + KnowledgeBase.F(_reference.MedianWorkMs) + " ms of " + KnowledgeBase.F(budget) + " ms). Remaining options would only cost quality: " +
                            string.Join(", ", cands.Where(c => c.VisualLoss > 0).Take(4).Select(c => c.Title)) + ".");
                        break;
                    }
                    if (goal == Goal.Quality && next.VisualLoss >= 4 && !targetMet)
                    {
                        rep.NotTouched.Add("Quality mode: not trying " + next.Title + " (visual cost " + next.VisualLoss + "/10).");
                        tried.Add(next.Key);
                        continue;
                    }
                    tried.Add(next.Key);
                    var log = RunExperiment(rep, next, goal);
                    if (log.Decision != Decision.Kept) blocked.Add(next.Setting);
                }

                // ---------------- Give visual quality back when there is plenty of headroom.
                if (goal == Goal.Balanced || goal == Goal.Quality) QualityGiveBack(rep, budget);

                // ---------------- Frame pacing (cap) — applied last, judged on smoothness.
                Pacing(rep, goal);

                // ---------------- Validate.
                Publish(Phase.Validating, "Validating the result", "Measuring the final configuration on the same benchmark flight as the baseline.", true);
                FrameStats f1, f2;
                rep.Final = MeasurePair("Final", out f1, out f2, rep.FinalTrace);
                rep.FinalMonitor = MonitorSummary.From(_monitor.History(_monitor.Now - 7000));
                var finalFrames = _link.FramesSince(_link.LatestGameTimeMs - 60000);
                finalFrames.RemoveAll(f => f.Revision != _current.Revision);
                rep.FinalHitches = FrameStats.Compute(finalFrames);
                VerifyHitchFix(rep);
                rep.FinalDiagnosis = Diagnosis.Run(rep.Final, rep.FinalMonitor, _hw, _current, rep.TargetFps, rep.FinalHitches);
                rep.FinalSettings = _current.Clone();
                rep.FinalSettings.ModifiedBy = "GAPO Optimiser";
                rep.Recommendations = Advisor.Build(_hw, rep.BaselineMonitor, rep.BackgroundApps, rep.BaselineHitches, rep);
                rep.Completed = true;
                _stepsDone = _stepsPlanned;
                _link.SendCommand(SandboxCommand.BenchmarkCamera, 0);
                _link.Toast("GAPO: " + OptimisationReport.Signed(rep.FpsGainPct) + "% average FPS (" + rep.Baseline.AvgFps.ToString("0") + " → " + rep.Final.AvgFps.ToString("0") + "). Details in GAPO.");
                Finish(rep, self, selfCpuStart, wall, selfPrio);
                Publish(Phase.Done, "Done", OptimisationReport.Signed(rep.FpsGainPct) + "% average FPS");
            }
            catch (OperationCanceledException)
            {
                rep.Cancelled = true;
                SafeRelease();
                Finish(rep, self, selfCpuStart, wall, selfPrio);
                Publish(Phase.Cancelled, "Stopped", "Optimisation stopped. Changes kept so far are still applied; use Revert all to undo them.");
            }
            catch (Exception ex)
            {
                rep.Error = ex.Message;
                SafeRelease();
                Finish(rep, self, selfCpuStart, wall, selfPrio);
                Publish(Phase.Failed, "Optimisation stopped", ex.Message);
            }
        }

        void Finish(OptimisationReport rep, Process self, double selfCpuStart, Stopwatch wall, ProcessPriorityClass selfPrio)
        {
            rep.Finished = DateTime.Now;
            rep.FinalSettings = rep.FinalSettings ?? (_current != null ? _current.Clone() : null);
            try
            {
                self.Refresh();
                rep.SelfCpuAvgPct = (self.TotalProcessorTime.TotalMilliseconds - selfCpuStart) / wall.Elapsed.TotalMilliseconds / Environment.ProcessorCount * 100;
                rep.SelfPrivateMB = self.PrivateMemorySize64 / 1048576.0;
                self.PriorityClass = selfPrio;
            }
            catch { }
            rep.SelfCpuPeakPct = _monitor.SelfCpuPeakPct;
            rep.SelfCpuInWindowsPct = _selfSamples > 0 ? _selfInWindows / _selfSamples : 0;
            rep.Windows = _windows; rep.MeasuredFrames = _frames;
            try { rep.Save(); } catch { }
        }

        void SafeRelease()
        {
            _monitor.ThreadSampling = false;
            try { if (_link.Connected) _link.SendCommand(SandboxCommand.BenchmarkCamera, 0, 800); } catch { }
        }

        void SleepChecked(int ms)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < ms) { Check(); Thread.Sleep(50); }
        }

        void WaitForWarmup()
        {
            var sw = Stopwatch.StartNew();
            while (true)
            {
                Check();
                var recent = _link.LastFrames(90);
                bool warming = _link.State != SandboxState.Running || recent.Count < 30 || recent.Any(f => (f.Flags & (int)FrameFlags.Warmup) != 0);
                if (!warming) return;
                Publish(Phase.Preparing, "Waiting for the game to finish loading", "The game is still streaming its world. Measuring now would blame the wrong thing — like judging a game while it compiles shaders.");
                if (sw.ElapsedMilliseconds > 45000) return;
                Thread.Sleep(250);
            }
        }

        /// <summary>Hitches that repeat every ~10 s need a longer observation than one window.</summary>
        void EnsureHitchObservation()
        {
            var sw = Stopwatch.StartNew();
            while (_link.ObservedSeconds < 14 && sw.ElapsedMilliseconds < 15000)
            {
                Publish(Phase.Diagnosing, "Watching for stutter", "Some hitches only happen every few seconds, so GAPO watches a little longer before judging them.");
                SleepChecked(500);
            }
        }

        // ================================================================= measurement

        FrameStats Measure(int expectedRevision, string label) { return FrameStats.Compute(MeasureFrames(expectedRevision, label)); }

        List<FrameRecord> MeasureFrames(int expectedRevision, string label)
        {
            Check();
            if (!_link.SendCommand(SandboxCommand.RestartBenchmarkPath, 0)) throw new InvalidOperationException("The game stopped responding.");
            double start = _link.LatestGameTimeMs + DiscardMs;
            _monitor.MarkSelf();
            var sw = Stopwatch.StartNew();
            List<FrameRecord> frames;
            while (true)
            {
                Thread.Sleep(200);
                Check();
                frames = _link.FramesSince(start);
                frames.RemoveAll(f => f.Revision != expectedRevision || (f.Flags & (int)FrameFlags.Warmup) != 0);
                double span = frames.Count > 1 ? frames[frames.Count - 1].TimeMs - frames[0].TimeMs : 0;
                if ((span >= WindowMs && frames.Count >= MinFrames) || sw.ElapsedMilliseconds > MaxWindowMs + DiscardMs) break;
            }
            if (frames.Count < 8) throw new InvalidOperationException("Too few frames were measured (" + frames.Count + ").");
            _selfInWindows += _monitor.SelfCpuSinceMarkPct(); _selfSamples++;
            _windows++; _frames += frames.Count;
            return frames;
        }

        /// <summary>Two identical windows; returns both (for the noise estimate) and their union.</summary>
        FrameStats MeasurePair(string label, out FrameStats a, out FrameStats b, List<float> trace = null)
        {
            var fa = MeasureFrames(_current.Revision, label + " A");
            if (trace != null) { trace.Clear(); foreach (var f in fa) trace.Add(f.FrameMs); }
            var fb = MeasureFrames(_current.Revision, label + " B");
            a = FrameStats.Compute(fa); b = FrameStats.Compute(fb);
            var all = new List<FrameRecord>(fa); all.AddRange(fb);
            return FrameStats.Compute(all);
        }

        // ================================================================= one experiment

        int ApplySettings(SandboxSettings s, string note)
        {
            int rev = _link.WriteSettings(s, "GAPO Optimiser" + (note != null ? " (" + note + ")" : ""));
            if (!_link.WaitForRevision(rev, 4000)) throw new InvalidOperationException("The game did not apply the new settings.");
            s.Revision = rev;
            return rev;
        }

        ExperimentLog RunExperiment(OptimisationReport rep, Candidate c, Goal goal)
        {
            var log = new ExperimentLog
            {
                Index = rep.Experiments.Count + 1, Title = c.Title, Setting = c.Setting, Hypothesis = c.Hypothesis, Kind = c.Kind,
                VisualLoss = c.VisualLoss, PredictedMs = c.PredictedMs, Before = _reference,
            };
            var sw = Stopwatch.StartNew();
            Publish(Phase.Testing, "Testing: " + c.Title, c.Hypothesis + " Predicted saving ≈ " + KnowledgeBase.F(c.PredictedMs) + " ms.", true);
            _link.Toast("GAPO testing: " + c.Title);

            var snapshot = _current.Clone();                       // transaction: snapshot
            var trial = _current.Clone();
            c.Apply(trial);
            int rev = ApplySettings(trial, "testing " + c.Setting);   // apply
            var result = Measure(rev, c.Title);                      // measure
            log.After = result;

            double gain = (_reference.MedianWorkMs - result.MedianWorkMs) / _reference.MedianWorkMs;
            double pace = (_reference.P95FrameMs - result.P95FrameMs) / _reference.P95FrameMs;
            double lowDrop = (_reference.Low1Fps - result.Low1Fps) / Math.Max(0.01, _reference.Low1Fps);
            log.GainPct = gain * 100; log.PaceGainPct = pace * 100;

            double wPerf, wPace, wVis;
            Weights(goal, out wPerf, out wPace, out wVis);
            double score = wPerf * gain + wPace * pace - wVis * c.VisualLoss;
            bool memOnly = c.Resource == Resource.Memory;

            if (!memOnly && gain <= _noise)
            {
                log.Decision = Decision.Reverted;
                log.Reason = "Reverted: " + OptimisationReport.Signed(gain * 100) + "% is within the ±" + (_noise * 100).ToString("0.0") + "% measurement noise — no proven benefit" + (c.VisualLoss > 0 ? ", so not worth any visual cost." : ".");
            }
            else if (!memOnly && lowDrop > Math.Max(0.12, _noise * 3) && result.Low1Fps < _reference.Low1Fps)
            {
                log.Decision = Decision.Reverted;
                log.Reason = "Reverted: average speed improved but 1% lows got worse (" + _reference.Low1Fps.ToString("0") + " → " + result.Low1Fps.ToString("0") + " FPS). Smoothness matters more than averages.";
            }
            else if (!memOnly && score <= 0)
            {
                log.Decision = Decision.Reverted;
                log.Reason = "Reverted: " + OptimisationReport.Signed(gain * 100) + "% faster isn't worth a visual cost of " + c.VisualLoss + "/10 in " + OptimisationReport.GoalName(goal) + " mode.";
            }
            else
            {
                log.Decision = Decision.Kept;
                log.Reason = memOnly ? "Kept: relieves memory pressure." :
                    "Kept: frame work " + KnowledgeBase.F(_reference.MedianWorkMs) + " → " + KnowledgeBase.F(result.MedianWorkMs) + " ms (" + OptimisationReport.Signed(gain * 100) + "%, above the ±" + (_noise * 100).ToString("0.0") + "% noise floor)" +
                    (c.VisualLoss == 0 ? " with no visual cost." : c.VisualLoss <= 2 ? " for a small visual cost." : " — a visible trade-off, but worth it for this goal.");
            }

            if (log.Decision == Decision.Kept)
            {
                _current = trial;                                  // commit
                _reference = result;
                var d = Diagnosis.Run(_reference, MonitorSummary.From(_monitor.History(_monitor.Now - 4000)), _hw, _current, rep.TargetFps, null);
                log.BottleneckAfter = d.Title;
                Publish(Phase.Testing, "Kept: " + c.Title, log.Reason, false, log, d);
            }
            else
            {
                ApplySettings(snapshot, "rollback");                // roll back
                _current = snapshot;
                Publish(Phase.Testing, "Reverted: " + c.Title, log.Reason, false, log);
            }
            log.Seconds = sw.Elapsed.TotalSeconds;
            log.SelfCpuPct = _monitor.SelfCpuSinceMarkPct();
            rep.Experiments.Add(log);
            _stepsDone++; _experimentsDone++;
            if (_stepsDone >= _stepsPlanned) _stepsPlanned = _stepsDone + 2;
            Publish(Phase.Testing, log.Decision == Decision.Kept ? "Kept: " + c.Title : "Reverted: " + c.Title, log.Reason, false, log);
            return log;
        }

        static void Weights(Goal g, out double perf, out double pace, out double vis)
        {
            switch (g)
            {
                case Goal.MaxFps: perf = 1.0; pace = 0.3; vis = 0.004; break;
                case Goal.Quality: perf = 1.0; pace = 0.5; vis = 0.05; break;
                case Goal.Smooth: perf = 0.6; pace = 1.2; vis = 0.02; break;
                default: perf = 1.0; pace = 0.5; vis = 0.022; break;
            }
        }

        void ApplyHitchFix(OptimisationReport rep, Candidate c)
        {
            var log = new ExperimentLog { Index = rep.Experiments.Count + 1, Title = c.Title, Setting = c.Setting, Hypothesis = c.Hypothesis, Kind = c.Kind, Before = rep.BaselineHitches };
            Publish(Phase.Testing, "Fixing: " + c.Title, c.Hypothesis, false);
            var trial = _current.Clone();
            c.Apply(trial);
            ApplySettings(trial, "autosave");
            _current = trial;
            _hitchFixAt = _link.LatestGameTimeMs;
            log.Decision = Decision.Kept;
            log.Reason = "Kept: removes a repeating " + rep.BaselineHitches.AutosaveHitchMs.ToString("0") + " ms freeze with no visual cost. Done first because random hitches would otherwise pollute every later measurement.";
            rep.Experiments.Add(log);
            _stepsDone++;
            Publish(Phase.Testing, "Kept: " + c.Title, log.Reason, false, log);
        }

        double _hitchFixAt = -1;

        void VerifyHitchFix(OptimisationReport rep)
        {
            if (_hitchFixAt < 0) return;
            var log = rep.Experiments.FirstOrDefault(e => e.Kind == ChangeKind.Hitch);
            if (log == null) return;
            var since = _link.FramesSince(_hitchFixAt);
            if (since.Count < 2) return;
            double secs = (since[since.Count - 1].TimeMs - since[0].TimeMs) / 1000.0;
            int saves = since.Count(f => (f.Flags & (int)FrameFlags.Autosave) != 0);
            log.After = FrameStats.Compute(since);
            if (saves == 0)
                log.Reason += " Verified: no autosave freezes in the " + secs.ToString("0") + " s since (there were " + rep.BaselineHitches.AutosaveHitches + " in the " + (rep.BaselineHitches.DurationMs / 1000).ToString("0") + " s before).";
            else
                log.Reason += " Check: " + saves + " save(s) still happened in the " + secs.ToString("0") + " s since the change.";
        }

        void TestPriority(OptimisationReport rep)
        {
            var target = _originalPriority == ProcessPriorityClass.Normal ? ProcessPriorityClass.AboveNormal : ProcessPriorityClass.Normal;
            var log = new ExperimentLog
            {
                Index = rep.Experiments.Count + 1, Title = "Game process priority: " + PrioName(_originalPriority) + " → " + PrioName(target), Setting = "Process priority", Kind = ChangeKind.Windows, Before = _reference,
                Hypothesis = "Background apps use " + rep.BaselineMonitor.BackgroundCpuPct.ToString("0") + "% of the CPU" + (target == ProcessPriorityClass.Normal ? " and the game was started at " + PrioName(_originalPriority) + " priority, so it loses every contest for the CPU. Restoring normal priority should help." : ". Giving the game a slightly higher priority lets it win those contests."),
            };
            Publish(Phase.Testing, "Testing: " + log.Title, log.Hypothesis, true);
            try
            {
                using (var gp = Process.GetProcessById(_link.Pid)) gp.PriorityClass = target;
                _priorityChanged = true;
                var r = Measure(_current.Revision, "Priority");
                log.After = r;
                double gain = (_reference.MedianWorkMs - r.MedianWorkMs) / _reference.MedianWorkMs;
                log.GainPct = gain * 100;
                if (gain > _noise) { log.Decision = Decision.Kept; log.Reason = "Kept: " + OptimisationReport.Signed(gain * 100) + "% faster. Resets automatically when the game closes."; _reference = r; }
                else
                {
                    using (var gp = Process.GetProcessById(_link.Pid)) gp.PriorityClass = _originalPriority;
                    _priorityChanged = false;
                    log.Decision = Decision.Reverted; log.Reason = "Reverted: no measurable benefit (" + OptimisationReport.Signed(gain * 100) + "%).";
                }
            }
            catch (Exception ex) when (!(ex is OperationCanceledException)) { log.Decision = Decision.Failed; log.Reason = "Windows refused the change (" + ex.Message + ")."; }
            rep.Experiments.Add(log);
            _stepsDone++;
            Publish(Phase.Testing, log.Title, log.Reason, false, log);
        }

        static string PrioName(ProcessPriorityClass p)
        {
            switch (p)
            {
                case ProcessPriorityClass.AboveNormal: return "Above normal";
                case ProcessPriorityClass.BelowNormal: return "Below normal";
                default: return p.ToString();
            }
        }

        void QualityGiveBack(OptimisationReport rep, double budget)
        {
            if (_reference.MedianWorkMs > budget * 0.6) return;
            var kept = rep.Experiments.Where(e => e.Decision == Decision.Kept && e.Kind == ChangeKind.GameSetting && e.VisualLoss >= 2).OrderByDescending(e => e.VisualLoss).ToList();
            foreach (var e in kept)
            {
                if (_reference.MedianWorkMs > budget * 0.6) break;
                Check();
                var restore = RestoreSetting(e.Setting, rep.OriginalSettings);
                if (restore == null) continue;
                double cost = e.Before != null && e.After != null ? Math.Max(0, e.Before.MedianWorkMs - e.After.MedianWorkMs) : 0;
                if (_reference.MedianWorkMs + cost > budget * 0.8)
                {
                    rep.NotTouched.Add("Quality give-back — restoring " + e.Setting + " would cost about " + KnowledgeBase.F(cost) + " ms, and only " + KnowledgeBase.F(Math.Max(0, budget * 0.8 - _reference.MedianWorkMs)) + " ms of safe headroom is left.");
                    continue;
                }
                var log = new ExperimentLog { Index = rep.Experiments.Count + 1, Title = "Restore quality: " + e.Setting, Setting = e.Setting, Kind = ChangeKind.QualityRestore, VisualLoss = -e.VisualLoss, Before = _reference,
                    Hypothesis = "There is plenty of headroom (" + KnowledgeBase.F(_reference.MedianWorkMs) + " ms of a " + KnowledgeBase.F(budget) + " ms budget), so GAPO tries to give back the most visible reduction." };
                Publish(Phase.Testing, log.Title, log.Hypothesis, true);
                var snapshot = _current.Clone();
                var trial = _current.Clone();
                restore(trial);
                int rev = ApplySettings(trial, "restoring " + e.Setting);
                var r = Measure(rev, log.Title);
                log.After = r;
                log.GainPct = (_reference.MedianWorkMs - r.MedianWorkMs) / _reference.MedianWorkMs * 100;
                if (r.MedianWorkMs <= budget * 0.8 && r.P95FrameMs <= budget * 1.3)
                {
                    log.Decision = Decision.Kept; log.Reason = "Kept: quality restored and the target is still met (" + KnowledgeBase.F(r.MedianWorkMs) + " ms of " + KnowledgeBase.F(budget) + " ms).";
                    _current = trial; _reference = r;
                }
                else
                {
                    ApplySettings(snapshot, "rollback"); _current = snapshot;
                    log.Decision = Decision.Reverted; log.Reason = "Reverted: restoring it would break the frame budget (" + KnowledgeBase.F(r.MedianWorkMs) + " ms).";
                }
                rep.Experiments.Add(log);
                _stepsDone++;
                Publish(Phase.Testing, log.Title, log.Reason, false, log);
            }
        }

        static Action<SandboxSettings> RestoreSetting(string setting, SandboxSettings o)
        {
            switch (setting)
            {
                case "Anti-aliasing": return s => s.AntiAliasing = o.AntiAliasing;
                case "Water reflections": return s => s.WaterReflections = o.WaterReflections;
                case "Shadows": return s => s.Shadows = o.Shadows;
                case "Clouds": return s => s.Clouds = o.Clouds;
                case "Fog": return s => s.Fog = o.Fog;
                case "Particles": return s => s.Particles = o.Particles;
                case "Simulation distance": return s => s.SimulationDistance = o.SimulationDistance;
                case "Creature density": return s => s.EntityDensity = o.EntityDensity;
                case "Render distance": return s => s.RenderDistance = o.RenderDistance;
                case "Resolution scale": return s => s.ResolutionScale = o.ResolutionScale;
                case "Physics substeps": return s => s.PhysicsSubsteps = o.PhysicsSubsteps;
                default: return null;
            }
        }

        void Pacing(OptimisationReport rep, Goal goal)
        {
            int refresh = _hw.EffectiveRefreshHz;
            double sustained = 1000.0 / Math.Max(0.1, _reference.P95FrameMs);
            double workFps = 1000.0 / Math.Max(0.1, _reference.MedianWorkMs);
            if (goal == Goal.MaxFps)
            {
                rep.PacingNote = "No frame cap (Maximum FPS mode).";
                rep.NotTouched.Add("Frame-rate cap — Maximum FPS mode never caps.");
                return;
            }
            int cap = 0;
            string why;
            if (workFps >= refresh * 1.08 && sustained >= refresh * 0.9) { cap = refresh; why = "Your PC now finishes frames well within " + KnowledgeBase.F(1000.0 / refresh) + " ms, so locking to your " + refresh + " Hz display gives even frame pacing and frees CPU time (less heat and fan noise)."; }
            else if (goal == Goal.Smooth && workFps >= refresh / 2.0 * 1.1) { cap = refresh / 2; why = "Smoothness mode: " + refresh + " FPS isn't sustainable yet, so a steady " + (refresh / 2) + " FPS (an even divisor of the refresh rate) avoids judder."; }
            else
            {
                rep.PacingNote = "Not capped: " + refresh + " FPS isn't sustainable yet, and on a fixed-refresh display a cap that isn't a divisor of " + refresh + " Hz would judder.";
                rep.NotTouched.Add("Frame-rate cap — " + rep.PacingNote.Substring(13));
                return;
            }
            if (_current.FrameRateCap == cap) return;
            var log = new ExperimentLog { Index = rep.Experiments.Count + 1, Title = "Frame-rate cap: " + (_current.FrameRateCap == 0 ? "Unlimited" : _current.FrameRateCap.ToString()) + " → " + cap + " FPS", Setting = "Frame-rate cap", Kind = ChangeKind.Pacing, Hypothesis = why, Before = _reference };
            Publish(Phase.Pacing, "Tuning frame pacing", why, true);
            var snapshot = _current.Clone();
            var trial = _current.Clone();
            trial.FrameRateCap = cap;
            int rev = ApplySettings(trial, "frame cap");
            var r = Measure(rev, "Pacing");
            log.After = r;
            double devBefore = _reference.StdDevFrameMs, devAfter = r.StdDevFrameMs;
            log.PaceGainPct = devBefore > 0 ? (devBefore - devAfter) / devBefore * 100 : 0;
            if (r.AvgFps >= cap * 0.93 && devAfter <= devBefore * 1.05)
            {
                log.Decision = Decision.Kept;
                log.Reason = "Kept: frame-time variation " + KnowledgeBase.F(devBefore) + " → " + KnowledgeBase.F(devAfter) + " ms at a steady " + r.AvgFps.ToString("0") + " FPS.";
                _current = trial; _reference = r;
                rep.PacingNote = "Locked to " + cap + " FPS to match your " + refresh + " Hz display.";
            }
            else
            {
                ApplySettings(snapshot, "rollback"); _current = snapshot;
                log.Decision = Decision.Reverted;
                log.Reason = "Reverted: the cap didn't hold (" + r.AvgFps.ToString("0") + " FPS) or pacing didn't improve.";
                rep.PacingNote = "Left uncapped (a " + cap + " FPS cap didn't improve pacing).";
            }
            rep.Experiments.Add(log);
            _stepsDone++;
            Publish(Phase.Pacing, log.Title, log.Reason, false, log);
        }

        void RecordHardwareDecisions(OptimisationReport rep, bool memPressure)
        {
            var c = _hw.Cpu;
            int physical = c.Cores > 0 ? c.Cores : c.Logical;
            rep.HardwareDecisions.Add(physical + " cores / " + c.Logical + " threads → render threads are tested at " + physical + (c.Smt && c.Logical > physical ? ", then " + c.Logical + " to see whether SMT helps this game" : "") + ".");
            rep.HardwareDecisions.Add((_hw.Display.RefreshUnknown ? "Unknown refresh rate (assumed 60 Hz)" : _hw.Display.RefreshHz + " Hz display") + " → frame budget " + KnowledgeBase.F(1000.0 / rep.TargetFps) + " ms (" + rep.TargetFps.ToString("0") + " FPS target).");
            rep.HardwareDecisions.Add(memPressure ? "RAM under pressure → memory-saving options allowed." : OptimisationReport.Gb(_hw.Memory.TotalBytes) + " RAM, " + rep.BaselineMonitor.MemLoadPct.ToString("0") + "% used → memory isn't limiting; texture quality left alone.");
            foreach (var s in _hw.Storage)
                if (s.Role.Contains("Game")) rep.HardwareDecisions.Add("Game drive " + s.Drive + " is " + (s.MediaType == "Unknown" ? "an unknown drive type" : "an " + s.MediaType) + " → save hitches " + (s.MediaType == "HDD" ? "are longer on hard drives; " : "") + "fixed via autosave interval if observed.");
            if (_hw.Power.HasBattery) rep.HardwareDecisions.Add("Laptop " + (_hw.Power.OnAc ? "on AC power" : "on battery") + " → " + (_hw.Power.OnAc ? "full performance available." : "Windows limits speed; a frame cap also saves battery."));
            if (_hw.Class == PcClass.Entry) rep.HardwareDecisions.Add("Entry-level PC → the biggest wins come from removing wasted work (threads, LOD, ray-marched effects) before touching visible quality.");
        }

        // ================================================================= revert

        public void RevertAll()
        {
            if (_original == null) return;
            if (Running) { Cancel(); try { _thread.Join(8000); } catch { } }
            if (_link.Connected)
            {
                var s = _original.Clone();
                int rev = _link.WriteSettings(s, "GAPO Optimiser (reverted to original)");
                _link.WaitForRevision(rev, 3000);
                if (_priorityChanged) try { using (var gp = Process.GetProcessById(_link.Pid)) gp.PriorityClass = _originalPriority; _priorityChanged = false; } catch { }
                _link.Toast("GAPO reverted every change");
            }
            else
            {
                try { var s = _original.Clone(); s.ModifiedBy = "GAPO Optimiser (reverted to original)"; s.Revision++; s.Save(DemoPaths.SettingsFile); } catch { }
            }
            _current = _original.Clone();
        }

        public bool HasChanges { get { return _original != null && _current != null && _original.ToIni(false) != _current.ToIni(false); } }
    }
}
