using System;
using System.Collections.Generic;
using Gapo.Sandbox.Contract;

namespace Gapo.Sandbox
{
    public sealed class RunStatistics
    {
        const int MaximumFrames = 400000;
        const float HitchFloorMs = 20f;
        const float HitchMedianFactor = 1.8f;

        readonly List<float> _frameMs = new List<float>(16384);
        readonly List<float> _workMs = new List<float>(16384);
        double _simMs, _renderMs, _presentMs, _ioMs, _waitMs;

        const int RecentFrames = 300;
        readonly float[] _recentFrame = new float[RecentFrames], _recentWork = new float[RecentFrames], _recentSim = new float[RecentFrames],
            _recentRender = new float[RecentFrames], _recentWait = new float[RecentFrames];
        int _recentCount;

        public double RecentMeanFrameMs { get { return RecentMean(_recentFrame); } }
        public double RecentMeanWorkMs { get { return RecentMean(_recentWork); } }
        public double RecentMeanSimulationMs { get { return RecentMean(_recentSim); } }
        public double RecentMeanRenderMs { get { return RecentMean(_recentRender); } }
        public double RecentMeanWaitMs { get { return RecentMean(_recentWait); } }

        double RecentMean(float[] values)
        {
            int count = Math.Min(_recentCount, RecentFrames);
            if (count == 0) return 0;
            double sum = 0;
            for (int i = 0; i < count; i++) sum += values[i];
            return sum / count;
        }

        public int Frames { get { return _frameMs.Count; } }
        public int WarmupFrames { get; private set; }
        public int AutosaveFrames { get; private set; }
        public float LongestAutosaveMs { get; private set; }
        public int SettingsReloads { get; private set; }

        public void Add(ref FrameRecord record)
        {
            var flags = record.FrameFlags;
            if ((flags & FrameFlags.SettingsReload) != 0) SettingsReloads++;
            if ((flags & FrameFlags.Warmup) != 0)
            {
                WarmupFrames++;
                return;
            }

            if ((flags & FrameFlags.Autosave) != 0)
            {
                AutosaveFrames++;
                LongestAutosaveMs = Math.Max(LongestAutosaveMs, record.IoMs);
            }

            int slot = _recentCount % RecentFrames;
            _recentFrame[slot] = record.FrameMs; _recentWork[slot] = record.WorkMs; _recentSim[slot] = record.SimMs;
            _recentRender[slot] = record.RenderMs; _recentWait[slot] = record.WaitMs;
            _recentCount++;
            if (_frameMs.Count >= MaximumFrames) return;
            _frameMs.Add(record.FrameMs);
            _workMs.Add(record.WorkMs);
            _simMs += record.SimMs;
            _renderMs += record.RenderMs;
            _presentMs += record.PresentMs;
            _ioMs += record.IoMs;
            _waitMs += record.WaitMs;
        }

        public RunSummary Summarise()
        {
            var summary = new RunSummary { Frames = Frames, WarmupFrames = WarmupFrames, AutosaveFrames = AutosaveFrames, LongestAutosaveMs = LongestAutosaveMs, SettingsReloads = SettingsReloads };
            if (Frames == 0) return summary;

            var sortedFrames = _frameMs.ToArray();
            Array.Sort(sortedFrames);
            var sortedWork = _workMs.ToArray();
            Array.Sort(sortedWork);
            double totalMs = 0;
            foreach (var frame in sortedFrames) totalMs += frame;

            summary.Seconds = totalMs / 1000.0;
            summary.AverageFps = totalMs > 0 ? Frames * 1000.0 / totalMs : 0;
            summary.OnePercentLowFps = LowFps(sortedFrames, 0.01);
            summary.PointOnePercentLowFps = LowFps(sortedFrames, 0.001);
            summary.MedianFrameMs = Percentile(sortedFrames, 0.5);
            summary.Percentile95FrameMs = Percentile(sortedFrames, 0.95);
            summary.Percentile99FrameMs = Percentile(sortedFrames, 0.99);
            summary.MedianWorkMs = Percentile(sortedWork, 0.5);
            summary.MeanSimulationMs = _simMs / Frames;
            summary.MeanRenderMs = _renderMs / Frames;
            summary.MeanPresentMs = _presentMs / Frames;
            summary.MeanIoMs = _ioMs / Frames;
            summary.MeanWaitMs = _waitMs / Frames;
            summary.Hitches = CountHitches(summary.MedianFrameMs);
            summary.HitchesPerMinute = summary.Seconds > 0 ? summary.Hitches * 60.0 / summary.Seconds : 0;
            return summary;
        }

        int CountHitches(double medianFrameMs)
        {
            double threshold = Math.Max(medianFrameMs * HitchMedianFactor, medianFrameMs + HitchFloorMs);
            int hitches = 0;
            foreach (var frame in _frameMs) if (frame > threshold) hitches++;
            return hitches;
        }

        static double LowFps(float[] sortedFrames, double share)
        {
            int count = Math.Max(1, (int)(sortedFrames.Length * share));
            double worstTotalMs = 0;
            for (int i = sortedFrames.Length - count; i < sortedFrames.Length; i++) worstTotalMs += sortedFrames[i];
            return worstTotalMs > 0 ? count * 1000.0 / worstTotalMs : 0;
        }

        static double Percentile(float[] sorted, double quantile)
        {
            double position = quantile * (sorted.Length - 1);
            int lower = (int)Math.Floor(position);
            int upper = Math.Min(sorted.Length - 1, lower + 1);
            return sorted[lower] + (sorted[upper] - sorted[lower]) * (position - lower);
        }
    }

    public sealed class RunSummary
    {
        public int Frames;
        public int WarmupFrames;
        public int AutosaveFrames;
        public float LongestAutosaveMs;
        public int SettingsReloads;
        public double Seconds;
        public double AverageFps;
        public double OnePercentLowFps;
        public double PointOnePercentLowFps;
        public double MedianFrameMs;
        public double Percentile95FrameMs;
        public double Percentile99FrameMs;
        public double MedianWorkMs;
        public double MeanSimulationMs;
        public double MeanRenderMs;
        public double MeanPresentMs;
        public double MeanIoMs;
        public double MeanWaitMs;
        public int Hitches;
        public double HitchesPerMinute;
    }
}
