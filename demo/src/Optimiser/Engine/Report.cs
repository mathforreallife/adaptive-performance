using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Gapo.Optimiser.Hardware;
using Gapo.Optimiser.Monitoring;
using Gapo.Shared;

namespace Gapo.Optimiser.Engine
{
    public enum Goal { Balanced, MaxFps, Quality, Smooth }
    public enum Decision { Kept, Reverted, Skipped, Failed }

    public sealed class ExperimentLog
    {
        public int Index;
        public string Title = "", Setting = "", Hypothesis = "", Reason = "", BottleneckAfter = "";
        public ChangeKind Kind;
        public int VisualLoss;
        public double PredictedMs;
        public FrameStats Before, After;
        public double GainPct, PaceGainPct, Seconds, SelfCpuPct;
        public Decision Decision;
    }

    public sealed class Recommendation
    {
        public int Severity;           // 0 info, 1 suggestion, 2 important
        public string Title = "", Detail = "", ActionLabel, ActionTarget;
    }

    public sealed class BackgroundApp
    {
        public string Name = "", Kind = "";
        public double CpuPct;
        public long MemBytes;
        public int Processes;
        public bool Protected;         // security / system: never suggest closing
    }

    public sealed class OptimisationReport
    {
        public DateTime Started, Finished;
        public Goal Goal;
        public double TargetFps;
        public string TargetReason = "";
        public HardwareProfile Hardware;
        public FrameStats Baseline, Final, BaselineHitches, FinalHitches;
        public MonitorSummary BaselineMonitor, FinalMonitor;
        public Diagnosis BaselineDiagnosis, FinalDiagnosis;
        public List<ExperimentLog> Experiments = new List<ExperimentLog>();
        public List<string> NotTouched = new List<string>();
        public List<Recommendation> Recommendations = new List<Recommendation>();
        public List<BackgroundApp> BackgroundApps = new List<BackgroundApp>();
        public List<string> HardwareDecisions = new List<string>();
        public SandboxSettings OriginalSettings, FinalSettings;
        public double NoiseFloorPct;
        public int MeasuredFrames, Windows;
        public double SelfCpuAvgPct, SelfCpuPeakPct, SelfCpuInWindowsPct, SelfPrivateMB;
        public bool Completed, Cancelled;
        public string Error;
        public string PacingNote = "";
        public List<float> BaselineTrace = new List<float>(), FinalTrace = new List<float>();

        public double FpsGainPct { get { return Baseline == null || Final == null || Baseline.AvgFps <= 0 ? 0 : (Final.AvgFps / Baseline.AvgFps - 1) * 100; } }
        public double Low1GainPct { get { return Baseline == null || Final == null || Baseline.Low1Fps <= 0 ? 0 : (Final.Low1Fps / Baseline.Low1Fps - 1) * 100; } }
        public double WorkSpeedup { get { return Baseline == null || Final == null || Final.MedianWorkMs <= 0 ? 1 : Baseline.MedianWorkMs / Final.MedianWorkMs; } }
        public int Kept { get { return Experiments.Count(e => e.Decision == Decision.Kept); } }
        public int Reverted { get { return Experiments.Count(e => e.Decision == Decision.Reverted); } }

        // ================================================================= text export

        public string ToText()
        {
            var ci = CultureInfo.InvariantCulture;
            var sb = new StringBuilder();
            sb.AppendLine("GAPO — Adaptive Performance report");
            sb.AppendLine("==================================");
            sb.AppendLine("Game:      " + KnowledgeBase.GameName + " (" + KnowledgeBase.EngineName + ")");
            sb.AppendLine("Goal:      " + GoalName(Goal) + " · target " + TargetFps.ToString("0", ci) + " FPS (" + TargetReason + ")");
            sb.AppendLine("When:      " + Started.ToString("yyyy-MM-dd HH:mm", ci) + " · took " + (Finished - Started).TotalSeconds.ToString("0", ci) + " s");
            sb.AppendLine();
            if (Baseline != null && Final != null)
            {
                sb.AppendLine("RESULT");
                sb.AppendLine("  Performance      " + Signed(FpsGainPct) + "% average FPS");
                sb.AppendLine("  Average FPS      " + Baseline.AvgFps.ToString("0.0", ci) + " → " + Final.AvgFps.ToString("0.0", ci));
                sb.AppendLine("  1% low FPS       " + Baseline.Low1Fps.ToString("0.0", ci) + " → " + Final.Low1Fps.ToString("0.0", ci));
                sb.AppendLine("  0.1% low FPS     " + Baseline.Low01Fps.ToString("0.0", ci) + " → " + Final.Low01Fps.ToString("0.0", ci));
                sb.AppendLine("  Frame work       " + Baseline.MedianWorkMs.ToString("0.0", ci) + " ms → " + Final.MedianWorkMs.ToString("0.0", ci) + " ms (" + WorkSpeedup.ToString("0.0", ci) + "× less work per frame)");
                if (BaselineHitches != null && FinalHitches != null)
                    sb.AppendLine("  Stutters/min     " + BaselineHitches.StuttersPerMin.ToString("0.0", ci) + " → " + FinalHitches.StuttersPerMin.ToString("0.0", ci));
                if (!string.IsNullOrEmpty(PacingNote)) sb.AppendLine("  Pacing           " + PacingNote);
                sb.AppendLine("  Measured         " + MeasuredFrames + " frames in " + Windows + " windows · noise floor ±" + NoiseFloorPct.ToString("0.0", ci) + "%");
                sb.AppendLine("  GAPO overhead    " + SelfCpuAvgPct.ToString("0.00", ci) + "% CPU average (" + SelfCpuInWindowsPct.ToString("0.00", ci) + "% during measurements, peak " + SelfCpuPeakPct.ToString("0.0", ci) + "%) · " + SelfPrivateMB.ToString("0", ci) + " MB RAM");
                sb.AppendLine();
            }
            if (BaselineDiagnosis != null)
            {
                sb.AppendLine("DIAGNOSIS (before)");
                sb.AppendLine("  " + BaselineDiagnosis.Title + " — " + BaselineDiagnosis.Summary);
                if (BaselineDiagnosis.EngineLimitation != null) sb.AppendLine("  " + BaselineDiagnosis.EngineLimitation);
                foreach (var e in BaselineDiagnosis.Evidence) sb.AppendLine("  · " + e);
                foreach (var e in BaselineDiagnosis.Secondary) sb.AppendLine("  ! " + e);
                sb.AppendLine();
            }
            sb.AppendLine("WHAT GAPO TRIED (one change at a time, each measured on the same benchmark flight)");
            foreach (var e in Experiments)
            {
                sb.AppendLine(string.Format(ci, "  {0,2}. [{1}] {2}", e.Index, e.Decision.ToString().ToUpperInvariant(), e.Title));
                sb.AppendLine("      Why:    " + e.Hypothesis);
                if (e.Kind == ChangeKind.Hitch && e.Before != null)
                    sb.AppendLine(string.Format(ci, "      Result: save freezes {0} in {1:0} s before; see decision for after", e.Before.AutosaveHitches, e.Before.DurationMs / 1000));
                else if (e.Kind == ChangeKind.QualityRestore && e.Before != null && e.After != null)
                    sb.AppendLine(string.Format(ci, "      Result: frame work {0:0.0} → {1:0.0} ms with the quality restored", e.Before.MedianWorkMs, e.After.MedianWorkMs));
                else if (e.Before != null && e.After != null)
                    sb.AppendLine(string.Format(ci, "      Result: frame work {0:0.0} → {1:0.0} ms ({2}%), FPS {3:0.0} → {4:0.0}, visual cost {5}/10, predicted −{6:0.0} ms",
                        e.Before.MedianWorkMs, e.After.MedianWorkMs, Signed(e.GainPct), e.Before.AvgFps, e.After.AvgFps, e.VisualLoss, e.PredictedMs));
                sb.AppendLine("      Decision: " + e.Reason);
                if (!string.IsNullOrEmpty(e.BottleneckAfter)) sb.AppendLine("      Bottleneck now: " + e.BottleneckAfter);
            }
            if (NotTouched.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("CONSIDERED BUT NOT CHANGED");
                foreach (var n in NotTouched) sb.AppendLine("  · " + n);
            }
            if (BackgroundApps.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("BACKGROUND ACTIVITY WHILE THE GAME RAN (top 6)");
                foreach (var a in BackgroundApps.Take(6))
                    sb.AppendLine(string.Format(ci, "  · {0} ({1}{2}) {3:0.0}% CPU · {4}", a.Name, a.Kind, a.Protected ? ", never touched" : "", a.CpuPct, Gb(a.MemBytes)));
            }
            if (Recommendations.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("RECOMMENDATIONS (not changed automatically)");
                foreach (var r in Recommendations) sb.AppendLine("  · " + r.Title + " — " + r.Detail);
            }
            if (Hardware != null)
            {
                sb.AppendLine();
                sb.AppendLine("YOUR PC");
                foreach (var line in HardwareLines(Hardware)) sb.AppendLine("  " + line);
                foreach (var h in HardwareDecisions) sb.AppendLine("  → " + h);
            }
            if (OriginalSettings != null && FinalSettings != null)
            {
                sb.AppendLine();
                sb.AppendLine("SETTINGS BEFORE");
                sb.AppendLine(Indent(OriginalSettings.ToIni(false)));
                sb.AppendLine("SETTINGS AFTER");
                sb.AppendLine(Indent(FinalSettings.ToIni(false)));
            }
            return sb.ToString();
        }

        static string Indent(string s) { return "  " + s.Replace("\n", "\n  "); }
        public static string Signed(double v) { return (v >= 0 ? "+" : "") + v.ToString("0", CultureInfo.InvariantCulture); }

        public static string GoalName(Goal g)
        {
            switch (g)
            {
                case Goal.MaxFps: return "Maximum FPS";
                case Goal.Quality: return "Quality";
                case Goal.Smooth: return "Smoothness";
                default: return "Balanced";
            }
        }

        public string Save()
        {
            Directory.CreateDirectory(DemoPaths.ReportsDir);
            string path = Path.Combine(DemoPaths.ReportsDir, "GAPO-report-" + Started.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".txt");
            File.WriteAllText(path, ToText().Replace("\n", "\r\n"), new UTF8Encoding(true));
            return path;
        }

        public static IEnumerable<string> HardwareLines(HardwareProfile hw)
        {
            var c = hw.Cpu;
            yield return "CPU      " + c.Name + " · " + c.Cores + " cores / " + c.Logical + " threads" + (c.Hybrid ? " (" + c.PerformanceCores + "P + " + c.EfficiencyCores + "E)" : "") +
                (c.MaxMhz > 0 ? " · " + (c.MaxMhz / 1000.0).ToString("0.0#", CultureInfo.InvariantCulture) + " GHz" : "") + (c.L3KB > 0 ? " · L3 " + (c.L3KB / 1024) + " MB" : "");
            foreach (var g in hw.Gpus)
                yield return "GPU      " + g.Name + (g.VramBytes > 0 ? " · " + Gb(g.VramBytes) + (g.Integrated ? " shared" : " VRAM") : "") + (g.Integrated ? " · integrated" : "") + (g.Virtual ? " · virtual adapter" : "") +
                    (g.DriverVersion.Length > 0 ? " · driver " + g.DriverVersion + (g.DriverDate.Length > 0 ? " (" + g.DriverDate + ")" : "") : "");
            var m = hw.Memory;
            string mods = m.Modules.Count > 0 ? " · " + ModuleSummary(m) : "";
            yield return "Memory   " + Gb(m.TotalBytes) + mods + " · " + m.ChannelMode + " · " + m.LoadPct + "% in use";
            foreach (var s in hw.Storage)
                yield return "Storage  " + s.Drive + " (" + s.Role + ") " + s.MediaType + (s.BusType.Length > 0 ? " " + s.BusType : "") + (s.Model.Length > 0 ? " · " + s.Model : "") + " · " + Gb(s.FreeBytes) + " free of " + Gb(s.TotalBytes);
            var d = hw.Display;
            yield return "Display  " + d.Width + "×" + d.Height + " @ " + (d.RefreshUnknown ? "? Hz (not reported)" : d.RefreshHz + " Hz") + " · " + d.Monitors + " monitor" + (d.Monitors == 1 ? "" : "s") + " · " + d.ScalePct + "% scale";
            var p = hw.Power;
            yield return "Power    " + (p.HasBattery ? (p.OnAc ? "Laptop on AC" : "Laptop on battery") + (p.BatteryPct >= 0 ? " (" + p.BatteryPct + "%)" : "") : "Desktop (AC)") + " · plan " + p.PlanName + (p.Overlay.Length > 0 ? " · mode " + p.Overlay : "");
            var o = hw.Os;
            yield return "Windows  " + o.Name + (o.Version.Length > 0 ? " " + o.Version : "") + " (build " + o.Build + ")" + " · Game Mode " + Tri(o.GameMode) + " · HAGS " + Tri(o.Hags);
            yield return "Class    " + ClassName(hw.Class) + " — " + hw.ClassReason;
        }

        public static string ModuleSummary(MemoryInfo m)
        {
            if (m.Modules.Count == 0) return "";
            var first = m.Modules[0];
            bool same = m.Modules.TrueForAll(x => x.CapacityBytes == first.CapacityBytes);
            string sizes = same ? m.Modules.Count + "× " + Gb(first.CapacityBytes) : string.Join(" + ", m.Modules.ConvertAll(x => Gb(x.CapacityBytes)).ToArray());
            return sizes + (first.Type.Length > 0 ? " " + first.Type : "") + (first.ConfiguredMts > 0 ? "-" + first.ConfiguredMts : "");
        }

        static string Tri(bool? b) { return b == null ? "unknown" : (b.Value ? "on" : "off"); }
        public static string ClassName(PcClass c) { return c == PcClass.Entry ? "Entry-level PC" : c == PcClass.Performance ? "Performance PC" : c == PcClass.Mainstream ? "Mainstream PC" : "Unknown"; }

        public static string Gb(long bytes)
        {
            double gb = bytes / 1073741824.0;
            if (gb >= 10) return gb.ToString("0", CultureInfo.InvariantCulture) + " GB";
            if (gb >= 1) return gb.ToString("0.#", CultureInfo.InvariantCulture) + " GB";
            return (bytes / 1048576.0).ToString("0", CultureInfo.InvariantCulture) + " MB";
        }
    }

    /// <summary>Evidence-based suggestions for things GAPO deliberately does not change by itself.</summary>
    public static class Advisor
    {
        static readonly Dictionary<string, string[]> Known = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            { "chrome", new[] { "Google Chrome", "Browser" } }, { "msedge", new[] { "Microsoft Edge", "Browser" } }, { "firefox", new[] { "Firefox", "Browser" } },
            { "opera", new[] { "Opera", "Browser" } }, { "brave", new[] { "Brave", "Browser" } }, { "discord", new[] { "Discord", "Chat" } },
            { "steamwebhelper", new[] { "Steam", "Launcher" } }, { "steam", new[] { "Steam", "Launcher" } }, { "epicgameslauncher", new[] { "Epic Games Launcher", "Launcher" } },
            { "obs64", new[] { "OBS Studio", "Recording" } }, { "onedrive", new[] { "OneDrive", "Cloud sync" } }, { "dropbox", new[] { "Dropbox", "Cloud sync" } },
            { "ms-teams", new[] { "Microsoft Teams", "Chat" } }, { "teams", new[] { "Microsoft Teams", "Chat" } }, { "spotify", new[] { "Spotify", "Media" } },
            { "zoom", new[] { "Zoom", "Chat" } }, { "code", new[] { "Visual Studio Code", "App" } }, { "vmmem", new[] { "Virtual machine / WSL", "Virtualisation" } },
            { "vmmemwsl", new[] { "WSL", "Virtualisation" } }, { "docker desktop", new[] { "Docker Desktop", "Virtualisation" } },
            { "icue", new[] { "Corsair iCUE", "RGB utility" } }, { "armourycrate.service", new[] { "Armoury Crate", "RGB utility" } }, { "razer synapse service", new[] { "Razer Synapse", "RGB utility" } },
            { "msedgewebview2", new[] { "Edge WebView (inside apps)", "App" } }, { "searchindexer", new[] { "Windows Search indexing", "Windows" } },
            { "tiworker", new[] { "Windows Update", "Windows" } }, { "mousocoreworker", new[] { "Windows Update", "Windows" } }, { "wuauclt", new[] { "Windows Update", "Windows" } },
            { "wmiprvse", new[] { "WMI provider host", "Windows" } },
        };

        static readonly HashSet<string> NeverTouch = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "msmpeng", "mssense", "nissrv", "securityhealthservice", "system", "registry", "smss", "csrss", "wininit", "services", "lsass", "svchost",
            "dwm", "winlogon", "fontdrvhost", "memory compression", "system idle", "idle", "audiodg", "explorer", "sihost", "ctfmon", "conhost",
        };

        public static List<BackgroundApp> Contention(ProcessSnapshot a, ProcessSnapshot b, double seconds, int gamePid, int selfPid)
        {
            var prev = new Dictionary<int, long>();
            foreach (var p in a.Processes) prev[p.Pid] = p.CpuTicks;
            var byName = new Dictionary<string, BackgroundApp>(StringComparer.OrdinalIgnoreCase);
            int logical = Environment.ProcessorCount;
            foreach (var p in b.Processes)
            {
                if (p.Pid == gamePid || p.Pid == selfPid || p.Pid == 0) continue;
                long before;
                double cpu = prev.TryGetValue(p.Pid, out before) ? (p.CpuTicks - before) / 10000.0 / (seconds * 1000) / logical * 100 : 0;
                string key = p.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? p.Name.Substring(0, p.Name.Length - 4) : p.Name;
                string[] info;
                string display = Known.TryGetValue(key, out info) ? info[0] : key;
                BackgroundApp app;
                if (!byName.TryGetValue(display, out app))
                {
                    app = new BackgroundApp { Name = display, Kind = info != null ? info[1] : (NeverTouch.Contains(key) ? "Windows" : "App"), Protected = NeverTouch.Contains(key) || key.StartsWith("MsMpEng", StringComparison.OrdinalIgnoreCase) };
                    byName[display] = app;
                }
                app.CpuPct += Math.Max(0, cpu);
                app.MemBytes += p.PrivateBytes;
                app.Processes++;
            }
            return byName.Values.OrderByDescending(x => x.CpuPct).ThenByDescending(x => x.MemBytes).ToList();
        }

        public static List<Recommendation> Build(HardwareProfile hw, MonitorSummary mon, List<BackgroundApp> apps, FrameStats hitches, OptimisationReport rep)
        {
            var list = new List<Recommendation>();
            var p = hw.Power;
            if (p.HasBattery && !p.OnAc)
                list.Add(new Recommendation { Severity = 2, Title = "Plug in your laptop", Detail = "You're on battery, so Windows limits CPU and GPU speed to save power. Plugged in, the same settings run faster." });
            if (p.PowerSaver)
                list.Add(new Recommendation { Severity = 2, Title = "Power mode is set to save energy", Detail = "The active plan/mode (" + p.PlanName + (p.Overlay.Length > 0 ? ", " + p.Overlay : "") + ") holds the CPU back. Switch to Balanced or Best performance while gaming.", ActionLabel = "Open power settings", ActionTarget = "ms-settings:powersleep" });

            var closable = apps.Where(a => !a.Protected && a.Kind != "Windows" && a.CpuPct >= 1.5).Take(4).ToList();
            double closableCpu = closable.Sum(a => a.CpuPct);
            if (mon.BackgroundCpuPct >= 10 && closableCpu >= 4)
                list.Add(new Recommendation { Severity = 1, Title = "Close busy background apps", Detail = "While the game ran, " + string.Join(", ", closable.Select(a => a.Name + " (" + a.CpuPct.ToString("0") + "% CPU)")) + " competed for the CPU. GAPO never closes apps for you." });
            if (mon.MemLoadPct >= 85)
            {
                var ram = apps.Where(a => !a.Protected && a.Kind != "Windows").OrderByDescending(a => a.MemBytes).Take(3).ToList();
                list.Add(new Recommendation { Severity = 2, Title = "Free up memory", Detail = "RAM is " + mon.MemLoadPct.ToString("0") + "% full, which causes stutter when the game streams data. Biggest users: " + string.Join(", ", ram.Select(a => a.Name + " " + OptimisationReport.Gb(a.MemBytes))) + "." });
            }
            var g = hw.PrimaryGpu;
            if (hw.Memory.SingleChannel && (g.Integrated || g.Virtual))
                list.Add(new Recommendation { Severity = 1, Title = "Memory runs in single-channel mode", Detail = "Only one RAM module is installed. Integrated graphics share system memory, so a second matching module (dual-channel) can raise graphics performance a lot. Hardware upgrade — GAPO can't change this." });
            else if (hw.Memory.SingleChannel)
                list.Add(new Recommendation { Severity = 0, Title = "Memory runs in single-channel mode", Detail = "A second matching RAM module would double memory bandwidth, which helps CPU-heavy games." });
            if (hw.Memory.TotalBytes > 0 && hw.Memory.TotalBytes < 6L * 1073741824)
                list.Add(new Recommendation { Severity = 1, Title = "Low RAM for modern games", Detail = "This PC has " + OptimisationReport.Gb(hw.Memory.TotalBytes) + " of RAM. Keep other apps closed while playing; 8–16 GB is the comfortable range today." });

            foreach (var s in hw.Storage)
            {
                if (s.Role.Contains("Game") && s.MediaType == "HDD")
                    list.Add(new Recommendation { Severity = 1, Title = "Game is on a hard drive", Detail = "Hard drives make loading and saving hitches much longer" + (hitches != null && hitches.AutosaveHitches > 0 ? " (autosave froze the game for ~" + hitches.AutosaveHitchMs.ToString("0") + " ms)" : "") + ". Installing games on an SSD helps." });
                if (s.TotalBytes > 0 && s.FreePct < 10)
                    list.Add(new Recommendation { Severity = 1, Title = "Drive " + s.Drive + " is almost full", Detail = "Only " + s.FreePct.ToString("0") + "% free. Low free space slows the pagefile, shader caches and updates.", ActionLabel = "Open storage settings", ActionTarget = "ms-settings:storagesense" });
            }
            var os = hw.Os;
            if (os.GameMode == false)
                list.Add(new Recommendation { Severity = 0, Title = "Windows Game Mode is off", Detail = "Game Mode reduces background activity (like Windows Update) while you play.", ActionLabel = "Open Game Mode", ActionTarget = "ms-settings:gaming-gamemode" });
            if (os.BackgroundRecording == true)
                list.Add(new Recommendation { Severity = 1, Title = "Background recording is on", Detail = "Xbox Game Bar is recording the last minutes of gameplay in the background, which costs performance on modest PCs.", ActionLabel = "Open capture settings", ActionTarget = "ms-settings:gaming-gamedvr" });
            if (hw.Display.RefreshUnknown)
                list.Add(new Recommendation { Severity = 0, Title = "Display refresh rate not reported", Detail = "The display driver didn't report a refresh rate (typical for virtual machines and basic display drivers), so GAPO assumed 60 Hz." });
            if (g.Virtual)
                list.Add(new Recommendation { Severity = 0, Title = "No hardware graphics driver", Detail = "Windows is using a basic/virtual display adapter (" + g.Name + "). Real games need the GPU vendor's driver." });
            if (rep.BaselineDiagnosis != null && rep.BaselineDiagnosis.EngineLimitation != null)
                list.Add(new Recommendation { Severity = 0, Title = "Engine limitation detected", Detail = rep.BaselineDiagnosis.EngineLimitation });
            return list.OrderByDescending(r => r.Severity).ToList();
        }
    }
}
