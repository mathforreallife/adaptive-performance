# Demo engine algorithm brief (for porting the closed loop)

Sources read line by line: `demo/src/Optimiser/Engine/{Diagnosis,FrameStats,KnowledgeBase,OptimisationEngine,Report}.cs`.
Read for context: `Optimiser/Game/GameLink.cs`, `Optimiser/Monitoring/SystemMonitor.cs`, `Optimiser/Hardware/HardwareProfile.cs`, `Shared/Telemetry.cs`, `Shared/SandboxSettings.cs`, `Optimiser/Program.cs`, parts of `Sandbox/Game.cs`, `HardwareScanner.cs`, `UI/MainForm.cs`, and spec Parts I, III, §9-§12, Appendices A/B/D.

Everything below is from static reading. The demo was not run. Two behaviours were confirmed by executing the equivalent .NET expressions (marked "verified by execution"). Section 20 lists what could not be verified.

Namespace `Gapo.Optimiser.Engine`. Demo runtime: .NET Framework 4.6.2. Strings in `code font` inside quotes are verbatim product wording, `{x}` marks an interpolated value.

---

## 1. What the engine consumes

### 1.1 Collaborators (the port needs an equivalent of each call)

| Object | Members the engine uses | Notes |
|---|---|---|
| `GameLink` | `Connected`, `Pid`, `State`, `ReadSettings()`, `WriteSettings(settings, modifiedBy) -> int revision`, `WaitForRevision(rev, timeoutMs) -> bool`, `SendCommand(cmd, arg, timeoutMs = 2000) -> bool` (waits for ack), `FramesSince(gameTimeMs)`, `LastFrames(n)`, `LatestGameTimeMs`, `ObservedSeconds`, `Toast(text)` | Polls shared memory every 100 ms when connected (1000 ms when not). Frame store capped at 24,000 records (trimmed to 22,000). On connect it back-fills at most the last 600 frames. |
| `SystemMonitor` | `SetGame(pid)`, `ThreadSampling` (bool), `History(sinceMs)`, `Now` (ms, own stopwatch), `MarkSelf()`, `SelfCpuSinceMarkPct()`, `SelfCpuPeakPct` | 1 sample per 1000 ms, history of 180 samples. Hottest-thread sampling only while `ThreadSampling` is true, at intervals of at least 900 ms. |
| `ProcessSnapshot.Take(gamePid)` | `Processes[] {Pid, Name, CpuTicks, PrivateBytes}` | One `NtQuerySystemInformation(SystemProcessInformation)` call. |
| `HardwareProfile` | `Cpu.{Cores, Logical, Smt, MhzLimit, MaxMhz}`, `Display.{RefreshHz, RefreshUnknown}`, `EffectiveRefreshHz`, `Memory`, `Storage[]`, `Power`, `Os`, `PrimaryGpu`, `Class` | `EffectiveRefreshHz = (RefreshUnknown or RefreshHz < 24) ? 60 : RefreshHz`. `RefreshUnknown` is set when the reported rate is <= 1. |

### 1.2 Per-frame record fields used

`TimeMs` (frame END time on the game clock, ms since game start), `FrameMs` (present-to-present interval, includes cap/VSync wait), `WorkMs` (busy time: frame start to after present, excludes cap wait), stage times `SimMs, SimEntitiesMs, SimPhysicsMs, SimParticlesMs, RenderMs, TerrainMs, ShadowMs, WaterMs, SkyMs, FogMs, SpritesMs, PostMs, PresentMs, IoMs, WaitMs`, `Revision` (settings revision the frame was produced with), `Flags`, `EntitiesSimulated`, `EntitiesTotal`, `ParticlesActive`, `RenderThreads`, `RenderWidth`, `RenderHeight`.

`FrameFlags` (bit values): `Warmup = 1`, `Autosave = 2`, `GarbageCollect = 4`, `SettingsReload = 8`, `ManualCamera = 16`, `BenchmarkCamera = 32`, `TerrainEdit = 64`, `Calibrating = 128`. The engine reads only `Warmup`, `Autosave`, `GarbageCollect`.

### 1.3 Enums

- `Phase { Idle, Preparing, Baseline, Diagnosing, Testing, Pacing, Validating, Done, Failed, Cancelled }`
- `Goal { Balanced, MaxFps, Quality, Smooth }`; display names via `GoalName`: `Balanced`, `Maximum FPS`, `Quality`, `Smoothness`.
- `Decision { Kept, Reverted, Skipped, Failed }` (`Skipped` is never assigned; `Failed` only by the priority test).
- `ChangeKind { GameSetting, Windows, Hitch, Pacing, QualityRestore }`
- `Resource { MainThread, Render, Memory, Storage, Scheduling, Pacing }` (only `Memory` changes behaviour; the rest are labels).
- `Bottleneck { Unknown, Warmup, CpuMainThread, RenderSingleThread, RenderPixels, StorageHitch, Background, Memory, Pacing, TargetMet }` (only `Warmup`, `CpuMainThread`, `RenderSingleThread`, `RenderPixels`, `TargetMet` are ever assigned as `Primary`).

### 1.4 Threading

`Start(goal)` is a no-op if already running; otherwise it spawns one background thread named `GAPO engine` at `ThreadPriority.BelowNormal`. `Run` also sets the optimiser process to `ProcessPriorityClass.BelowNormal` for the run and restores the previous class in `Finish`. `Cancel()` sets a volatile flag. `Check()` is called at every wait step: cancel flag -> `OperationCanceledException`; `!link.Connected` -> `InvalidOperationException("The game was closed.")`.

---

## 2. Statistics (`FrameStats.Compute(IList<FrameRecord>)`)

`FrameStats` is not revision-aware. Callers filter the frame list first (section 2.3). With `n = 0` it returns an object with `Count = 0` and all zeros.

Let `ft[i] = FrameMs`, `work[i] = WorkMs`, `sorted` = ascending copy.

### 2.1 Formulas

| Field | Formula |
|---|---|
| `Count` | n |
| `DurationMs` | sum of `ft` (NOT last.TimeMs - first.TimeMs) |
| `MeanFrameMs` | `DurationMs / n` |
| `AvgFps` | `1000 / MeanFrameMs` |
| `StdDevFrameMs` | population standard deviation: `sqrt(sum((ft - mean)^2) / n)` |
| `Percentile(sorted, p)` | if n == 1 return the element; `idx = p * (n - 1)`; `lo = floor(idx)`; `hi = min(n - 1, lo + 1)`; `sorted[lo] + (sorted[hi] - sorted[lo]) * (idx - lo)` (linear interpolation, same as Excel PERCENTILE.INC / NumPy default) |
| `MedianFrameMs`, `P95FrameMs`, `P99FrameMs` | `Percentile(sorted ft, 0.5 / 0.95 / 0.99)` |
| `MaxFrameMs` | `sorted[n - 1]` |
| `MeanOfWorst(sorted, fraction)` | `k = max(1, ceil(n * fraction))`; mean of the k largest values |
| `Low1Fps` | `1000 / MeanOfWorst(sorted ft, 0.01)` |
| `Low01Fps` | `1000 / MeanOfWorst(sorted ft, 0.001)` |
| `MedianWorkMs` | `Percentile(sorted work, 0.5)`. This is the throughput metric every decision uses. |
| `MeanWorkMs` | mean of `work` (computed, never used) |
| Stutter threshold | `max(MedianFrameMs * 1.8, MedianFrameMs + 20.0)` |
| `Stutters` | count of frames where `ft > threshold` OR (`(Flags & (Autosave | Warmup)) != 0` AND `ft > MedianFrameMs + 15.0`) |
| `StuttersPerMin` | `Stutters / (DurationMs / 60000)`; 0 when duration is 0 |
| `AutosaveHitches` | count of frames with `Autosave` flag |
| `AutosaveHitchMs` | mean of `IoMs` over the `Autosave`-flagged frames (0 if none) |
| `WarmupFrames`, `GcFrames` | counts of flagged frames (`GcFrames` is never used) |
| Stage costs `Sim, SimEntities, SimPhysics, SimParticles, Render, Terrain, Shadow, Water, Sky, Fog, Sprites, Post, Present, Io, Wait` | each is the independent median (`Percentile(.., 0.5)`) of that field across the frames. Medians are used "so one hitch cannot skew them". They do not sum to `MedianWorkMs`. |
| `EntitiesSimulated`, `EntitiesTotal`, `Particles` | medians of the counters |
| `RenderThreads`, `RenderW`, `RenderH`, `Revision` | taken from the LAST frame in the list |
| `PixelWork` (property) | `Shadow + Water + Sky + Fog + Sprites + Terrain * 0.5` |

### 2.2 Statistical weight of one window

A window is about 2.6 s. At 39 FPS that is about 100 frames: `ceil(100 * 0.01) = 1`, so `Low1Fps = Low01Fps = 1000 / MaxFrameMs`. At 150 frames the 1 % low is the mean of 2 frames. The 0.1 % low is the single worst frame for any window under 1,001 frames. That is why decisions use `MedianWorkMs` and `P95FrameMs`, and the 1 % low only as a guarded veto (section 10.3).

### 2.3 Per-revision handling (done by callers)

| Caller | Filter applied before `Compute` |
|---|---|
| `MeasureFrames` (every measurement window) | drop frames with `Revision != expectedRevision` or `Warmup` flag |
| `rep.BaselineHitches` | frames with `TimeMs >= Latest - 60000`, drop `Revision != _current.Revision` or `Warmup` flag |
| `rep.FinalHitches` | frames with `TimeMs >= Latest - 60000`, drop `Revision != _current.Revision` (no warm-up filter) |
| Hitch-fix verification (`log.After`) | all frames since the fix was applied, NO revision filter (mixes configurations) |

The rule the spec takes from this (I.5 #6): never compute a stutter threshold over frames from different settings, because the threshold is relative to the median.

---

## 3. Measurement window lifecycle

Constants: `DiscardMs = 650`, `WindowMs = 2600`, `MinFrames = 45`, `MaxWindowMs = 7000`.

`MeasureFrames(expectedRevision, label)` (the `label` argument is unused):

1. `Check()`.
2. `SendCommand(RestartBenchmarkPath, 0)` (2000 ms ack timeout). On failure throw `"The game stopped responding."`. The sandbox resets its benchmark flight time to 0; the flight loops every 12 s.
3. `start = link.LatestGameTimeMs + DiscardMs` (game clock). Frames ending before `start` are discarded as settling frames. Because `LatestGameTimeMs` is up to one 100 ms poll stale, the real discard after the restart is roughly 550-650 ms.
4. `monitor.MarkSelf()` (start of the self-overhead span).
5. Loop: `Thread.Sleep(200)`; `Check()`; `frames = FramesSince(start)` minus wrong-revision and warm-up frames; `span = last.TimeMs - first.TimeMs` (0 if fewer than 2 frames). Exit when `(span >= WindowMs && frames.Count >= MinFrames)` or wall time `> MaxWindowMs + DiscardMs` (7650 ms).
6. If `frames.Count < 8` throw `"Too few frames were measured ({count})."`.
7. Accumulate `_selfInWindows += monitor.SelfCpuSinceMarkPct()`, `_selfSamples++`, `_windows++`, `_frames += frames.Count`.

Consequences: a window is a span of at least 2.6 s and at least 45 frames, quantised by the 200 ms loop and the 100 ms link poll (about 2.6-2.9 s in practice). Below about 17 FPS the 45-frame minimum stretches the window. A window that hits the 7.65 s timeout is accepted silently if it holds 8 or more frames. Full cost per window including discard is about 3.3-3.6 s.

`Measure(rev, label)` = `FrameStats.Compute(MeasureFrames(rev, label))` (one window).

`MeasurePair(label, out a, out b, trace)`: two back-to-back windows A and B on the current revision, each with its own path restart and discard. `a`, `b` are the per-window stats; the return value is `Compute` over the concatenation of both frame lists. If `trace` is given it receives window A's `FrameMs` values only.

`ApplySettings(settings, note)`: `rev = link.WriteSettings(settings, "GAPO Optimiser" + (note != null ? " (" + note + ")" : ""))`; `WaitForRevision(rev, 4000)` (30 ms poll) else throw `"The game did not apply the new settings."`; then `settings.Revision = rev`. `WriteSettings` computes `rev = max(file revision, game's applied revision) + 1`, writes `path + ".tmp"` then deletes the target and moves the temp file over it. The sandbox checks the file timestamp every 200 ms and publishes the applied revision.

Warm-up wait `WaitForWarmup()` (before anything is measured): loop { `Check()`; `recent = LastFrames(90)`; `warming = State != Running || recent.Count < 30 || any frame in recent has Warmup flag`; if not warming return; publish status (section 15); if elapsed `> 45000 ms` return anyway; `Sleep(250)` }.

---

## 4. Run sequence

`Run(goal)`, in order. Every step that waits calls `Check()`.

1. Create `OptimisationReport { Started = now, Goal, Hardware }`. Lower own process priority. Record own CPU time and start a wall stopwatch. Reset counters (`_hitchFixAt = -1`, windows, frames, self-overhead sums).
2. **Preparing.** `monitor.SetGame(link.Pid)`. `_original = link.ReadSettings()`; null -> throw `"Could not read the game's settings file."`. `_current = clone`, `rep.OriginalSettings = clone`. Read the game's process priority into `_originalPriority` (stays `Normal` if the read fails). `WaitForWarmup()`.
3. **Target.** `refresh = hw.EffectiveRefreshHz`. `TargetFps = (goal == MaxFps) ? max(refresh, 144) : min(refresh, 144)`. `TargetReason = (goal == MaxFps) ? "as high as possible" : (RefreshUnknown ? "refresh rate unknown, assumed 60 Hz" : "your " + refresh + " Hz display")`. `budget = 1000 / TargetFps` ms.
4. Switch the game to the benchmark camera: `SendCommand(BenchmarkCamera, 1)`, failure -> throw `"The game did not respond to the benchmark command."`. Toast `"GAPO is measuring — benchmark flight"`.
5. **Baseline (A/A)**: section 5.
6. **Diagnosing**: section 6.
7. **Hitch fix first**: section 7.
8. **Background contention / priority test**: section 8.
9. **Experiment loop**: sections 9-10.
10. **Quality give-back** (Balanced and Quality goals only): section 11.
11. **Pacing**: section 12.
12. **Validating** and completion: section 13.

Window count for a run = 2 (baseline) + 0/1 (priority) + number of setting experiments + number of give-back tests + 0/1 (pacing) + 2 (final). The hitch fix uses no window. Spec run #6: 2 + 3 + 1 + 2 = 8 windows, 31 s.

---

## 5. A/A noise floor

```
baseline = MeasurePair("Baseline", out a, out b, rep.BaselineTrace)
raw      = |a.MedianWorkMs - b.MedianWorkMs| / max(0.01, (a.MedianWorkMs + b.MedianWorkMs) / 2)
_noise   = max(0.025, min(0.10, raw * 2.5))
rep.NoiseFloorPct = _noise * 100
_reference = rep.Baseline = baseline        // stats of A and B combined
```

- `_noise` is a relative fraction of median busy time. Field default before measurement is `0.03`.
- Multiplier 2.5, clamp to [2.5 %, 10 %].
- `_reference` is the "current best configuration" stats object. It is replaced whenever a change is kept.

---

## 6. Diagnose step

### 6.1 Evidence gathering (between windows, never inside one)

1. Publish Diagnosing status. `monitor.ThreadSampling = true`.
2. `snapA = ProcessSnapshot.Take(pid)`; `SleepChecked(2300)` (50 ms steps); `snapB = ProcessSnapshot.Take(pid)`; `ThreadSampling = false`.
3. `rep.BaselineMonitor = MonitorSummary.From(monitor.History(monitor.Now - 9000))` (last 9 s of 1 Hz samples: covers the two baseline windows plus the 2.3 s snapshot span).
4. `rep.BackgroundApps = Advisor.Contention(snapA, snapB, elapsedSeconds, gamePid, selfPid)` (section 17).
5. `EnsureHitchObservation()`: while `link.ObservedSeconds < 14` and waited `< 15000 ms`: publish "Watching for stutter" status, `SleepChecked(500)`. `ObservedSeconds` is the time span of the whole frame store.
6. `rep.BaselineHitches` = stats over the last 60 s of frames with the current revision and no warm-up flag.
7. `memPressure = rep.BaselineMonitor.MemLoadPct >= 88`.
8. `diag = Diagnosis.Run(_reference, rep.BaselineMonitor, hw, _current, rep.TargetFps, rep.BaselineHitches)`; stored as `rep.BaselineDiagnosis`.
9. `rep.NotTouched.AddRange(KnowledgeBase.NotWorthTouching(_current, _reference, memPressure))` (section 9.4).
10. `RecordHardwareDecisions(rep, memPressure)` (section 16.3).
11. Publish `(Phase.Diagnosing, diag.Title, diag.Summary)` with the diagnosis attached, then `SleepChecked(900)` so the user can read it.

### 6.2 `MonitorSummary.From(samples)`

Averages over the samples: `TotalCpuPct`, `GameCpuCores`, `BackgroundCpuPct`, `SelfCpuPct`, `MemLoadPct`, `CommitPct`, per-core `CoreUsage[]` (0..1). Maximum: `HottestThreadPct` (initial -1 = not sampled), `WriteMBpsPeak`. Minimum: `AvailBytesMin`. Last sample: `SelfPrivateBytes`, `GamePrivateBytes`. `BusiestCore = max(CoreUsage) * 100`. Empty input -> all zeros, `AvailBytesMin = 0`, `HottestThreadPct = -1`.

Sample definitions (from `SystemMonitor`): `TotalCpuPct` = mean per-core busy share x 100; `GameCpuPct` = game CPU time / wall / logical count x 100; `SelfCpuPct` likewise for the optimiser; `BackgroundCpuPct = max(0, TotalCpuPct - GameCpuPct - SelfCpuPct)` (share of the whole machine); hottest thread = largest per-thread CPU delta as % of one core, capped at 100.

### 6.3 `Diagnosis.Run(st, mon, hw, cur, targetFps, hitchWindow)`

Setup: `BudgetMs = 1000 / targetFps`; `WorkMs = st.MedianWorkMs`; `RequiredSavingMs = max(0, WorkMs - BudgetMs)`; `work = max(0.01, st.MedianWorkMs)`; `other = max(0, work - st.Sim - st.Render - st.Present - st.Io)`; `logical = max(1, hw.Cpu.Logical)`.

Stage list (name, ms, share = ms / work), in this order:

1. `"Simulation (main thread)"` = `st.Sim`
2. `"Rendering"` = `st.Render`
3. `"Present to screen"` = `st.Present`
4. `"Streaming & saving"` = `st.Io`, only added when `st.Io > 0.05`
5. `"Other"` = `other`

Primary classification, first match wins:

| # | Condition | `Primary` | `Title` | `Summary` | `Category` |
|---|---|---|---|---|---|
| 1 | `st.WarmupFrames > st.Count * 0.2` (returns immediately, no evidence) | `Warmup` | `Game is still loading (warm-up)` | `The game is streaming world data. Judging performance now would be misleading, so GAPO waits until it settles.` | `Temporary` |
| 2 | `st.Render >= st.Sim` and `st.RenderThreads <= 1` and `logical > 1` | `RenderSingleThread` | `Rendering is stuck on one CPU thread` | `Rendering takes {F(Render)} ms of each {F(work)} ms frame, all on 1 of your {logical} CPU threads{OtherCoresNote}.` | `B · game setting (render threads)` |
| 3 | `st.Render >= st.Sim` otherwise | `RenderPixels` | `Rendering workload (pixels & effects)` | `Rendering takes {F(Render)} ms of each {F(work)} ms frame. Most expensive: {top 3 passes as "name X ms", comma separated}.` | `B · game settings` |
| 4 | `st.Render < st.Sim` | `CpuMainThread` | `CPU main thread (simulation)` | `The simulation takes {F(Sim)} ms of each {F(work)} ms frame ({(int)EntitiesSimulated} creatures simulated, physics {F(SimPhysics)} ms).` | `C · engine limitation, mitigated by B · game settings` |

Case 3 pass names (sorted by median ms descending, top 3): `shadows` (Shadow), `terrain` (Terrain), `water` (Water), `sky & clouds` (Sky), `fog` (Fog), `creatures & particles` (Sprites), `anti-aliasing` (Post).

Case 4 also sets `EngineLimitation` = `This is a main-thread limitation. External settings cannot make the game simulation multithreaded. Reducing simulation distance is likely to provide the largest available improvement.`

`OtherCoresNote(mon)` (every clause is conditional on its own data, spec I.5 #11):
- `""` if `mon.CoreUsage.Length < 2` or `mon.TotalCpuPct < 0.5`.
- `others` = mean of all per-core averages except the highest, x 100.
- `others < 35` -> `, while the other cores are mostly idle ({others:0}% on average)`
- else if `mon.BackgroundCpuPct >= 15` -> `; the other cores are busy with background apps ({BackgroundCpuPct:0}% of the CPU)`
- else `""`.

Evidence sentences, appended in this order:

| Condition | Sentence |
|---|---|
| `CoreUsage.Length > 1 && TotalCpuPct > 0.5 && BusiestCore - TotalCpuPct >= 25` | `Total CPU use is {TotalCpuPct:0}%, but the busiest core runs at {BusiestCore:0}% — overall CPU use hides a one-thread limit.` |
| `CoreUsage.Length > 1 && TotalCpuPct > 0.5` | `Per-core load: core 0 {u0:0}% · core 1 {u1:0}% · ...` (separator is space, middle dot, space) |
| `HottestThreadPct >= 0` | `The game's busiest thread uses {HottestThreadPct:0}% of one core (measured from outside the game via thread CPU times).` |
| always | `Frame work {F(work)} ms vs a {F(BudgetMs)} ms budget for {targetFps:0} FPS.` |

Secondary problems, in this order (`hw2 = hitchWindow ?? st`):

| Condition | Sentence |
|---|---|
| `hw2.AutosaveHitches > 0` | `Storage hitch: a blocking autosave freezes the game for about {AutosaveHitchMs:0} ms, {AutosaveHitches}× in the last {DurationMs/1000:0} s (disk writes ~4 MB each).` |
| `mon.BackgroundCpuPct >= 15` | `Background apps use {BackgroundCpuPct:0}% of the CPU while you play.` |
| `mon.MemLoadPct >= 88` or (`0 < AvailBytesMin < 700 * 1024 * 1024`) | `Memory pressure: {MemLoadPct:0}% of RAM in use.` |
| `st.P99FrameMs > st.MedianFrameMs * 2.2` and `hw2.AutosaveHitches == 0` | `Uneven frame pacing: the slowest 1% of frames take {F(P99FrameMs)} ms vs a typical {F(MedianFrameMs)} ms.` |
| `MhzLimit > 0 && MaxMhz > 0 && MhzLimit < MaxMhz * 0.8` | `Windows reports the CPU clock limited to {MhzLimit} of {MaxMhz} MHz (power or thermal limit).` |

Target-met override (last step): if `work <= BudgetMs * 0.92` and `hw2.AutosaveHitches == 0` then `Primary = TargetMet`, `Title = "Target reached"`, `Summary = "Each frame takes {F(work)} ms of work — inside the {F(BudgetMs)} ms budget for {targetFps:0} FPS."` `Category`, `EngineLimitation`, evidence and secondary items from the earlier branch are left in place.

`ShortName(Bottleneck)`: `CPU main thread`, `Single-threaded rendering`, `Rendering workload`, `Storage hitches`, `Background apps`, `Memory pressure`, `Frame pacing`, `Warm-up`, `Target reached`, default `Unknown`.

Stage colours (demo palette, do not carry over): Sim `0xF472B6`, Render `0x22D3EE`, Present `0xA78BFA`, Io `0xFBBF24`, Other `0x64748B`.

**The diagnosis does not select candidates.** It is explanatory output only. Candidate order comes entirely from the knowledge-base predictions (section 9), which are recomputed from the latest reference stage times after every kept change. That recomputation, plus a fresh `Diagnosis.Run` for the "Bottleneck now" label, is how "re-diagnose after each commit" is implemented.

---

## 7. Hitch detection and hitch-first logic

Detection is flag-based: a frame carries `Autosave` when the sandbox performed a blocking save inside it; `IoMs` holds the save duration. No timing-pattern detection exists in the demo.

Trigger: `rep.BaselineHitches.AutosaveHitches > 0 && _current.AutosaveSeconds > 0 && _current.AutosaveSeconds < 120`.

Candidate: `Setting = "Autosave interval"`, `From = "{AutosaveSeconds} s"`, `To = "5 min"`, `Kind = Hitch`, `Resource = Storage`, `VisualLoss = 0`, `PredictedMs = 0`, `Apply: AutosaveSeconds = 300`.
Hypothesis: `Every {AutosaveSeconds} s the game saves the world on its main thread and waits for the disk, freezing for ~{AutosaveHitchMs:0} ms. Saving every 5 minutes removes almost all of those freezes.`

`ApplyHitchFix`: log `{Index, Title, Setting, Hypothesis, Kind, Before = rep.BaselineHitches}`. Publish `("Fixing: " + Title, Hypothesis)`. Clone, apply, `ApplySettings(trial, "autosave")`, `_current = trial`, `_hitchFixAt = link.LatestGameTimeMs`. `Decision = Kept` unconditionally, with no measurement window. Reason: `Kept: removes a repeating {AutosaveHitchMs:0} ms freeze with no visual cost. Done first because random hitches would otherwise pollute every later measurement.` Publish `("Kept: " + Title, Reason)`.

`VerifyHitchFix` (runs during validation): find the first experiment with `Kind == Hitch`; `since = FramesSince(_hitchFixAt)`; skip if fewer than 2 frames; `secs = (last.TimeMs - first.TimeMs) / 1000`; `saves` = count of `Autosave`-flagged frames; `log.After = Compute(since)`. Append to the reason:
- `saves == 0`: ` Verified: no autosave freezes in the {secs:0} s since (there were {BaselineHitches.AutosaveHitches} in the {BaselineHitches.DurationMs/1000:0} s before).`
- else: ` Check: {saves} save(s) still happened in the {secs:0} s since the change.`

The decision stays `Kept` either way.

---

## 8. Background contention and the priority test

Decision after the hitch fix:

| Condition | Action |
|---|---|
| `BaselineMonitor.BackgroundCpuPct >= 15` and `_originalPriority` is `Normal`, `BelowNormal` or `Idle` | `TestPriority(rep)` |
| `BackgroundCpuPct < 15` | NotTouched: `Process priority — background apps used only {BackgroundCpuPct:0}% CPU, so raising the game's priority has nothing to win (and random priority changes are a classic 'booster' mistake).` |
| otherwise (already above normal) | NotTouched: `Process priority — the game already runs at {_originalPriority} priority; GAPO doesn't raise it further.` |

`TestPriority`:
- `target = (_originalPriority == Normal) ? AboveNormal : Normal`. Never higher than Above normal.
- Log: `Title = "Game process priority: {PrioName(original)} → {PrioName(target)}"`, `Setting = "Process priority"`, `Kind = Windows`, `Before = _reference`.
- Hypothesis: `Background apps use {BackgroundCpuPct:0}% of the CPU` + (target is Normal ? ` and the game was started at {PrioName(original)} priority, so it loses every contest for the CPU. Restoring normal priority should help.` : `. Giving the game a slightly higher priority lets it win those contests.`)
- Publish `("Testing: " + Title, Hypothesis, measuring = true)`. Set the game's `PriorityClass = target`; `_priorityChanged = true`; `r = Measure(_current.Revision, "Priority")`; `gain = (_reference.MedianWorkMs - r.MedianWorkMs) / _reference.MedianWorkMs`.
- `gain > _noise` -> Kept, `Reason = "Kept: {Signed(gain%)}% faster. Resets automatically when the game closes."`, `_reference = r`.
- else restore `_originalPriority`, `_priorityChanged = false`, Reverted, `Reason = "Reverted: no measurable benefit ({Signed(gain%)}%)."`
- Any exception except cancellation: `Decision = Failed`, `Reason = "Windows refused the change ({ex.Message})."`
- `PrioName`: `AboveNormal -> "Above normal"`, `BelowNormal -> "Below normal"`, others `ToString()`.

---

## 9. Knowledge base (`KnowledgeBase`)

Constants: `GameName = "GAPO Sandbox"`, `EngineName = "GAPO voxel engine (software renderer)"`.

`Profile` (shown to the user as what GAPO knows about this game):
1. `Simulation (creature AI + physics) runs on one main thread — engine limitation`
2. `Rendering can be spread over several threads (Render threads setting)`
3. `Ultra shadows are ray-marched per pixel: very expensive, looks almost the same as High`
4. `Resolution and anti-aliasing multiply all pixel work`
5. `Texture quality only changes memory use, not frame time`
6. `Autosave blocks the main thread while it writes to disk (storage hitch)`
7. `Streams world chunks for a few seconds after start-up (temporary stutter)`

### 9.1 Candidate model

`Candidate { Setting, From, To, Kind, Resource, VisualLoss (0 invisible .. 10 very visible), PredictedMs (predicted saving in frame work), Hypothesis, Apply(settings) }`

- `Key = Setting + "→" + To`
- `Title = Setting + ": " + From + " → " + To`
- `Value = PredictedMs / (VisualLoss + 0.6)` (the ordering metric)
- `Make(...)` clamps `PredictedMs` to `max(0, predicted)` and sets `Kind = GameSetting`. The two render-thread candidates are built without `Make`, so their prediction is not clamped.
- `F(ms)` = `ms < 10 ? ms.ToString("0.0") : ms.ToString("0")` (current culture).

### 9.2 Ladders: `Generate(cur, st, hw, memoryPressure)`

Each call emits at most one candidate per setting: the next single step down from the current value. `st` is the current reference stats. `physical = max(1, Cores > 0 ? Cores : Logical)`, `logical = max(1, Logical)`, `simCore = st.SimEntities + st.SimPhysics`. Emission order is the row order below (it decides ties, because the sort is stable).

| # | Setting | Condition | From -> To | Resource | VisualLoss | PredictedMs |
|---|---|---|---|---|---|---|
| 1a | `Render threads` | `cur.RenderThreads < min(physical, 16) && logical > 1` | cur -> `to = min(physical, 16)` (if `to == 1` then `min(logical, 2)`; unreachable in practice) | Render | 0 | `st.Render * (1 - cur.RenderThreads / (to * eff))`, `eff = to >= 4 ? 0.8 : 0.85` |
| 1b | `Render threads` | else if `Smt && logical > physical && cur.RenderThreads == physical` | physical -> logical | Render | 0 | `st.Render * 0.12` |
| 2 | `Terrain LOD` | `!cur.TerrainLod` | Off -> On | Render | 1 | `st.Terrain * 0.55` |
| 3a | `Shadows` | Ultra | Ultra -> High | Render | 1 | `st.Shadow * 0.88` |
| 3b | `Shadows` | High | High -> Off | Render | 5 | `st.Shadow * 0.95` |
| 4a | `Anti-aliasing` | SSAA4x | `4× SSAA` -> `FXAA` | Render | 2 | `st.Terrain * 0.5 + (Shadow + Water + Sky + Fog + Sprites) * 0.75 + st.Post * 0.4` |
| 4b | `Anti-aliasing` | FXAA | FXAA -> Off | Render | 2 | `st.Post * 0.9` |
| 5a | `Water reflections` | Ultra | Ultra -> Simple | Render | 2 | `st.Water * 0.85` |
| 5b | `Water reflections` | Simple | Simple -> Off | Render | 3 | `st.Water * 0.9` |
| 6a | `Clouds` | Ultra | Ultra -> Low | Render | 1 | `st.Sky * 0.75` |
| 6b | `Clouds` | Low | Low -> Off | Render | 3 | `st.Sky * 0.5` |
| 7a | `Fog` | Volumetric | Volumetric -> Simple | Render | 1 | `st.Fog * 0.7` |
| 7b | `Fog` | Simple | Simple -> Off | Render | 4 | `st.Fog` |
| 8 | `Particles` | not Off | level n -> n-1 over `Off, Low, Medium, High, Ultra` | Render | from >= 3 (High/Ultra): 1; from == 2: 2; from == 1: 3 | `(st.SimParticles + st.Sprites * 0.6) * (1 - count(to) / count(from))`, counts `{0, 700, 2000, 4500, 9000}` |
| 9a | `Simulation distance` | Unlimited and `st.EntitiesSimulated > 0` | Unlimited -> Far | MainThread | 1 | `simCore * 0.65` |
| 9b | `Simulation distance` | Far and `st.EntitiesSimulated > 0` | Far -> Near | MainThread | 2 | `simCore * 0.6` |
| 10 | `Physics substeps` | `cur > 1 && st.SimPhysics > 0.05` | cur -> `to = cur > 4 ? 4 : max(1, cur / 2)` (8 -> 4 -> 2 -> 1) | MainThread | to >= 4: 0; to >= 2: 1; else 2 | `st.SimPhysics * (1 - to / cur)` |
| 11 | `Creature density` | `cur > 50 && st.EntitiesTotal > 0` | `{cur}%` -> `to = cur > 75 ? 75 : 50` `%` | MainThread | to == 75: 3; else 4 | `simCore * (1 - r^2) + st.Sprites * 0.2`, `r = to / cur` |
| 12 | `Render distance` | `cur > 700` | cur -> `to = cur > 1150 ? 1100 : (cur > 900 ? 850 : 650)` | Render | to >= 1100: 2; to >= 850: 3; else 4 | `st.Terrain * (1 - to / cur) * (cur.TerrainLod ? 0.45 : 1.0)` |
| 13 | `Resolution scale` | `cur > 70` | `{cur}%` -> `to = cur > 85 ? 85 : 70` `%` | Render | to == 85: 3; else 5 | `st.PixelWork * (1 - r^2) + st.Post * (1 - r^2)`, `r = to / cur` |
| 14 | `Textures` | `memoryPressure && Textures == Ultra` | Ultra -> High | Memory | 2 | 0 |

Not in any ladder: `VSync`, `FrameRateCap` (handled by pacing), `AutosaveSeconds` (handled by the hitch fix), Textures High -> Low.

### 9.3 Hypothesis strings

| Row | Hypothesis |
|---|---|
| 1a | `Rendering runs on {cur} thread{s} but your CPU has {physical} cores. The render passes are parallel work, so spreading them over {to} threads should cut {F(Render)} ms of rendering substantially.` (`s` omitted when cur == 1) |
| 1b | `Your CPU runs 2 threads per core (SMT/Hyper-Threading). Whether that helps is game-specific, so it has to be measured, not assumed.` |
| 2 | `Terrain is sampled at full detail all the way to the horizon ({F(Terrain)} ms). Distant terrain covers few pixels, so coarser sampling there is nearly invisible.` |
| 3a | `Ultra shadows ray-march every pixel every frame ({F(Shadow)} ms). High uses a baked shadow map that looks almost identical.` |
| 3b | `Shadows cost {F(Shadow)} ms. Turning them off saves that but flattens the terrain visibly.` |
| 4a | `4× supersampling renders four times the pixels ({RenderW*2}×{RenderH*2}) and averages them. FXAA smooths edges in one cheap pass.` |
| 4b | `FXAA costs {F(Post)} ms; turning it off leaves jagged edges.` |
| 5a | `Ultra reflections ray-march the terrain for every water pixel ({F(Water)} ms). Simple reflects the sky only.` |
| 5b | `Water shading costs {F(Water)} ms.` |
| 6a | `Ultra clouds compute 5 octaves of noise per sky pixel every frame ({F(Sky)} ms); Low samples a pre-baked cloud texture that looks the same at this resolution.` |
| 6b | `Sky and clouds cost {F(Sky)} ms.` |
| 7a | `Volumetric fog marches every pixel's view ray ({F(Fog)} ms). Simple distance fog is one lookup per pixel.` |
| 7b | `Fog costs {F(Fog)} ms but hides the edge of the render distance.` |
| 8 | `Blossom particles cost {F(SimParticles + Sprites*0.6)} ms (update + drawing).` |
| 9a | `All {(int)EntitiesSimulated} creatures are simulated every frame, even far out of sight. Their AI is all-pairs (O(n²)), so simulating only the ones within range cuts the cost disproportionately.` |
| 9b | `{(int)EntitiesSimulated} creatures are still simulated. Near only animates the ones close to the camera.` |
| 10 | `Collision is solved {cur} times per frame ({F(SimPhysics)} ms). Slow-moving creatures don't need more than {to}.` |
| 11 | `Fewer creatures means quadratically less AI work, but the world looks emptier.` |
| 12 | `Terrain work grows with view distance ({F(Terrain)} ms).` |
| 13 | `Pixel work scales with the square of the resolution.` |
| 14 | `System memory is under pressure; High textures use 12 MB less.` |

### 9.4 `NotWorthTouching(cur, st, memoryPressure)`

- `!memoryPressure && cur.Textures != Low`: `Texture quality — memory isn't under pressure, and textures don't cost frame time in this game. Lowering them would cost visuals for no gain.`
- `0 < st.Present < 3`: `Window/present mode — copying the frame to the screen takes only {F(Present)} ms.`

---

## 10. Experiment loop

### 10.1 Candidate ordering and stop rules

State: `tried` (set of `Key`), `blocked` (set of `Setting`), `budget = 1000 / rep.TargetFps`.

Each iteration:

1. `Check()`.
2. `cands = Generate(_current, _reference, hw, memPressure)` filtered by: `Key` not in `tried`, `Setting` not in `blocked`, and (`Resource == Memory` or `PredictedMs >= max(0.25, _reference.MedianWorkMs * 0.025)`). Sort by `Value` descending (stable).
3. `_experimentsLeft = min(6, count(c => goal == MaxFps || c.VisualLoss <= 2)) + 1` (progress estimate only).
4. If `cands` is empty: stop.
5. `next = cands[0]`.
6. `targetMet = _reference.MedianWorkMs <= budget * 0.88 && _reference.P95FrameMs <= budget * 1.25`.
7. **Stop at target:** if `goal != MaxFps && targetMet && next.VisualLoss > 0`: add NotTouched `Stopped lowering visuals: the {TargetFps:0} FPS target is met with headroom ({F(MedianWorkMs)} ms of {F(budget)} ms). Remaining options would only cost quality: {titles of up to 4 remaining candidates with VisualLoss > 0, comma separated}.` and stop.
8. **Quality guard:** if `goal == Quality && next.VisualLoss >= 4 && !targetMet`: add NotTouched `Quality mode: not trying {next.Title} (visual cost {VisualLoss}/10).`, add `next.Key` to `tried`, continue.
9. Add `next.Key` to `tried`; `log = RunExperiment(rep, next, goal)`; if `log.Decision != Kept` add `next.Setting` to `blocked` (that setting is not tried again in this run).

Maximum FPS never stops at the target: it runs until no candidate passes the filters.

### 10.2 One experiment (`RunExperiment`)

1. Log `{Index = Experiments.Count + 1, Title, Setting, Hypothesis, Kind, VisualLoss, PredictedMs, Before = _reference}`.
2. Publish `(Testing, "Testing: " + Title, Hypothesis + " Predicted saving ≈ " + F(PredictedMs) + " ms.", measuring = true)`. Toast `GAPO testing: {Title}`.
3. Transaction: `snapshot = _current.Clone()`; `trial = _current.Clone()`; `c.Apply(trial)`; `rev = ApplySettings(trial, "testing " + Setting)`; `result = Measure(rev, Title)`; `log.After = result`.
4. Metrics:
   - `gain = (ref.MedianWorkMs - result.MedianWorkMs) / ref.MedianWorkMs`
   - `pace = (ref.P95FrameMs - result.P95FrameMs) / ref.P95FrameMs`
   - `lowDrop = (ref.Low1Fps - result.Low1Fps) / max(0.01, ref.Low1Fps)`
   - `log.GainPct = gain * 100`, `log.PaceGainPct = pace * 100`
   - `score = wPerf * gain + wPace * pace - wVis * VisualLoss`
   - `memOnly = (Resource == Memory)`
5. Decision (section 10.3).
6. Kept: `_current = trial`; `_reference = result`; `d = Diagnosis.Run(_reference, MonitorSummary.From(monitor.History(Now - 4000)), hw, _current, TargetFps, null)`; `log.BottleneckAfter = d.Title`; publish `("Kept: " + Title, Reason)` with `d`.
7. Reverted: `ApplySettings(snapshot, "rollback")`; `_current = snapshot`; publish `("Reverted: " + Title, Reason)`. `_reference` is unchanged. The rollback gets a new revision number.
8. `log.Seconds` = wall time of the experiment; `log.SelfCpuPct = monitor.SelfCpuSinceMarkPct()`; add to `rep.Experiments`; `_experimentsDone++`.

### 10.3 Keep / rollback rules (first match wins)

| # | Condition | Decision | Reason string |
|---|---|---|---|
| 1 | `!memOnly && gain <= _noise` | Reverted | `Reverted: {Signed(gain%)}% is within the ±{noise%:0.0}% measurement noise — no proven benefit` + (VisualLoss > 0 ? `, so not worth any visual cost.` : `.`) |
| 2 | `!memOnly && lowDrop > max(0.12, _noise * 3) && result.Low1Fps < ref.Low1Fps` | Reverted | `Reverted: average speed improved but 1% lows got worse ({ref.Low1Fps:0} → {result.Low1Fps:0} FPS). Smoothness matters more than averages.` |
| 3 | `!memOnly && score <= 0` | Reverted | `Reverted: {Signed(gain%)}% faster isn't worth a visual cost of {VisualLoss}/10 in {GoalName(goal)} mode.` |
| 4 | otherwise | Kept | memOnly: `Kept: relieves memory pressure.` Otherwise: `Kept: frame work {F(ref work)} → {F(result work)} ms ({Signed(gain%)}%, above the ±{noise%:0.0}% noise floor)` + (VisualLoss == 0 ? ` with no visual cost.` : VisualLoss <= 2 ? ` for a small visual cost.` : ` — a visible trade-off, but worth it for this goal.`) |

### 10.4 Goal weight sets

| Goal | `wPerf` | `wPace` | `wVis` (per visual-loss point) | Break-even gain per visual point (pace = 0) |
|---|---:|---:|---:|---:|
| Balanced (default) | 1.0 | 0.5 | 0.022 | 2.2 % |
| Maximum FPS (`MaxFps`) | 1.0 | 0.3 | 0.004 | 0.4 % |
| Quality | 1.0 | 0.5 | 0.05 | 5 % |
| Smoothness (`Smooth`) | 0.6 | 1.2 | 0.02 | 3.3 % |

Other goal differences: target FPS (section 4 step 3); stop-at-target and Quality guard (10.1); give-back only for Balanced and Quality (11); Maximum FPS never caps, Smoothness may cap at half refresh (12).

UI goal descriptions (`MainForm.GoalDescription`): Balanced `Most performance for the least visual cost; stops lowering quality once the target is met.` Maximum FPS `Highest frame rate. Accepts bigger visual trade-offs and never caps FPS.` Quality `Keeps visuals high: only low-impact changes, just enough to reach the target.` Smoothness `Favours steady frame times and strong 1% lows; caps FPS when that helps.`

---

## 11. Quality give-back (`QualityGiveBack(rep, budget)`)

Runs only when `goal == Balanced || goal == Quality`.

1. If `_reference.MedianWorkMs > budget * 0.6` return (needs at least 40 % headroom).
2. `kept` = experiments with `Decision == Kept && Kind == GameSetting && VisualLoss >= 2`, ordered by `VisualLoss` descending.
3. For each `e`:
   - If `_reference.MedianWorkMs > budget * 0.6` stop.
   - `restore = RestoreSetting(e.Setting, rep.OriginalSettings)`; null -> skip. Mapped settings: `Anti-aliasing`, `Water reflections`, `Shadows`, `Clouds`, `Fog`, `Particles`, `Simulation distance`, `Creature density`, `Render distance`, `Resolution scale`, `Physics substeps`. It restores the ORIGINAL value of that setting, not one step.
   - Predictive check: `cost = max(0, e.Before.MedianWorkMs - e.After.MedianWorkMs)`. If `_reference.MedianWorkMs + cost > budget * 0.8`: NotTouched `Quality give-back — restoring {Setting} would cost about {F(cost)} ms, and only {F(max(0, budget*0.8 - ref work))} ms of safe headroom is left.` and skip.
   - Log: `Title = "Restore quality: " + Setting`, `Kind = QualityRestore`, `VisualLoss = -e.VisualLoss`, `Before = _reference`, Hypothesis `There is plenty of headroom ({F(ref work)} ms of a {F(budget)} ms budget), so GAPO tries to give back the most visible reduction.`
   - Publish `(Testing, Title, Hypothesis, measuring = true)`. Snapshot, apply `restore`, `ApplySettings(trial, "restoring " + Setting)`, `r = Measure(rev, Title)`. `log.GainPct = (ref work - r work) / ref work * 100` (normally negative).
   - Keep if `r.MedianWorkMs <= budget * 0.8 && r.P95FrameMs <= budget * 1.3`: `Kept: quality restored and the target is still met ({F(r work)} ms of {F(budget)} ms).`; `_current = trial`; `_reference = r`.
   - Else roll back (`ApplySettings(snapshot, "rollback")`): `Reverted: restoring it would break the frame budget ({F(r work)} ms).`

---

## 12. Pacing / frame cap (`Pacing(rep, goal)`)

Applied last, judged on smoothness. Uses `refresh = hw.EffectiveRefreshHz` (not `TargetFps`).

```
sustained = 1000 / max(0.1, _reference.P95FrameMs)
workFps   = 1000 / max(0.1, _reference.MedianWorkMs)
```

| Case | Condition | Result |
|---|---|---|
| A | `goal == MaxFps` | `PacingNote = "No frame cap (Maximum FPS mode)."`; NotTouched `Frame-rate cap — Maximum FPS mode never caps.`; return |
| B | `workFps >= refresh * 1.08 && sustained >= refresh * 0.9` | `cap = refresh`; why = `Your PC now finishes frames well within {F(1000/refresh)} ms, so locking to your {refresh} Hz display gives even frame pacing and frees CPU time (less heat and fan noise).` |
| C | `goal == Smooth && workFps >= refresh / 2.0 * 1.1` | `cap = refresh / 2` (integer division); why = `Smoothness mode: {refresh} FPS isn't sustainable yet, so a steady {refresh/2} FPS (an even divisor of the refresh rate) avoids judder.` |
| D | otherwise | `PacingNote = "Not capped: {refresh} FPS isn't sustainable yet, and on a fixed-refresh display a cap that isn't a divisor of {refresh} Hz would judder."`; NotTouched `Frame-rate cap — ` + the note without its `Not capped: ` prefix (intended); return |

For B and C: if `_current.FrameRateCap == cap` return (nothing recorded). Otherwise:

- Log: `Title = "Frame-rate cap: {Unlimited | current cap} → {cap} FPS"`, `Setting = "Frame-rate cap"`, `Kind = Pacing`, `Hypothesis = why`, `Before = _reference`.
- Publish `(Pacing, "Tuning frame pacing", why, measuring = true)`. Snapshot; `trial.FrameRateCap = cap`; `ApplySettings(trial, "frame cap")`; `r = Measure(rev, "Pacing")`.
- `devBefore = _reference.StdDevFrameMs`, `devAfter = r.StdDevFrameMs`; `log.PaceGainPct = devBefore > 0 ? (devBefore - devAfter) / devBefore * 100 : 0`.
- Keep if `r.AvgFps >= cap * 0.93 && devAfter <= devBefore * 1.05`: `Kept: frame-time variation {F(devBefore)} → {F(devAfter)} ms at a steady {r.AvgFps:0} FPS.`; `_current = trial`; `_reference = r`; `PacingNote = "Locked to {cap} FPS to match your {refresh} Hz display."`
- Else roll back. `held = r.AvgFps >= cap * 0.93`.
  - held: `Reverted: frame-time variation went from {F(devBefore)} to {F(devAfter)} ms — the cap made pacing no smoother on this PC, so the extra frames are worth more.`; `PacingNote = "Left uncapped: a {cap} FPS cap was tested and didn't make frame pacing smoother here."`
  - not held: `Reverted: the {cap} FPS cap didn't hold ({r.AvgFps:0} FPS measured).`; `PacingNote = "Left uncapped: a {cap} FPS cap couldn't be held."`
- Publish `(Pacing, Title, Reason)`.

For a 60 Hz display: full cap needs median work <= 15.43 ms and p95 frame time <= 18.52 ms. Half-refresh cap in Smoothness needs median work <= 30.3 ms.

---

## 13. Validation, completion, cancel, failure

Validation:
1. Publish `(Validating, "Validating the result", "Measuring the final configuration on the same benchmark flight as the baseline.", measuring = true)`.
2. `rep.Final = MeasurePair("Final", out f1, out f2, rep.FinalTrace)`.
3. `rep.FinalMonitor = MonitorSummary.From(monitor.History(Now - 7000))`.
4. `rep.FinalHitches` = stats over the last 60 s of frames with the final revision.
5. `VerifyHitchFix(rep)`.
6. `rep.FinalDiagnosis = Diagnosis.Run(rep.Final, rep.FinalMonitor, hw, _current, TargetFps, rep.FinalHitches)`.
7. `rep.FinalSettings = _current.Clone()` with `ModifiedBy = "GAPO Optimiser"` (report object only; not written to disk).
8. `rep.Recommendations = Advisor.Build(hw, rep.BaselineMonitor, rep.BackgroundApps, rep.BaselineHitches, rep)`.
9. `rep.Completed = true`. `SendCommand(BenchmarkCamera, 0)`. Toast `GAPO: {Signed(FpsGainPct)}% average FPS ({Baseline.AvgFps:0} → {Final.AvgFps:0}). Details in GAPO.`
10. `Finish`, then publish `(Done, "Done", "{Signed(FpsGainPct)}% average FPS")`.

Validation only measures. It does not compare the final result with the baseline and never triggers a rollback.

`Finish(rep, ...)`: `rep.Finished = now`; `FinalSettings` falls back to `_current`; `SelfCpuAvgPct = (own CPU ms used during run) / wall ms / Environment.ProcessorCount * 100`; `SelfPrivateMB = PrivateMemorySize64 / 1048576`; restore own priority; `SelfCpuPeakPct = monitor.SelfCpuPeakPct`; `SelfCpuInWindowsPct = mean of the per-window SelfCpuSinceMarkPct values`; `Windows`, `MeasuredFrames`; `rep.Save()` (errors swallowed).

Cancel (`OperationCanceledException`): `rep.Cancelled = true`; `SafeRelease()` (thread sampling off; `SendCommand(BenchmarkCamera, 0, 800)`); `Finish`; publish `(Cancelled, "Stopped", "Optimisation stopped. Changes kept so far are still applied; use Revert all to undo them.")`.

Any other exception: `rep.Error = ex.Message`; `SafeRelease()`; `Finish`; publish `(Failed, "Optimisation stopped", ex.Message)`.

Error messages thrown by the engine: `Could not read the game's settings file.` / `The game did not respond to the benchmark command.` / `The game stopped responding.` / `Too few frames were measured ({n}).` / `The game did not apply the new settings.` / `The game was closed.`

---

## 14. Revert all (`RevertAll()`)

1. Return if `_original == null`.
2. If running: `Cancel()`, `thread.Join(8000)`.
3. If the game is connected: `rev = link.WriteSettings(_original.Clone(), "GAPO Optimiser (reverted to original)")`; `link.WaitForRevision(rev, 3000)` (result ignored); if `_priorityChanged`, set the game's priority back to `_originalPriority`; toast `GAPO reverted every change`.
4. If not connected: clone `_original`, set `ModifiedBy = "GAPO Optimiser (reverted to original)"`, `Revision++`, save to `DemoPaths.SettingsFile` (`%LOCALAPPDATA%\GAPO-Demo\sandbox-settings.ini`).
5. `_current = _original.Clone()`.

UI toast after revert (`MainForm`): `Every change was reverted — the game is back to its original settings.`

`HasChanges = _original != null && _current != null && _original.ToIni(false) != _current.ToIni(false)` (not referenced by the UI).

---

## 15. Progress phases and status text

`EngineStatus { Phase, Title, Detail, Progress (0..1), Measuring, LastLog, Diagnosis }`. `Publish` builds a new status object, carries `LastLog` and `Diagnosis` forward when not supplied, assigns it to the volatile `Status` field and raises `Changed` on the engine thread (handler exceptions swallowed). `Measuring == true` means a measurement window is open and the UI must not repaint (UI text: `Measuring — GAPO is keeping quiet`).

`ProgressFor(phase)`:

| Phase | Progress |
|---|---|
| Idle, Preparing | 0.02 |
| Baseline | 0.06 |
| Diagnosing | 0.16 |
| Testing | `0.2 + 0.62 * done / (done + left)`, `done = _experimentsDone`, `left = max(1, _experimentsLeft)` (range 0.20-0.82) |
| Pacing | 0.85 |
| Validating | 0.9 |
| Done | 1 |
| Failed, Cancelled | previous value |

`_experimentsDone` counts only `RunExperiment` calls (not the hitch fix, priority test, give-back or pacing). `_experimentsLeft` starts at 6.

Status text by step:

| Step | Title | Detail |
|---|---|---|
| Start | `Getting ready` | `Connecting to the game and waiting until it has finished loading.` |
| Warm-up wait | `Waiting for the game to finish loading` | `The game is still streaming its world. Measuring now would blame the wrong thing — like judging a game while it compiles shaders.` |
| Baseline | `Measuring the baseline` | `Two identical benchmark runs. How much they differ tells GAPO how precise its measurements are on this PC.` |
| Diagnosing | `Diagnosing` | `Checking per-core load, the game's busiest thread and background apps.` |
| Hitch observation | `Watching for stutter` | `Some hitches only happen every few seconds, so GAPO watches a little longer before judging them.` |
| Diagnosis result | `{diag.Title}` | `{diag.Summary}` |
| Others | see sections 7, 8, 10.2, 11, 12, 13 | |

UI phase labels (`MainForm`): `Preparing`, `Step 1 · Observe`, `Step 2 · Diagnose`, `Step 3 · Test one change`, `Step 4 · Frame pacing`, `Step 5 · Verify`.

---

## 16. Report model and text export

### 16.1 Model

`ExperimentLog { Index, Title, Setting, Hypothesis, Reason, BottleneckAfter, Kind, VisualLoss, PredictedMs, Before, After (FrameStats), GainPct, PaceGainPct, Seconds, SelfCpuPct, Decision }`

`Recommendation { Severity (0 info, 1 suggestion, 2 important), Title, Detail, ActionLabel, ActionTarget }`

`BackgroundApp { Name, Kind, CpuPct, MemBytes, Processes, Protected }`

`OptimisationReport { Started, Finished, Goal, TargetFps, TargetReason, Hardware, Baseline, Final, BaselineHitches, FinalHitches, BaselineMonitor, FinalMonitor, BaselineDiagnosis, FinalDiagnosis, Experiments[], NotTouched[], Recommendations[], BackgroundApps[], HardwareDecisions[], OriginalSettings, FinalSettings, NoiseFloorPct, MeasuredFrames, Windows, SelfCpuAvgPct, SelfCpuPeakPct, SelfCpuInWindowsPct, SelfPrivateMB, Completed, Cancelled, Error, PacingNote, BaselineTrace[], FinalTrace[] }`

Derived:
- `FpsGainPct = (Final.AvgFps / Baseline.AvgFps - 1) * 100` (0 if missing). This is the headline number.
- `Low1GainPct = (Final.Low1Fps / Baseline.Low1Fps - 1) * 100`
- `WorkSpeedup = Baseline.MedianWorkMs / Final.MedianWorkMs` (1 if missing)
- `Kept`, `Reverted` = counts by decision.

Helpers:
- `Signed(v)` = `(v >= 0 ? "+" : "") + v.ToString("0", InvariantCulture)`
- `Gb(bytes)`: >= 10 GiB `"0" GB`; >= 1 GiB `"0.#" GB`; else MiB as `"0" MB` (invariant culture)
- `ClassName`: `Entry-level PC`, `Performance PC`, `Mainstream PC`, `Unknown`
- `Tri(bool?)`: `unknown`, `on`, `off`

### 16.2 `ToText()` layout (numbers in invariant culture unless inside a pre-built reason string)

```
GAPO — Adaptive Performance report
==================================
Game:      {GameName} ({EngineName})
Goal:      {GoalName} · target {TargetFps:0} FPS ({TargetReason})
When:      {Started:yyyy-MM-dd HH:mm} · took {(Finished-Started).TotalSeconds:0} s

RESULT                                                        [only if Baseline and Final exist]
  Performance      {Signed(FpsGainPct)}% average FPS
  Average FPS      {Baseline.AvgFps:0.0} → {Final.AvgFps:0.0}
  1% low FPS       {Baseline.Low1Fps:0.0} → {Final.Low1Fps:0.0}
  0.1% low FPS     {Baseline.Low01Fps:0.0} → {Final.Low01Fps:0.0}
  Frame work       {Baseline.MedianWorkMs:0.0} ms → {Final.MedianWorkMs:0.0} ms ({WorkSpeedup:0.0}× less work per frame)
  Stutters/min     {BaselineHitches.StuttersPerMin:0.0} → {FinalHitches.StuttersPerMin:0.0}   [if both exist]
  Pacing           {PacingNote}                                                            [if not empty]
  Measured         {MeasuredFrames} frames in {Windows} windows · noise floor ±{NoiseFloorPct:0.0}%
  GAPO overhead    {SelfCpuAvgPct:0.00}% CPU average ({SelfCpuInWindowsPct:0.00}% during measurements, peak {SelfCpuPeakPct:0.0}%) · {SelfPrivateMB:0} MB RAM

DIAGNOSIS (before)                                            [only if BaselineDiagnosis exists]
  {Title} — {Summary}
  {EngineLimitation}                                          [if not null]
  · {each evidence sentence}
  ! {each secondary sentence}

WHAT GAPO TRIED (one change at a time, each measured on the same benchmark flight)
  {Index,2}. [{DECISION upper case}] {Title}
      Why:    {Hypothesis}
      Result: ...                                             [one of the three forms below]
      Decision: {Reason}
      Bottleneck now: {BottleneckAfter}                       [if not empty]

CONSIDERED BUT NOT CHANGED                                    [if any]
  · {each NotTouched line}

BACKGROUND ACTIVITY WHILE THE GAME RAN (top 6)                [if any]
  · {Name} ({Kind}{", never touched" if Protected}) {CpuPct:0.0}% CPU · {Gb(MemBytes)}

RECOMMENDATIONS (not changed automatically)                   [if any]
  · {Title} — {Detail}

YOUR PC                                                       [if Hardware exists]
  {each HardwareLines line}
  → {each HardwareDecisions line}

SETTINGS BEFORE                                               [if both settings exist]
  {OriginalSettings.ToIni(false), each line indented two spaces}
SETTINGS AFTER
  {FinalSettings.ToIni(false), indented}
```

Result line forms:
- `Kind == Hitch` and `Before` exists: `Result: save freezes {Before.AutosaveHitches} in {Before.DurationMs/1000:0} s before; see decision for after`
- `Kind == QualityRestore` with both stats: `Result: frame work {Before work:0.0} → {After work:0.0} ms with the quality restored`
- otherwise with both stats: `Result: frame work {Before work:0.0} → {After work:0.0} ms ({Signed(GainPct)}%), FPS {Before.AvgFps:0.0} → {After.AvgFps:0.0}, visual cost {VisualLoss}/10, predicted −{PredictedMs:0.0} ms` (the minus before the prediction is U+2212)

`Save()`: creates `DemoPaths.ReportsDir` (`%LOCALAPPDATA%\GAPO-Demo\Reports`), writes `GAPO-report-{Started:yyyyMMdd-HHmmss}.txt`, UTF-8 with BOM, after `ToText().Replace("\n", "\r\n")`. Called automatically at the end of every run (completed, cancelled or failed).

`HardwareLines(hw)` (label padded to 9 characters):
```
CPU      {Name} · {Cores} cores / {Logical} threads{ ({P}P + {E}E) if Hybrid}{ · {MaxMhz/1000:0.0#} GHz if MaxMhz > 0}{ · L3 {L3KB/1024} MB if L3KB > 0}
GPU      {Name}{ · {Gb(Vram)} shared|VRAM}{ · integrated}{ · virtual adapter}{ · driver {DriverVersion}{ ({DriverDate})}}      [one per GPU]
Memory   {Gb(Total)}{ · {ModuleSummary}} · {ChannelMode} · {LoadPct}% in use
Storage  {Drive} ({Role}) {MediaType}{ {BusType}}{ · {Model}} · {Gb(Free)} free of {Gb(Total)}                          [one per drive]
Display  {Width}×{Height} @ {"? Hz (not reported)" | "{RefreshHz} Hz"} · {Monitors} monitor{s} · {ScalePct}% scale
Power    {"Laptop on AC" | "Laptop on battery"}{ ({BatteryPct}%)} | "Desktop (AC)" · plan {PlanName}{ · mode {Overlay}}
Windows  {Name}{ {Version}} (build {Build}) · Game Mode {Tri} · HAGS {Tri}
Class    {ClassName} — {ClassReason}
```
`ModuleSummary`: `{count}× {size}` when all modules are equal, else sizes joined by ` + `; then ` {Type}` and `-{ConfiguredMts}` when known.

### 16.3 `HardwareDecisions` lines (`RecordHardwareDecisions`)

- `{physical} cores / {logical} threads → render threads are tested at {physical}{, then {logical} to see whether SMT helps this game}.` (SMT clause when `Smt && logical > physical`)
- `{"Unknown refresh rate (assumed 60 Hz)" | "{RefreshHz} Hz display"} → frame budget {F(1000/TargetFps)} ms ({TargetFps:0} FPS target).`
- memPressure: `RAM under pressure → memory-saving options allowed.` else `{Gb(Total)} RAM, {MemLoadPct:0}% used → memory isn't limiting; texture quality left alone.`
- For each drive whose role contains `Game`: `Game drive {Drive} is {"an unknown drive type" | "an {MediaType}"} → save hitches {"are longer on hard drives; " if HDD}fixed via autosave interval if observed.`
- If a battery exists: `Laptop {on AC power | on battery} → {full performance available. | Windows limits speed; a frame cap also saves battery.}`
- If `Class == Entry`: `Entry-level PC → the biggest wins come from removing wasted work (threads, LOD, ray-marched effects) before touching visible quality.`

---

## 17. Background-app contention and recommendations (`Advisor`)

### 17.1 `Contention(a, b, seconds, gamePid, selfPid)`

For every process in snapshot `b` except the game, the optimiser and PID 0:
- `cpu = (b.CpuTicks - a.CpuTicks) / 10000 / (seconds * 1000) / Environment.ProcessorCount * 100` (share of the whole machine; 0 if the PID was not in `a`).
- `key` = process name without a trailing `.exe`.
- Display name and kind from the `Known` table (case-insensitive) or `key` itself.
- Group by display name: `CpuPct += max(0, cpu)`, `MemBytes += PrivateBytes`, `Processes++`.
- `Kind` = known kind, else `Windows` if `key` is in `NeverTouch`, else `App`.
- `Protected` = `key` in `NeverTouch` or `key` starts with `MsMpEng`.
- Sort by `CpuPct` descending, then `MemBytes` descending.

`Known` (process key -> name, kind): chrome -> Google Chrome, Browser; msedge -> Microsoft Edge, Browser; firefox -> Firefox, Browser; opera -> Opera, Browser; brave -> Brave, Browser; discord -> Discord, Chat; steamwebhelper -> Steam, Launcher; steam -> Steam, Launcher; epicgameslauncher -> Epic Games Launcher, Launcher; obs64 -> OBS Studio, Recording; onedrive -> OneDrive, Cloud sync; dropbox -> Dropbox, Cloud sync; ms-teams -> Microsoft Teams, Chat; teams -> Microsoft Teams, Chat; spotify -> Spotify, Media; zoom -> Zoom, Chat; code -> Visual Studio Code, App; vmmem -> Virtual machine / WSL, Virtualisation; vmmemwsl -> WSL, Virtualisation; docker desktop -> Docker Desktop, Virtualisation; icue -> Corsair iCUE, RGB utility; armourycrate.service -> Armoury Crate, RGB utility; razer synapse service -> Razer Synapse, RGB utility; msedgewebview2 -> Edge WebView (inside apps), App; searchindexer -> Windows Search indexing, Windows; tiworker -> Windows Update, Windows; mousocoreworker -> Windows Update, Windows; wuauclt -> Windows Update, Windows; wmiprvse -> WMI provider host, Windows.

`NeverTouch`: msmpeng, mssense, nissrv, securityhealthservice, system, registry, smss, csrss, wininit, services, lsass, svchost, dwm, winlogon, fontdrvhost, memory compression, system idle, idle, audiodg, explorer, sihost, ctfmon, conhost.

### 17.2 `Build(hw, mon, apps, hitches, rep)`

`mon` is the BASELINE monitor summary. Output is sorted by `Severity` descending (stable).

| # | Condition | Sev | Title | Detail | Action (label -> target) |
|---|---|---:|---|---|---|
| 1 | `HasBattery && !OnAc` | 2 | `Plug in your laptop` | `You're on battery, so Windows limits CPU and GPU speed to save power. Plugged in, the same settings run faster.` | |
| 2 | `Power.PowerSaver` | 2 | `Power mode is set to save energy` | `The active plan/mode ({PlanName}{, {Overlay}}) holds the CPU back. Switch to Balanced or Best performance while gaming.` | `Open power settings` -> `ms-settings:powersleep` |
| 3 | `mon.BackgroundCpuPct >= 10` and sum of CPU of `closable >= 4`, where `closable` = first 4 apps with `!Protected && Kind != "Windows" && CpuPct >= 1.5` | 1 | `Close busy background apps` | `While the game ran, {Name ({CpuPct:0}% CPU), ...} competed for the CPU. GAPO never closes apps for you.` | |
| 4 | `mon.MemLoadPct >= 85` | 2 | `Free up memory` | `RAM is {MemLoadPct:0}% full, which causes stutter when the game streams data. Biggest users: {top 3 non-protected, non-Windows apps by memory as "Name Gb"}.` | |
| 5a | `Memory.SingleChannel && (gpu.Integrated || gpu.Virtual)` | 1 | `Memory runs in single-channel mode` | `Only one RAM module is installed. Integrated graphics share system memory, so a second matching module (dual-channel) can raise graphics performance a lot. Hardware upgrade — GAPO can't change this.` | |
| 5b | else `Memory.SingleChannel` | 0 | `Memory runs in single-channel mode` | `A second matching RAM module would double memory bandwidth, which helps CPU-heavy games.` | |
| 6 | `0 < Memory.TotalBytes < 6 GiB` | 1 | `Low RAM for modern games` | `This PC has {Gb(Total)} of RAM. Keep other apps closed while playing; 8–16 GB is the comfortable range today.` | |
| 7 | drive role contains `Game` and `MediaType == "HDD"` | 1 | `Game is on a hard drive` | `Hard drives make loading and saving hitches much longer{ (autosave froze the game for ~{AutosaveHitchMs:0} ms) if hitches seen}. Installing games on an SSD helps.` | |
| 8 | drive `TotalBytes > 0 && FreePct < 10` | 1 | `Drive {Drive} is almost full` | `Only {FreePct:0}% free. Low free space slows the pagefile, shader caches and updates.` | `Open storage settings` -> `ms-settings:storagesense` |
| 9 | `Os.GameMode == false` | 0 | `Windows Game Mode is off` | `Game Mode reduces background activity (like Windows Update) while you play.` | `Open Game Mode` -> `ms-settings:gaming-gamemode` |
| 10 | `Os.BackgroundRecording == true` | 1 | `Background recording is on` | `Xbox Game Bar is recording the last minutes of gameplay in the background, which costs performance on modest PCs.` | `Open capture settings` -> `ms-settings:gaming-gamedvr` |
| 11 | `Display.RefreshUnknown` | 0 | `Display refresh rate not reported` | `The display driver didn't report a refresh rate (typical for virtual machines and basic display drivers), so GAPO assumed 60 Hz.` | |
| 12 | `gpu.Virtual` | 0 | `No hardware graphics driver` | `Windows is using a basic/virtual display adapter ({Name}). Real games need the GPU vendor's driver.` | |
| 13 | `rep.BaselineDiagnosis.EngineLimitation != null` | 0 | `Engine limitation detected` | the `EngineLimitation` text | |

`PowerSaver` is true when the active scheme GUID is `a1841308-3541-4fab-bc81-f71556f20b4a` or the effective overlay is `961cc777-2547-4f9d-8174-7d86181b8a7a` (Best power efficiency). `BackgroundRecording` reads `HKCU\Software\Microsoft\Windows\CurrentVersion\GameDVR\HistoricalCaptureEnabled == 1`; `GameMode` reads `HKCU\Software\Microsoft\GameBar\AutoGameModeEnabled` (missing = on).

---

## 18. Every numeric constant

Only the first four are named in the demo. The spec requires named constants in the product (III.1); suggested names are given.

### Measurement

| Value | Demo name / location | Meaning | Suggested name |
|---|---|---|---|
| 650 ms | `DiscardMs` | settling frames discarded after path restart | `SettlingDiscardMs` |
| 2600 ms | `WindowMs` | minimum frame-time span of a window | `WindowMinSpanMs` |
| 45 | `MinFrames` | minimum frames in a window | `WindowMinFrames` |
| 7000 ms | `MaxWindowMs` | wall timeout (plus `DiscardMs` = 7650 ms) | `WindowTimeoutMs` |
| 8 | `MeasureFrames` | hard floor; below it the window throws | `WindowAbortBelowFrames` |
| 200 ms | `MeasureFrames` | window poll interval | `WindowPollMs` |
| 90 / 30 | `WaitForWarmup` | frames inspected / minimum frames present | `WarmupLookbackFrames`, `WarmupMinFrames` |
| 45000 ms / 250 ms | `WaitForWarmup` | warm-up timeout / poll | `WarmupTimeoutMs`, `WarmupPollMs` |
| 4000 ms / 30 ms | `ApplySettings`, `WaitForRevision` | acknowledgement timeout / poll | `ApplyAckTimeoutMs` |
| 2000 ms / 15 ms | `SendCommand` | command ack timeout / poll | `CommandAckTimeoutMs` |
| 800 ms | `SafeRelease` | ack timeout when releasing the benchmark camera | |
| 50 ms | `SleepChecked` | cancellation check granularity | |

### Noise floor and decisions

| Value | Location | Meaning | Suggested name |
|---|---|---|---|
| 0.03 | `_noise` initial | default before the A/A run | `DefaultNoiseFloor` |
| 2.5 | `Run` | A/A difference multiplier | `NoiseFloorMultiplier` |
| 0.025 / 0.10 | `Run` | noise-floor clamp | `NoiseFloorMin`, `NoiseFloorMax` |
| 0.01 | several | divide-by-zero guards (ms, FPS) | |
| 0.12 and 3x noise | `RunExperiment` | 1 % low veto threshold | `LowsVetoMinDrop`, `LowsVetoNoiseMultiple` |
| 0.25 ms and 0.025 x work | `Run` | minimum predicted saving for a candidate | `CandidateMinPredictedMs`, `CandidateMinPredictedShare` |
| 0.6 | `Candidate.Value` | visual-loss offset in the value ratio | `ValueVisualLossOffset` |
| 0.88 / 1.25 | `Run` | stop-at-target: work share of budget / p95 share of budget | `StopWorkShare`, `StopP95Share` |
| 4 | `Run` | Quality goal refuses candidates with this visual loss or more | `QualityMaxVisualLoss` |
| 4 | `Run` | number of remaining options named in the stop message | |
| 6, +1, 2 | `Run` | progress estimate inputs | |
| 2 | Kept reason | `VisualLoss <= 2` = "small visual cost" | |
| weights | `Weights` | section 10.4 | `GoalWeights` table |
| 144 | `Run` | target ceiling (floor for Maximum FPS) | `TargetFpsCeiling` |
| 24 -> 60 | `EffectiveRefreshHz` | below 24 Hz treat as unknown, assume 60 | `MinPlausibleRefreshHz`, `AssumedRefreshHz` |

### Diagnose step

| Value | Location | Meaning |
|---|---|---|
| 2300 ms | `Run` | process-snapshot span for contention and hottest thread |
| 9000 / 7000 / 4000 ms | `Run`, `RunExperiment` | monitor look-back for baseline / final / post-commit summary |
| 14 s / 15000 ms / 500 ms | `EnsureHitchObservation` | observation needed / maximum wait / poll |
| 60000 ms | `Run` | hitch-statistics look-back |
| 900 ms | `Run` | pause so the diagnosis can be read |
| 88 % | `Run`, `Diagnosis` | memory-pressure threshold (`MemLoadPct`) |
| 700 MiB | `Diagnosis` | available-RAM floor for the memory secondary |
| 0.2 | `Diagnosis` | warm-up share of frames |
| 0.05 ms | `Diagnosis`, KB | show Streaming stage / physics candidate eligibility |
| 0.5 % | `Diagnosis` | minimum total CPU for per-core evidence |
| 25 points | `Diagnosis` | busiest core minus total CPU for the "hides a one-thread limit" sentence |
| 35 % | `OtherCoresNote` | other cores "mostly idle" |
| 15 % | `Diagnosis`, `Run` | background CPU threshold (secondary, other-cores note, priority test) |
| 2.2 | `Diagnosis` | p99 over median for "uneven frame pacing" |
| 0.8 | `Diagnosis` | `MhzLimit / MaxMhz` clock-limit threshold |
| 0.92 | `Diagnosis` | target-reached share of budget |
| 3 | `Diagnosis` | render passes named |

### Statistics

| Value | Meaning |
|---|---|
| 0.5 / 0.95 / 0.99 | percentiles |
| 0.01 / 0.001 | worst-frame fractions for 1 % / 0.1 % lows |
| 1.8 x median, median + 20 ms | stutter threshold (the larger) |
| median + 15 ms | flagged-hitch stutter threshold |
| 60000 | ms per minute for stutters/min |
| 0.5 | terrain share in `PixelWork` |

### Hitch fix, give-back, pacing

| Value | Meaning |
|---|---|
| 120 s | autosave intervals shorter than this are eligible |
| 300 s | new autosave interval |
| 0.6 | give-back entry: work <= 60 % of budget |
| 2 | give-back only for kept changes with visual loss >= 2 |
| 0.8 / 1.3 | give-back predictive and keep limit: work share / p95 share of budget |
| 1.08 / 0.9 | full cap: `workFps >= refresh * 1.08`, `sustained >= refresh * 0.9` |
| 1.1 | half-refresh cap (Smoothness): `workFps >= refresh / 2 * 1.1` |
| 0.93 | cap "held": `AvgFps >= cap * 0.93` |
| 1.05 | cap kept only if std-dev <= 105 % of before |
| 0.1 ms | divide guard |
| 8000 / 3000 ms | revert-all thread join / ack wait |

### Knowledge base

Thread efficiency 0.8 (to >= 4) / 0.85; thread cap 16; SMT gain 0.12; Terrain LOD 0.55; Shadows 0.88 / 0.95; AA 0.5, 0.75, 0.4 / 0.9; Water 0.85 / 0.9; Clouds 0.75 / 0.5; Fog 0.7 / 1.0; Particles sprite share 0.6, counts 0 / 700 / 2000 / 4500 / 9000; Simulation distance 0.65 / 0.6; Physics step target 4 then halving; Density steps 75 / 50, sprite share 0.2; Render distance thresholds 1150 / 900 / 700 -> 1100 / 850 / 650, LOD factor 0.45; Resolution 85 / 70; textures "12 MB"; present threshold 3 ms. Visual-loss values as in section 9.2.

### Advisor

1.5 % (closable app CPU), 4 apps, 10 % background CPU, 4 % closable sum, 85 % RAM, 3 biggest memory users, 6 GiB low-RAM limit, 10 % free disk, 6 apps in the report.

### Progress

0.02, 0.06, 0.16, 0.2 + 0.62 x fraction, 0.85, 0.9, 1.

### Collaborators (context)

Link poll 100 ms (1000 ms disconnected); ring 4096 frames; frame store 24,000 (trim to 22,000); back-fill 600 frames; disconnect after 4 s without frames and a dead process; monitor interval 1000 ms; history 180 samples; thread sampling interval >= 900 ms; sandbox settings-file check 200 ms; benchmark flight loop 12 s.

---

## 19. Bugs, shortcuts and demo-only hacks (do not copy)

### Confirmed defects

1. **Off-by-one in the pacing "not capped" line.** `rep.PacingNote.Substring(13)` should be 12. The NotTouched line loses the first digit of the refresh rate: `Frame-rate cap — 0 FPS isn't sustainable yet…` on 60 Hz. Verified by execution.
2. **Doubled carriage returns in saved reports.** `ToText()` is built with `AppendLine` (already `\r\n` on Windows) and `Save()` then replaces `\n` with `\r\n`, producing `\r\r\n`. Verified by execution. `Indent` has the same assumption.
3. **A failed or cancelled experiment leaves the untested trial setting applied.** If `Measure` throws after `ApplySettings(trial)` (cancel, game closed, too few frames), there is no rollback and `_current` still holds the pre-trial settings, so the engine's view and the file disagree. The cancel message claims only kept changes remain. The same applies to give-back and pacing trials. The spec (§10 Added) requires: roll back and record `Failed`, not "no gain".
4. **Stop-at-target looks only at the top-ranked candidate.** If it has a visual cost the loop ends, even when a zero-visual-loss candidate (or the memory-relief candidate, whose `Value` is always 0) is further down the list.
5. **Rule 1 wording is wrong for regressions.** A change that makes things 20 % slower is reported as `-20% is within the ±2.5% measurement noise`. Split "within noise" from "made it worse".
6. **Validation never acts.** Final is measured but not compared with the baseline; a net regression is reported, not reverted.
7. **Hitch fix is not an experiment.** It is kept without measurement and never rolled back; the "Check:" outcome does not change the decision. In the sandbox the autosave timer is reset to now + 300 s on change, so "no freezes since" is guaranteed for any run shorter than 5 minutes. `log.After` mixes frames from every later configuration.
8. **Stutters/min before and after are not comparable.** Baseline hitch stats cover up to 60 s of same-revision frames, including frames from before the benchmark camera was enabled (different scene). Final hitch stats cover only frames of the final revision, which can be just the two validation windows (about 6 s), and do not exclude warm-up frames. The 14 s observation rule checks the span of the whole frame store, not of same-revision frames.
9. **Rollbacks create new revisions.** Identical configurations get different revision numbers, so per-revision filters throw away valid frames. Key statistics by a configuration hash instead.
10. **`TargetReason` and pacing ignore the 144 FPS ceiling.** On a 240 Hz display with a non-Maximum goal the target is 144 FPS but the text says `your 240 Hz display`, and pacing tries to cap at 240. For 2-23 Hz the engine assumes 60 but prints the raw value.
11. **Quality give-back restores the original value, not one step**, while its cost estimate uses a single step's saving. A setting with two kept steps of visual loss >= 2 (anti-aliasing, water) is processed twice; the second pass tests an unchanged configuration. `Terrain LOD`, `Textures` and `Render threads` have no restore mapping and are skipped silently.
12. **Priority test error handling.** Any exception during the window (including "The game was closed." and "Too few frames…") is reported as `Windows refused the change (…)`, and the raised priority is left in place. The "already runs at" line prints the raw enum name (`AboveNormal`).
13. **Revert all depends on in-memory state.** `_original` is re-read at the start of every run, so after a second run "Revert all" returns to the first run's result, not the user's settings; the original priority has the same problem. Nothing survives an optimiser crash. The disconnected path writes to a hard-coded demo path. The ack result is ignored and the 8 s join can time out while the engine thread is still writing.
14. **`HasChanges` compares INI text that includes `Revision` and `ModifiedBy`**, so it is true after any write (even a rollback) and can be false in case 3.
15. **Settings write is not atomic.** Delete then Move leaves a moment with no file. Use `File.Replace` / `MoveFileEx(MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH)` and keep a backup.
16. **Diagnosis leftovers.** The target-met override keeps the earlier `Category` and `EngineLimitation`, so a game that meets its target can still produce an "Engine limitation detected" tip. Primary classification is only `Render >= Sim`; present, I/O, background, memory and pacing can never be primary. The warm-up branch is unreachable from the engine because warm-up frames are filtered out first (it is reachable from the live analyser).
17. **Hard-coded evidence.** `(disk writes ~4 MB each)` and `High textures use 12 MB less` are literals, not measurements. `in the last {n} s` uses summed frame time, not wall time.
18. **Pacing wording.** `Locked to {cap} FPS to match your {refresh} Hz display.` is also used for the half-refresh cap. No note is recorded when the cap is already set. Odd refresh rates give a non-divisor half cap (75 -> 37).
19. **Culture mixing.** `F()` and many `ToString("0")` calls use the current culture while `ToText` uses invariant culture, so a German or French Windows shows `6,1 ms` inside reasons next to `6.1` in the result lines.
20. **Negative zero.** `Signed(-0.3)` yields `0` on .NET Framework but `-0` on .NET Core 3.0 and later (formatting change), so ported strings differ unless handled. Reason strings use ASCII `-` for negatives while the report's "predicted" uses U+2212.

### Statistical shortcuts

21. Noise floor from one pair of windows (n = 2), scaled by a fixed 2.5 and clamped at 10 %. On noisier machines the true noise can exceed the clamp. The spec asks for confidence intervals or rank tests and ABAB interleaving.
22. One window per experiment (about 100-250 frames). 1 % low is 1-3 frames; 0.1 % low is the worst frame.
23. Windows are not equal length (2.6-2.9 s, up to 7.65 s), so each covers a slightly different part of the 12 s flight. A timed-out window with 8 or more frames is accepted as valid.
24. Autosave-, GC- and settings-reload-flagged frames are not excluded from windows.
25. Pacing is decided on the standard deviation of one window; Appendix B records that this decision flips between runs. Borderline results need a longer re-test.
26. Experiments require `gain > noise` on busy time even in Smoothness mode, so a pure pacing improvement can never be kept by the experiment loop.
27. A reverted setting is blocked for the rest of the run even if the bottleneck later moves to its resource.
28. Candidates below the minimum predicted saving are dropped without a "considered but not changed" entry.
29. The memory candidate is kept unconditionally; nothing checks that memory pressure fell or that frame time did not get worse.
30. Monitor summaries are wall-clock look-backs (9 / 7 / 4 s), not aligned to windows; the baseline summary includes the 2.3 s snapshot span. Hottest-thread evidence comes from one or two samples. "Busiest core" is the maximum of per-core averages, which thread migration flattens.
31. Contention is one 2.3 s sample taken once; processes that start in between count as 0 %; grouping is by executable name.
32. Thresholds are inconsistent: target met is 0.92 (diagnosis), 0.88 with p95 1.25 (stop rule), 0.8 with p95 1.3 (give-back); memory 88 % (pressure) vs 85 % (tip); background CPU 15 % (test) vs 10 % (tip). Decide deliberately in the product.
33. `WaitForWarmup` gives up silently after 45 s and measures anyway.
34. `SelfCpuPeakPct` is the monitor's lifetime peak (includes the start-up hardware scan), not the run's peak. `ExperimentLog.SelfCpuPct` spans from the window start to the end of the experiment and is never shown.

### Dead or redundant code

`_stepsPlanned`, `_stepsDone` (never read by `ProgressFor`); `label` parameters; `Decision.Skipped`; `FrameStats.GcFrames`, `MeanWorkMs`, `Wait`; `MonitorSummary.GameCpuCores`, `CommitPct`, `WriteMBpsPeak`; five `Bottleneck` values; `Resource` values other than `Memory`; the `to == 1` branch in the render-thread ladder; `&& result.Low1Fps < ref.Low1Fps` in rule 2 and `&& !targetMet` in the Quality guard (both always true where evaluated).

### Demo-only by design

- Engine stage times, flags, settings revision acknowledgement, benchmark camera commands and in-game toasts all come from the sandbox's own hook. Real games provide none of them (spec I.4).
- `KnowledgeBase` is hard-coded for one game; multipliers were hand-tuned and predictions were off by up to about 2x (spec I.4).
- Hardware awareness is limited to the render-thread count. `hw.Class` only adds a text line. No P-core/E-core handling.
- Synchronous worker thread with `Thread.Sleep` polling; reports saved under `%LOCALAPPDATA%\GAPO-Demo`.
- No transaction journal on disk.
- Stage colours and source comments (the product must contain no comments, spec III.1).

---

## 20. Port notes from the spec, and what was not verified

Spec decisions that change this algorithm (Parts I.7, II §10, III):

- Keep: the loop order, A/A noise floor, identical scenes, acknowledged changes, hitch-first, re-diagnosis after each commit, stop-at-target, give-back, pacing last, `Value = predicted / (visual loss + 0.6)`, conditional evidence sentences, showing rollbacks as prominently as keeps.
- Change: confidence intervals or rank tests instead of a single multiplier; lows accumulated over play time; ABAB interleaving against thermal drift; hitch statistics per configuration; crash-safe journal for every change, replayed backwards by "Revert all" and uninstall; snapshot kept on disk if the game closes mid-experiment; failed measurement recorded as failed; predicted vs measured stored for every experiment; frame timing from PresentMon/ETW; per-game adapters with `Detect`, `ReadSettings`, `WriteSettings`, `RequiresRestart`, `Benchmark`, `AntiCheatPolicy`, `SettingLadders`.
- Engineering rules: named constants (`NoiseFloorMultiplier = 2.5`), no code comments, behaviour documented by tests (`RejectsChangeWithinNoiseFloor`), overhead budget of 0.3 % CPU inside a measurement window on a 2-core/4-thread PC.

Target laptop (2 cores / 4 threads): row 1a offers Render threads 1 -> 2 (efficiency 0.85, predicted 41 % of render time), then row 1b offers 2 -> 4 as the SMT test (predicted 12 %). Background CPU of 15 % of the machine equals 0.6 of one core there.

Not verified:

- The demo was not built or run for this brief; all behaviour is from reading the source. Only items 1, 2 and 20 in section 19 were checked by executing equivalent expressions (Windows PowerShell 5.1 on .NET Framework; the .NET Core behaviour in item 20 is from knowledge of the runtime change, not executed).
- `MainForm.cs`, `HardwareScanner.cs`, `Renderer.cs`, `Simulation.cs`, `World.cs` and most of `Sandbox/Game.cs` were read only where the engine depends on them. How the UI renders `Stages`, `BaselineTrace`, `Recommendation.ActionTarget` or `Severity` is not covered.
- Whether the benchmark flight is frame-for-frame deterministic was not checked (the engine assumes it).
- How PresentMon metrics map onto `WorkMs` and the stage times was not researched here. The demo's diagnosis and every prediction formula take engine stage times as input; under PresentMon those inputs do not exist, so sections 6.3 and 9 need new inputs and are the largest porting gap.
- The numeric multipliers in the knowledge base have no derivation in the source; treat them as starting priors only.
