using System.IO;
using System.Text.Json;

namespace Gapo.Sandbox
{
    public static class ResultLog
    {
        public static void Write(string path, Game game, string presentPath, string adapterName, bool softwareAdapter, string presentFallbackReason, PresentPathTimes presentTimes)
        {
            var summary = game.Stats.Summarise();
            var directory = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            using (var stream = File.Create(path))
            using (var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
            {
                json.WriteStartObject();
                json.WriteString("scenario", game.Options.Scenario);
                json.WriteNumber("frames", summary.Frames);
                json.WriteNumber("seconds", Round(summary.Seconds));
                json.WriteNumber("averageFps", Round(summary.AverageFps));
                json.WriteNumber("onePercentLowFps", Round(summary.OnePercentLowFps));
                json.WriteNumber("pointOnePercentLowFps", Round(summary.PointOnePercentLowFps));
                json.WriteNumber("medianFrameMs", Round(summary.MedianFrameMs));
                json.WriteNumber("percentile95FrameMs", Round(summary.Percentile95FrameMs));
                json.WriteNumber("percentile99FrameMs", Round(summary.Percentile99FrameMs));
                json.WriteNumber("medianWorkMs", Round(summary.MedianWorkMs));
                json.WriteNumber("meanSimulationMs", Round(summary.MeanSimulationMs));
                json.WriteNumber("meanRenderMs", Round(summary.MeanRenderMs));
                json.WriteNumber("meanPresentMs", Round(summary.MeanPresentMs));
                json.WriteNumber("meanIoMs", Round(summary.MeanIoMs));
                json.WriteNumber("meanWaitMs", Round(summary.MeanWaitMs));
                json.WriteNumber("hitches", summary.Hitches);
                json.WriteNumber("hitchesPerMinute", Round(summary.HitchesPerMinute));
                json.WriteNumber("warmupFrames", summary.WarmupFrames);
                json.WriteNumber("autosaveFrames", summary.AutosaveFrames);
                json.WriteNumber("longestAutosaveMs", Round(summary.LongestAutosaveMs));
                json.WriteNumber("settingsReloads", summary.SettingsReloads);
                json.WriteNumber("settingsRevision", game.Settings.Revision);
                json.WriteNumber("renderThreads", game.Settings.RenderThreads);
                json.WriteNumber("baseWidth", game.BaseW);
                json.WriteNumber("baseHeight", game.BaseH);
                json.WriteNumber("entities", game.Sim.Total);
                json.WriteNumber("detail", Round(game.Detail));
                json.WriteBoolean("fixedWorkload", game.Options.HasFixedWorkload);
                json.WriteNumber("calibratedUltraMs", Round(game.CalibratedMs));
                json.WriteNumber("memoryBallastMb", game.Ballast.CommittedMb);
                json.WriteNumber("gpuLoadIterations", game.GpuLoadIterations);
                json.WriteString("presentPath", presentPath);
                json.WriteString("adapter", adapterName ?? string.Empty);
                json.WriteBoolean("softwareAdapter", softwareAdapter);
                json.WriteString("presentFallbackReason", presentFallbackReason ?? string.Empty);
                json.WriteNumber("recentFrameMs", Round(game.Stats.RecentMeanFrameMs));
                json.WriteNumber("recentComposeMs", Round(presentTimes.MeanComposeMs));
                json.WriteNumber("recentUploadMs", Round(presentTimes.MeanUploadMs));
                json.WriteNumber("recentDrawMs", Round(presentTimes.MeanDrawMs));
                json.WriteNumber("recentPresentCallMs", Round(presentTimes.MeanPresentCallMs));
                json.WriteNumber("recentWorkMs", Round(game.Stats.RecentMeanWorkMs));
                json.WriteNumber("recentSimulationMs", Round(game.Stats.RecentMeanSimulationMs));
                json.WriteNumber("recentRenderMs", Round(game.Stats.RecentMeanRenderMs));
                json.WriteNumber("recentWaitMs", Round(game.Stats.RecentMeanWaitMs));
                json.WriteEndObject();
            }
        }

        static double Round(double value)
        {
            return System.Math.Round(value, 3);
        }
    }
}
