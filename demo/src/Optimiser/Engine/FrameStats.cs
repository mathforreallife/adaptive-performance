using System;
using System.Collections.Generic;
using Gapo.Shared;

namespace Gapo.Optimiser.Engine
{
    /// <summary>Frame-time statistics for one measurement window.</summary>
    public sealed class FrameStats
    {
        public int Count;
        public double DurationMs;
        public double AvgFps, Low1Fps, Low01Fps;
        public double MeanFrameMs, MedianFrameMs, P95FrameMs, P99FrameMs, MaxFrameMs, StdDevFrameMs;
        public double MedianWorkMs, MeanWorkMs;
        public int Stutters;
        public double StuttersPerMin;
        public int AutosaveHitches, WarmupFrames, GcFrames;
        public double AutosaveHitchMs;
        // Stage costs (medians, so one hitch cannot skew them)
        public double Sim, SimEntities, SimPhysics, SimParticles, Render, Terrain, Shadow, Water, Sky, Fog, Sprites, Post, Present, Io, Wait;
        public double EntitiesSimulated, EntitiesTotal, Particles;
        public int RenderThreads, RenderW, RenderH, Revision;

        public static FrameStats Compute(IList<FrameRecord> frames)
        {
            var s = new FrameStats();
            int n = frames.Count;
            s.Count = n;
            if (n == 0) return s;
            var ft = new double[n];
            var work = new double[n];
            double sum = 0;
            for (int i = 0; i < n; i++) { ft[i] = frames[i].FrameMs; work[i] = frames[i].WorkMs; sum += ft[i]; }
            s.DurationMs = sum;
            s.MeanFrameMs = sum / n;
            s.AvgFps = 1000.0 / s.MeanFrameMs;
            double var = 0;
            for (int i = 0; i < n; i++) var += (ft[i] - s.MeanFrameMs) * (ft[i] - s.MeanFrameMs);
            s.StdDevFrameMs = Math.Sqrt(var / n);

            var sorted = (double[])ft.Clone();
            Array.Sort(sorted);
            s.MedianFrameMs = Percentile(sorted, 0.5);
            s.P95FrameMs = Percentile(sorted, 0.95);
            s.P99FrameMs = Percentile(sorted, 0.99);
            s.MaxFrameMs = sorted[n - 1];
            s.Low1Fps = 1000.0 / MeanOfWorst(sorted, 0.01);
            s.Low01Fps = 1000.0 / MeanOfWorst(sorted, 0.001);

            var ws = (double[])work.Clone();
            Array.Sort(ws);
            s.MedianWorkMs = Percentile(ws, 0.5);
            double wsum = 0; foreach (var w in work) wsum += w;
            s.MeanWorkMs = wsum / n;

            // A stutter is a frame far slower than the typical frame (not just a slow frame), or a
            // frame the engine marked as a save/stream hitch that was visibly longer than usual.
            double threshold = Math.Max(s.MedianFrameMs * 1.8, s.MedianFrameMs + 20.0);
            for (int i = 0; i < n; i++)
            {
                var f = frames[i];
                bool marked = (f.Flags & (int)(FrameFlags.Autosave | FrameFlags.Warmup)) != 0 && ft[i] > s.MedianFrameMs + 15.0;
                if (ft[i] > threshold || marked) s.Stutters++;
                if ((f.Flags & (int)FrameFlags.Autosave) != 0) { s.AutosaveHitches++; s.AutosaveHitchMs += f.IoMs; }
                if ((f.Flags & (int)FrameFlags.Warmup) != 0) s.WarmupFrames++;
                if ((f.Flags & (int)FrameFlags.GarbageCollect) != 0) s.GcFrames++;
            }
            if (s.AutosaveHitches > 0) s.AutosaveHitchMs /= s.AutosaveHitches;
            s.StuttersPerMin = sum > 0 ? s.Stutters / (sum / 60000.0) : 0;

            s.Sim = Median(frames, f => f.SimMs);
            s.SimEntities = Median(frames, f => f.SimEntitiesMs);
            s.SimPhysics = Median(frames, f => f.SimPhysicsMs);
            s.SimParticles = Median(frames, f => f.SimParticlesMs);
            s.Render = Median(frames, f => f.RenderMs);
            s.Terrain = Median(frames, f => f.TerrainMs);
            s.Shadow = Median(frames, f => f.ShadowMs);
            s.Water = Median(frames, f => f.WaterMs);
            s.Sky = Median(frames, f => f.SkyMs);
            s.Fog = Median(frames, f => f.FogMs);
            s.Sprites = Median(frames, f => f.SpritesMs);
            s.Post = Median(frames, f => f.PostMs);
            s.Present = Median(frames, f => f.PresentMs);
            s.Io = Median(frames, f => f.IoMs);
            s.Wait = Median(frames, f => f.WaitMs);
            s.EntitiesSimulated = Median(frames, f => f.EntitiesSimulated);
            s.EntitiesTotal = Median(frames, f => f.EntitiesTotal);
            s.Particles = Median(frames, f => f.ParticlesActive);
            var last = frames[n - 1];
            s.RenderThreads = last.RenderThreads; s.RenderW = last.RenderWidth; s.RenderH = last.RenderHeight; s.Revision = last.Revision;
            return s;
        }

        static double Percentile(double[] sorted, double p)
        {
            if (sorted.Length == 1) return sorted[0];
            double idx = p * (sorted.Length - 1);
            int lo = (int)Math.Floor(idx), hi = Math.Min(sorted.Length - 1, lo + 1);
            return sorted[lo] + (sorted[hi] - sorted[lo]) * (idx - lo);
        }

        static double MeanOfWorst(double[] sorted, double fraction)
        {
            int k = Math.Max(1, (int)Math.Ceiling(sorted.Length * fraction));
            double s = 0;
            for (int i = sorted.Length - k; i < sorted.Length; i++) s += sorted[i];
            return s / k;
        }

        static double Median(IList<FrameRecord> frames, Func<FrameRecord, double> sel)
        {
            var a = new double[frames.Count];
            for (int i = 0; i < a.Length; i++) a[i] = sel(frames[i]);
            Array.Sort(a);
            return Percentile(a, 0.5);
        }

        /// <summary>Pixel-shading work that scales with resolution and anti-aliasing.</summary>
        public double PixelWork { get { return Shadow + Water + Sky + Fog + Sprites + Terrain * 0.5; } }
    }
}
