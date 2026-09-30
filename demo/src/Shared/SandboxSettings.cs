using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace Gapo.Shared
{
    public enum AntiAliasing { Off, FXAA, SSAA4x }
    public enum ShadowQuality { Off, High, Ultra }
    public enum WaterQuality { Off, Simple, Ultra }
    public enum CloudQuality { Off, Low, Ultra }
    public enum FogQuality { Off, Simple, Volumetric }
    public enum ParticleQuality { Off, Low, Medium, High, Ultra }
    public enum SimulationDistance { Near, Far, Unlimited }
    public enum TextureQuality { Low, High, Ultra }

    /// <summary>
    /// The sandbox game's settings. The game stores them in a plain INI file, the same way a
    /// real game would, and the optimiser changes the game by editing that file (it never
    /// touches the game's memory). The game hot-reloads the file and reports the revision it is
    /// running with, so the optimiser knows exactly when a change has taken effect.
    /// </summary>
    public sealed class SandboxSettings
    {
        // Graphics
        public int ResolutionScale = 100;              // % of the base internal resolution
        public AntiAliasing AntiAliasing = AntiAliasing.SSAA4x;
        public int RenderDistance = 1400;              // world units
        public bool TerrainLod = false;                // distance-based level of detail
        public ShadowQuality Shadows = ShadowQuality.Ultra;
        public WaterQuality WaterReflections = WaterQuality.Ultra;
        public CloudQuality Clouds = CloudQuality.Ultra;
        public FogQuality Fog = FogQuality.Volumetric;
        public ParticleQuality Particles = ParticleQuality.Ultra;
        public TextureQuality Textures = TextureQuality.Ultra;

        // Simulation
        public SimulationDistance SimulationDistance = SimulationDistance.Unlimited;
        public int EntityDensity = 100;                // % of the world's creatures
        public int PhysicsSubsteps = 8;

        // Performance
        public int RenderThreads = 1;
        public int FrameRateCap = 0;                   // 0 = unlimited
        public bool VSync = false;

        // System
        public int AutosaveSeconds = 10;               // 0 = off

        // Meta
        public int Revision = 0;
        public string ModifiedBy = "Game defaults (unoptimised)";

        public static SandboxSettings UnoptimisedDefaults() { return new SandboxSettings(); }

        public SandboxSettings Clone()
        {
            return (SandboxSettings)MemberwiseClone();
        }

        public bool IsUnoptimisedDefaults()
        {
            var d = UnoptimisedDefaults();
            d.Revision = Revision; d.ModifiedBy = ModifiedBy;
            return d.ToIni(false) == ToIni(false);
        }

        // ------------------------------------------------------------------ INI serialisation

        public string ToIni(bool withHeader = true)
        {
            var ci = CultureInfo.InvariantCulture;
            var sb = new StringBuilder();
            if (withHeader)
            {
                sb.AppendLine("; GAPO Sandbox settings. The game reloads this file automatically while it runs.");
                sb.AppendLine("; The GAPO Optimiser edits it one setting at a time and reverts anything that does not help.");
                sb.AppendLine();
            }
            sb.AppendLine("[Graphics]");
            sb.AppendLine("ResolutionScale=" + ResolutionScale.ToString(ci) + "        ; 50-100 (% of base resolution)");
            sb.AppendLine("AntiAliasing=" + AntiAliasing + "           ; Off, FXAA, SSAA4x");
            sb.AppendLine("RenderDistance=" + RenderDistance.ToString(ci) + "        ; 400-1600");
            sb.AppendLine("TerrainLOD=" + (TerrainLod ? "On" : "Off") + "                ; On, Off");
            sb.AppendLine("Shadows=" + Shadows + "                ; Off, High, Ultra");
            sb.AppendLine("WaterReflections=" + WaterReflections + "       ; Off, Simple, Ultra");
            sb.AppendLine("Clouds=" + Clouds + "                 ; Off, Low, Ultra");
            sb.AppendLine("Fog=" + Fog + "               ; Off, Simple, Volumetric");
            sb.AppendLine("Particles=" + Particles + "              ; Off, Low, Medium, High, Ultra");
            sb.AppendLine("Textures=" + Textures + "               ; Low, High, Ultra");
            sb.AppendLine();
            sb.AppendLine("[Simulation]");
            sb.AppendLine("SimulationDistance=" + SimulationDistance + "  ; Near, Far, Unlimited");
            sb.AppendLine("EntityDensity=" + EntityDensity.ToString(ci) + "          ; 25-100 (%)");
            sb.AppendLine("PhysicsSubsteps=" + PhysicsSubsteps.ToString(ci) + "          ; 1-8");
            sb.AppendLine();
            sb.AppendLine("[Performance]");
            sb.AppendLine("RenderThreads=" + RenderThreads.ToString(ci) + "            ; 1-64");
            sb.AppendLine("FrameRateCap=" + FrameRateCap.ToString(ci) + "             ; 0 = unlimited");
            sb.AppendLine("VSync=" + (VSync ? "On" : "Off"));
            sb.AppendLine();
            sb.AppendLine("[System]");
            sb.AppendLine("AutosaveSeconds=" + AutosaveSeconds.ToString(ci) + "         ; 0 = off");
            sb.AppendLine();
            sb.AppendLine("[Meta]");
            sb.AppendLine("Revision=" + Revision.ToString(ci));
            sb.AppendLine("ModifiedBy=" + ModifiedBy);
            return sb.ToString();
        }

        public static SandboxSettings FromIni(string text)
        {
            var s = new SandboxSettings();
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == ';' || line[0] == '[') continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string key = line.Substring(0, eq).Trim();
                string val = line.Substring(eq + 1);
                int sc = val.IndexOf(';');
                if (sc >= 0 && !key.Equals("ModifiedBy", StringComparison.OrdinalIgnoreCase)) val = val.Substring(0, sc);
                map[key] = val.Trim();
            }
            s.ResolutionScale = Clamp(Int(map, "ResolutionScale", s.ResolutionScale), 50, 100);
            s.AntiAliasing = Enum(map, "AntiAliasing", s.AntiAliasing);
            s.RenderDistance = Clamp(Int(map, "RenderDistance", s.RenderDistance), 300, 1600);
            s.TerrainLod = Bool(map, "TerrainLOD", s.TerrainLod);
            s.Shadows = Enum(map, "Shadows", s.Shadows);
            s.WaterReflections = Enum(map, "WaterReflections", s.WaterReflections);
            s.Clouds = Enum(map, "Clouds", s.Clouds);
            s.Fog = Enum(map, "Fog", s.Fog);
            s.Particles = Enum(map, "Particles", s.Particles);
            s.Textures = Enum(map, "Textures", s.Textures);
            s.SimulationDistance = Enum(map, "SimulationDistance", s.SimulationDistance);
            s.EntityDensity = Clamp(Int(map, "EntityDensity", s.EntityDensity), 10, 100);
            s.PhysicsSubsteps = Clamp(Int(map, "PhysicsSubsteps", s.PhysicsSubsteps), 1, 8);
            s.RenderThreads = Clamp(Int(map, "RenderThreads", s.RenderThreads), 1, 64);
            s.FrameRateCap = Clamp(Int(map, "FrameRateCap", s.FrameRateCap), 0, 1000);
            s.VSync = Bool(map, "VSync", s.VSync);
            s.AutosaveSeconds = Clamp(Int(map, "AutosaveSeconds", s.AutosaveSeconds), 0, 3600);
            s.Revision = Int(map, "Revision", 0);
            string by;
            if (map.TryGetValue("ModifiedBy", out by) && by.Length > 0) s.ModifiedBy = by;
            return s;
        }

        static int Clamp(int v, int lo, int hi) { return v < lo ? lo : (v > hi ? hi : v); }

        static int Int(Dictionary<string, string> m, string k, int def)
        {
            string v; int r;
            return m.TryGetValue(k, out v) && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out r) ? r : def;
        }

        static bool Bool(Dictionary<string, string> m, string k, bool def)
        {
            string v;
            if (!m.TryGetValue(k, out v)) return def;
            v = v.ToLowerInvariant();
            if (v == "on" || v == "true" || v == "1" || v == "yes") return true;
            if (v == "off" || v == "false" || v == "0" || v == "no") return false;
            return def;
        }

        static T Enum<T>(Dictionary<string, string> m, string k, T def) where T : struct
        {
            string v; T r;
            return m.TryGetValue(k, out v) && System.Enum.TryParse(v, true, out r) ? r : def;
        }

        // ------------------------------------------------------------------ file access

        /// <summary>Atomic save: write a temp file, then swap it in, so the game never reads half a file.</summary>
        public void Save(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, ToIni(), new UTF8Encoding(false));
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    if (File.Exists(path)) File.Delete(path);
                    File.Move(tmp, path);
                    return;
                }
                catch (IOException) { if (attempt > 20) throw; Thread.Sleep(15); }
                catch (UnauthorizedAccessException) { if (attempt > 20) throw; Thread.Sleep(15); }
            }
        }

        public static SandboxSettings Load(string path)
        {
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (var sr = new StreamReader(fs, Encoding.UTF8))
                        return FromIni(sr.ReadToEnd());
                }
                catch (FileNotFoundException) { return null; }
                catch (DirectoryNotFoundException) { return null; }
                catch (IOException) { if (attempt > 20) return null; Thread.Sleep(10); }
                catch (UnauthorizedAccessException) { if (attempt > 20) return null; Thread.Sleep(10); }
            }
        }

        public static string Describe(AntiAliasing v) { return v == AntiAliasing.SSAA4x ? "4× SSAA" : v.ToString(); }
    }

    public static class DemoPaths
    {
        public static string DataDir
        {
            get
            {
                string baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (string.IsNullOrEmpty(baseDir)) baseDir = Path.GetTempPath();
                return Path.Combine(baseDir, "GAPO-Demo");
            }
        }
        public static string SettingsFile { get { return Path.Combine(DataDir, "sandbox-settings.ini"); } }
        public static string CalibrationFile { get { return Path.Combine(DataDir, "calibration.ini"); } }
        public static string SaveFile { get { return Path.Combine(DataDir, "world.sav"); } }
        public static string ReportsDir { get { return Path.Combine(DataDir, "Reports"); } }
    }
}
