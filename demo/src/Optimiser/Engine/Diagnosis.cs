using System;
using System.Collections.Generic;
using System.Linq;
using Gapo.Optimiser.Hardware;
using Gapo.Optimiser.Monitoring;
using Gapo.Shared;

namespace Gapo.Optimiser.Engine
{
    public enum Bottleneck { Unknown, Warmup, CpuMainThread, RenderSingleThread, RenderPixels, StorageHitch, Background, Memory, Pacing, TargetMet }

    public sealed class StageShare
    {
        public string Name; public double Ms; public double Share; public int Color;
        public StageShare(string n, double ms, double share, int color) { Name = n; Ms = ms; Share = share; Color = color; }
    }

    /// <summary>Averages of the system monitor over a time span.</summary>
    public sealed class MonitorSummary
    {
        public double TotalCpuPct, GameCpuCores, BackgroundCpuPct, SelfCpuPct, MemLoadPct, CommitPct;
        public double HottestThreadPct = -1, WriteMBpsPeak;
        public long AvailBytesMin = long.MaxValue, SelfPrivateBytes, GamePrivateBytes;
        public double[] CoreUsage = new double[0];
        public int Samples;

        public static MonitorSummary From(List<MonitorSample> samples)
        {
            var m = new MonitorSummary();
            if (samples == null || samples.Count == 0) { m.AvailBytesMin = 0; return m; }
            int cores = samples[0].CoreUsage.Length;
            m.CoreUsage = new double[cores];
            foreach (var s in samples)
            {
                m.TotalCpuPct += s.TotalCpuPct; m.GameCpuCores += s.GameCpuCores; m.BackgroundCpuPct += s.BackgroundCpuPct;
                m.SelfCpuPct += s.SelfCpuPct; m.MemLoadPct += s.MemLoadPct; m.CommitPct += s.CommitPct;
                if (s.GameHottestThreadPct > m.HottestThreadPct) m.HottestThreadPct = s.GameHottestThreadPct;
                if (s.GameWriteMBps > m.WriteMBpsPeak) m.WriteMBpsPeak = s.GameWriteMBps;
                if (s.AvailBytes < m.AvailBytesMin) m.AvailBytesMin = s.AvailBytes;
                for (int i = 0; i < Math.Min(cores, s.CoreUsage.Length); i++) m.CoreUsage[i] += s.CoreUsage[i];
                m.SelfPrivateBytes = s.SelfPrivateBytes; m.GamePrivateBytes = s.GamePrivateBytes;
            }
            int n = samples.Count;
            m.Samples = n;
            m.TotalCpuPct /= n; m.GameCpuCores /= n; m.BackgroundCpuPct /= n; m.SelfCpuPct /= n; m.MemLoadPct /= n; m.CommitPct /= n;
            for (int i = 0; i < cores; i++) m.CoreUsage[i] /= n;
            return m;
        }

        public double BusiestCore { get { return CoreUsage.Length == 0 ? 0 : CoreUsage.Max() * 100; } }
    }

    public sealed class Diagnosis
    {
        public Bottleneck Primary;
        public string Title = "", Summary = "", Category = "";
        public string EngineLimitation;              // category C text, when relevant
        public List<string> Evidence = new List<string>();
        public List<string> Secondary = new List<string>();
        public List<StageShare> Stages = new List<StageShare>();
        public double TargetFps, BudgetMs, WorkMs, RequiredSavingMs;

        public const int ColSim = 0xF472B6, ColRender = 0x22D3EE, ColPresent = 0xA78BFA, ColIo = 0xFBBF24, ColOther = 0x64748B;

        public static Diagnosis Run(FrameStats st, MonitorSummary mon, HardwareProfile hw, SandboxSettings cur, double targetFps, FrameStats hitchWindow)
        {
            var d = new Diagnosis { TargetFps = targetFps, BudgetMs = 1000.0 / targetFps, WorkMs = st.MedianWorkMs };
            d.RequiredSavingMs = Math.Max(0, d.WorkMs - d.BudgetMs);
            double work = Math.Max(0.01, st.MedianWorkMs);
            double other = Math.Max(0, work - st.Sim - st.Render - st.Present - st.Io);
            d.Stages.Add(new StageShare("Simulation (main thread)", st.Sim, st.Sim / work, ColSim));
            d.Stages.Add(new StageShare("Rendering", st.Render, st.Render / work, ColRender));
            d.Stages.Add(new StageShare("Present to screen", st.Present, st.Present / work, ColPresent));
            if (st.Io > 0.05) d.Stages.Add(new StageShare("Streaming & saving", st.Io, st.Io / work, ColIo));
            d.Stages.Add(new StageShare("Other", other, other / work, ColOther));

            int logical = Math.Max(1, hw.Cpu.Logical);
            string perCore = mon.CoreUsage.Length > 0
                ? "Per-core load: " + string.Join(" · ", mon.CoreUsage.Select((u, i) => "core " + i + " " + (u * 100).ToString("0") + "%"))
                : null;

            if (st.WarmupFrames > st.Count * 0.2)
            {
                d.Primary = Bottleneck.Warmup;
                d.Title = "Game is still loading (warm-up)";
                d.Summary = "The game is streaming world data. Judging performance now would be misleading, so GAPO waits until it settles.";
                d.Category = "Temporary";
                return d;
            }

            if (st.Render >= st.Sim)
            {
                if (st.RenderThreads <= 1 && logical > 1)
                {
                    d.Primary = Bottleneck.RenderSingleThread;
                    d.Title = "Rendering is stuck on one CPU thread";
                    d.Summary = "Rendering takes " + KnowledgeBase.F(st.Render) + " ms of each " + KnowledgeBase.F(work) + " ms frame, all on 1 of your " + logical + " CPU threads" + OtherCoresNote(mon) + ".";
                    d.Category = "B · game setting (render threads)";
                }
                else
                {
                    d.Primary = Bottleneck.RenderPixels;
                    var passes = new[] {
                        Tuple.Create("shadows", st.Shadow), Tuple.Create("terrain", st.Terrain), Tuple.Create("water", st.Water),
                        Tuple.Create("sky & clouds", st.Sky), Tuple.Create("fog", st.Fog), Tuple.Create("creatures & particles", st.Sprites), Tuple.Create("anti-aliasing", st.Post)
                    }.OrderByDescending(t => t.Item2).Take(3).ToList();
                    d.Title = "Rendering workload (pixels & effects)";
                    d.Summary = "Rendering takes " + KnowledgeBase.F(st.Render) + " ms of each " + KnowledgeBase.F(work) + " ms frame. Most expensive: " +
                        string.Join(", ", passes.Select(p => p.Item1 + " " + KnowledgeBase.F(p.Item2) + " ms")) + ".";
                    d.Category = "B · game settings";
                }
            }
            else
            {
                d.Primary = Bottleneck.CpuMainThread;
                d.Title = "CPU main thread (simulation)";
                d.Summary = "The simulation takes " + KnowledgeBase.F(st.Sim) + " ms of each " + KnowledgeBase.F(work) + " ms frame (" +
                    (int)st.EntitiesSimulated + " creatures simulated, physics " + KnowledgeBase.F(st.SimPhysics) + " ms).";
                d.Category = "C · engine limitation, mitigated by B · game settings";
                d.EngineLimitation = "This is a main-thread limitation. External settings cannot make the game simulation multithreaded. Reducing simulation distance is likely to provide the largest available improvement.";
            }

            // Evidence: utilisation is not the same as a bottleneck.
            if (mon.CoreUsage.Length > 1 && mon.TotalCpuPct > 0.5)
            {
                if (mon.BusiestCore - mon.TotalCpuPct >= 25)
                    d.Evidence.Add("Total CPU use is " + mon.TotalCpuPct.ToString("0") + "%, but the busiest core runs at " + mon.BusiestCore.ToString("0") + "% — overall CPU use hides a one-thread limit.");
                if (perCore != null) d.Evidence.Add(perCore);
            }
            if (mon.HottestThreadPct >= 0)
                d.Evidence.Add("The game's busiest thread uses " + mon.HottestThreadPct.ToString("0") + "% of one core (measured from outside the game via thread CPU times).");
            d.Evidence.Add("Frame work " + KnowledgeBase.F(work) + " ms vs a " + KnowledgeBase.F(d.BudgetMs) + " ms budget for " + targetFps.ToString("0") + " FPS.");

            // Secondary problems.
            var hw2 = hitchWindow ?? st;
            if (hw2.AutosaveHitches > 0)
                d.Secondary.Add("Storage hitch: a blocking autosave freezes the game for about " + hw2.AutosaveHitchMs.ToString("0") + " ms, " + hw2.AutosaveHitches + "× in the last " + (hw2.DurationMs / 1000).ToString("0") + " s (disk writes ~4 MB each).");
            if (mon.BackgroundCpuPct >= 15)
                d.Secondary.Add("Background apps use " + mon.BackgroundCpuPct.ToString("0") + "% of the CPU while you play.");
            if (mon.MemLoadPct >= 88 || (mon.AvailBytesMin > 0 && mon.AvailBytesMin < 700L * 1024 * 1024))
                d.Secondary.Add("Memory pressure: " + mon.MemLoadPct.ToString("0") + "% of RAM in use.");
            if (st.P99FrameMs > st.MedianFrameMs * 2.2 && hw2.AutosaveHitches == 0)
                d.Secondary.Add("Uneven frame pacing: the slowest 1% of frames take " + KnowledgeBase.F(st.P99FrameMs) + " ms vs a typical " + KnowledgeBase.F(st.MedianFrameMs) + " ms.");
            if (hw.Cpu.MhzLimit > 0 && hw.Cpu.MaxMhz > 0 && hw.Cpu.MhzLimit < hw.Cpu.MaxMhz * 0.8)
                d.Secondary.Add("Windows reports the CPU clock limited to " + hw.Cpu.MhzLimit + " of " + hw.Cpu.MaxMhz + " MHz (power or thermal limit).");

            if (work <= d.BudgetMs * 0.92 && hw2.AutosaveHitches == 0)
            {
                d.Primary = Bottleneck.TargetMet;
                d.Title = "Target reached";
                d.Summary = "Each frame takes " + KnowledgeBase.F(work) + " ms of work — inside the " + KnowledgeBase.F(d.BudgetMs) + " ms budget for " + targetFps.ToString("0") + " FPS.";
            }
            return d;
        }

        static string OtherCoresNote(MonitorSummary mon)
        {
            if (mon == null || mon.CoreUsage.Length < 2 || mon.TotalCpuPct < 0.5) return "";
            var sorted = mon.CoreUsage.OrderByDescending(u => u).ToArray();
            double others = sorted.Skip(1).Average() * 100;
            if (others < 35) return ", while the other cores are mostly idle (" + others.ToString("0") + "% on average)";
            if (mon.BackgroundCpuPct >= 15) return "; the other cores are busy with background apps (" + mon.BackgroundCpuPct.ToString("0") + "% of the CPU)";
            return "";
        }

        public static string ShortName(Bottleneck b)
        {
            switch (b)
            {
                case Bottleneck.CpuMainThread: return "CPU main thread";
                case Bottleneck.RenderSingleThread: return "Single-threaded rendering";
                case Bottleneck.RenderPixels: return "Rendering workload";
                case Bottleneck.StorageHitch: return "Storage hitches";
                case Bottleneck.Background: return "Background apps";
                case Bottleneck.Memory: return "Memory pressure";
                case Bottleneck.Pacing: return "Frame pacing";
                case Bottleneck.Warmup: return "Warm-up";
                case Bottleneck.TargetMet: return "Target reached";
                default: return "Unknown";
            }
        }
    }
}
