using System;
using System.Collections.Generic;
using Gapo.Optimiser.Hardware;
using Gapo.Shared;

namespace Gapo.Optimiser.Engine
{
    public enum ChangeKind { GameSetting, Windows, Hitch, Pacing, QualityRestore }
    public enum Resource { MainThread, Render, Memory, Storage, Scheduling, Pacing }

    /// <summary>One proposed change: a single setting moved one step, with its predicted effect.</summary>
    public sealed class Candidate
    {
        public string Setting = "", From = "", To = "";
        public ChangeKind Kind;
        public Resource Resource;
        public int VisualLoss;              // 0 (invisible) .. 10 (very visible)
        public double PredictedMs;          // predicted saving in frame work time
        public string Hypothesis = "";
        public Action<SandboxSettings> Apply;
        public string Key { get { return Setting + "→" + To; } }
        public string Title { get { return Setting + ": " + From + " → " + To; } }
        public double Value { get { return PredictedMs / (VisualLoss + 0.6); } }
    }

    /// <summary>
    /// What GAPO knows about the sandbox game — the "game × engine" knowledge-base entry from the
    /// spec (§8). It turns measured stage times into predicted savings for each one-step change, so the
    /// optimiser tests the most promising, least visible change first instead of guessing.
    /// </summary>
    public static class KnowledgeBase
    {
        public const string GameName = "GAPO Sandbox";
        public const string EngineName = "GAPO voxel engine (software renderer)";

        public static readonly string[] Profile =
        {
            "Simulation (creature AI + physics) runs on one main thread — engine limitation",
            "Rendering can be spread over several threads (Render threads setting)",
            "Ultra shadows are ray-marched per pixel: very expensive, looks almost the same as High",
            "Resolution and anti-aliasing multiply all pixel work",
            "Texture quality only changes memory use, not frame time",
            "Autosave blocks the main thread while it writes to disk (storage hitch)",
            "Streams world chunks for a few seconds after start-up (temporary stutter)",
        };

        static readonly string[] ParticleNames = { "Off", "Low", "Medium", "High", "Ultra" };

        public static List<Candidate> Generate(SandboxSettings cur, FrameStats st, HardwareProfile hw, bool memoryPressure)
        {
            var list = new List<Candidate>();
            var cpu = hw.Cpu;
            int physical = Math.Max(1, cpu.Cores > 0 ? cpu.Cores : cpu.Logical);
            int logical = Math.Max(1, cpu.Logical);

            // --- Render threads (hardware-aware: uses the detected core count) ---
            if (cur.RenderThreads < Math.Min(physical, 16) && logical > 1)
            {
                int to = Math.Min(physical, 16);
                if (to == 1) to = Math.Min(logical, 2);
                double eff = to >= 4 ? 0.8 : 0.85;
                list.Add(new Candidate
                {
                    Setting = "Render threads", From = cur.RenderThreads.ToString(), To = to.ToString(),
                    Kind = ChangeKind.GameSetting, Resource = Resource.Render, VisualLoss = 0,
                    PredictedMs = st.Render * (1 - cur.RenderThreads / (to * eff)),
                    Hypothesis = "Rendering runs on " + cur.RenderThreads + " thread" + (cur.RenderThreads == 1 ? "" : "s") + " but your CPU has " + physical + " cores. The render passes are parallel work, so spreading them over " + to + " threads should cut " + F(st.Render) + " ms of rendering substantially.",
                    Apply = s => s.RenderThreads = to,
                });
            }
            else if (cpu.Smt && logical > physical && cur.RenderThreads == physical && physical > 0)
            {
                list.Add(new Candidate
                {
                    Setting = "Render threads", From = cur.RenderThreads.ToString(), To = logical.ToString(),
                    Kind = ChangeKind.GameSetting, Resource = Resource.Render, VisualLoss = 0,
                    PredictedMs = st.Render * 0.12,
                    Hypothesis = "Your CPU runs 2 threads per core (SMT/Hyper-Threading). Whether that helps is game-specific, so it has to be measured, not assumed.",
                    Apply = s => s.RenderThreads = logical,
                });
            }

            // --- Rendering settings ---
            if (!cur.TerrainLod)
                list.Add(Make("Terrain LOD", "Off", "On", Resource.Render, 1, st.Terrain * 0.55,
                    "Terrain is sampled at full detail all the way to the horizon (" + F(st.Terrain) + " ms). Distant terrain covers few pixels, so coarser sampling there is nearly invisible.",
                    s => s.TerrainLod = true));

            if (cur.Shadows == ShadowQuality.Ultra)
                list.Add(Make("Shadows", "Ultra", "High", Resource.Render, 1, st.Shadow * 0.88,
                    "Ultra shadows ray-march every pixel every frame (" + F(st.Shadow) + " ms). High uses a baked shadow map that looks almost identical.",
                    s => s.Shadows = ShadowQuality.High));
            else if (cur.Shadows == ShadowQuality.High)
                list.Add(Make("Shadows", "High", "Off", Resource.Render, 5, st.Shadow * 0.95,
                    "Shadows cost " + F(st.Shadow) + " ms. Turning them off saves that but flattens the terrain visibly.",
                    s => s.Shadows = ShadowQuality.Off));

            if (cur.AntiAliasing == AntiAliasing.SSAA4x)
                list.Add(Make("Anti-aliasing", "4× SSAA", "FXAA", Resource.Render, 2, st.Terrain * 0.5 + (st.Shadow + st.Water + st.Sky + st.Fog + st.Sprites) * 0.75 + st.Post * 0.4,
                    "4× supersampling renders four times the pixels (" + st.RenderW * 2 + "×" + st.RenderH * 2 + ") and averages them. FXAA smooths edges in one cheap pass.",
                    s => s.AntiAliasing = AntiAliasing.FXAA));
            else if (cur.AntiAliasing == AntiAliasing.FXAA)
                list.Add(Make("Anti-aliasing", "FXAA", "Off", Resource.Render, 2, st.Post * 0.9,
                    "FXAA costs " + F(st.Post) + " ms; turning it off leaves jagged edges.",
                    s => s.AntiAliasing = AntiAliasing.Off));

            if (cur.WaterReflections == WaterQuality.Ultra)
                list.Add(Make("Water reflections", "Ultra", "Simple", Resource.Render, 2, st.Water * 0.85,
                    "Ultra reflections ray-march the terrain for every water pixel (" + F(st.Water) + " ms). Simple reflects the sky only.",
                    s => s.WaterReflections = WaterQuality.Simple));
            else if (cur.WaterReflections == WaterQuality.Simple)
                list.Add(Make("Water reflections", "Simple", "Off", Resource.Render, 3, st.Water * 0.9,
                    "Water shading costs " + F(st.Water) + " ms.", s => s.WaterReflections = WaterQuality.Off));

            if (cur.Clouds == CloudQuality.Ultra)
                list.Add(Make("Clouds", "Ultra", "Low", Resource.Render, 1, st.Sky * 0.75,
                    "Ultra clouds compute 5 octaves of noise per sky pixel every frame (" + F(st.Sky) + " ms); Low samples a pre-baked cloud texture that looks the same at this resolution.",
                    s => s.Clouds = CloudQuality.Low));
            else if (cur.Clouds == CloudQuality.Low)
                list.Add(Make("Clouds", "Low", "Off", Resource.Render, 3, st.Sky * 0.5,
                    "Sky and clouds cost " + F(st.Sky) + " ms.", s => s.Clouds = CloudQuality.Off));

            if (cur.Fog == FogQuality.Volumetric)
                list.Add(Make("Fog", "Volumetric", "Simple", Resource.Render, 1, st.Fog * 0.7,
                    "Volumetric fog marches every pixel's view ray (" + F(st.Fog) + " ms). Simple distance fog is one lookup per pixel.",
                    s => s.Fog = FogQuality.Simple));
            else if (cur.Fog == FogQuality.Simple)
                list.Add(Make("Fog", "Simple", "Off", Resource.Render, 4, st.Fog,
                    "Fog costs " + F(st.Fog) + " ms but hides the edge of the render distance.", s => s.Fog = FogQuality.Off));

            if (cur.Particles != ParticleQuality.Off)
            {
                int from = (int)cur.Particles, to = from - 1;
                double frac = 1 - (double)Simulation(to) / Simulation(from);
                int vis = from >= 3 ? 1 : (from == 2 ? 2 : 3);
                list.Add(Make("Particles", ParticleNames[from], ParticleNames[to], Resource.Render, vis, (st.SimParticles + st.Sprites * 0.6) * frac,
                    "Blossom particles cost " + F(st.SimParticles + st.Sprites * 0.6) + " ms (update + drawing).",
                    s => s.Particles = (ParticleQuality)to));
            }

            // --- Simulation (main thread) ---
            double simCore = st.SimEntities + st.SimPhysics;
            if (cur.SimulationDistance == SimulationDistance.Unlimited && st.EntitiesSimulated > 0)
                list.Add(Make("Simulation distance", "Unlimited", "Far", Resource.MainThread, 1, simCore * 0.65,
                    "All " + (int)st.EntitiesSimulated + " creatures are simulated every frame, even far out of sight. Their AI is all-pairs (O(n²)), so simulating only the ones within range cuts the cost disproportionately.",
                    s => s.SimulationDistance = SimulationDistance.Far));
            else if (cur.SimulationDistance == SimulationDistance.Far && st.EntitiesSimulated > 0)
                list.Add(Make("Simulation distance", "Far", "Near", Resource.MainThread, 2, simCore * 0.6,
                    (int)st.EntitiesSimulated + " creatures are still simulated. Near only animates the ones close to the camera.",
                    s => s.SimulationDistance = SimulationDistance.Near));

            if (cur.PhysicsSubsteps > 1 && st.SimPhysics > 0.05)
            {
                int to = cur.PhysicsSubsteps > 4 ? 4 : Math.Max(1, cur.PhysicsSubsteps / 2);
                int vis = to >= 4 ? 0 : (to >= 2 ? 1 : 2);
                list.Add(Make("Physics substeps", cur.PhysicsSubsteps.ToString(), to.ToString(), Resource.MainThread, vis, st.SimPhysics * (1 - (double)to / cur.PhysicsSubsteps),
                    "Collision is solved " + cur.PhysicsSubsteps + " times per frame (" + F(st.SimPhysics) + " ms). Slow-moving creatures don't need more than " + to + ".",
                    s => s.PhysicsSubsteps = to));
            }

            if (cur.EntityDensity > 50 && st.EntitiesTotal > 0)
            {
                int to = cur.EntityDensity > 75 ? 75 : 50;
                double r = (double)to / cur.EntityDensity;
                list.Add(Make("Creature density", cur.EntityDensity + "%", to + "%", Resource.MainThread, to == 75 ? 3 : 4, simCore * (1 - r * r) + st.Sprites * 0.2,
                    "Fewer creatures means quadratically less AI work, but the world looks emptier.",
                    s => s.EntityDensity = to));
            }

            // --- Distance & resolution (big visual cost, last resort) ---
            if (cur.RenderDistance > 700)
            {
                int to = cur.RenderDistance > 1150 ? 1100 : (cur.RenderDistance > 900 ? 850 : 650);
                double r = (double)to / cur.RenderDistance;
                int vis = to >= 1100 ? 2 : (to >= 850 ? 3 : 4);
                list.Add(Make("Render distance", cur.RenderDistance.ToString(), to.ToString(), Resource.Render, vis, st.Terrain * (1 - r) * (cur.TerrainLod ? 0.45 : 1.0),
                    "Terrain work grows with view distance (" + F(st.Terrain) + " ms).",
                    s => s.RenderDistance = to));
            }

            if (cur.ResolutionScale > 70)
            {
                int to = cur.ResolutionScale > 85 ? 85 : 70;
                double r = (double)to / cur.ResolutionScale;
                list.Add(Make("Resolution scale", cur.ResolutionScale + "%", to + "%", Resource.Render, to == 85 ? 3 : 5, st.PixelWork * (1 - r * r) + st.Post * (1 - r * r),
                    "Pixel work scales with the square of the resolution.",
                    s => s.ResolutionScale = to));
            }

            // --- Memory: only relevant when memory is actually under pressure ---
            if (memoryPressure && cur.Textures == TextureQuality.Ultra)
                list.Add(Make("Textures", "Ultra", "High", Resource.Memory, 2, 0,
                    "System memory is under pressure; High textures use 12 MB less.",
                    s => s.Textures = TextureQuality.High));

            return list;
        }

        static int Simulation(int particleLevel) { return new[] { 0, 700, 2000, 4500, 9000 }[particleLevel]; }

        static Candidate Make(string setting, string from, string to, Resource r, int visual, double predicted, string hypothesis, Action<SandboxSettings> apply)
        {
            return new Candidate { Setting = setting, From = from, To = to, Kind = ChangeKind.GameSetting, Resource = r, VisualLoss = visual, PredictedMs = Math.Max(0, predicted), Hypothesis = hypothesis, Apply = apply };
        }

        public static string F(double ms) { return ms < 10 ? ms.ToString("0.0") : ms.ToString("0"); }

        /// <summary>Settings that exist but are deliberately left alone, with the reason (shown to the user).</summary>
        public static List<string> NotWorthTouching(SandboxSettings cur, FrameStats st, bool memoryPressure)
        {
            var l = new List<string>();
            if (!memoryPressure && cur.Textures != TextureQuality.Low)
                l.Add("Texture quality — memory isn't under pressure, and textures don't cost frame time in this game. Lowering them would cost visuals for no gain.");
            if (st.Present > 0 && st.Present < 3)
                l.Add("Window/present mode — copying the frame to the screen takes only " + F(st.Present) + " ms.");
            return l;
        }
    }
}
