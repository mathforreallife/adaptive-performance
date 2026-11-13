using System;
using System.Globalization;
using Gapo.Sandbox.Contract;

namespace Gapo.Sandbox
{
    public enum PresentPath { Dxgi, Gdi }

    public sealed class FixtureOptions
    {
        public const double DefaultWarmupSeconds = 2.8;
        public const int DefaultSaveSizeMb = 4;

        public const string SingleThreadRenderScenario = "single-thread-render";
        public const string SimulationBoundScenario = "simulation-bound";
        public const string StorageHitchScenario = "storage-hitch";
        public const string WarmupScenario = "warm-up";
        public const string MemoryPressureScenario = "memory-pressure";
        public const string GpuLoadScenario = "gpu-load";

        public SandboxPaths Paths = SandboxPaths.Default;
        public string MapName = TelemetryChannel.DefaultMapName;
        public bool PublishTelemetry = true;
        public string Scenario = SingleThreadRenderScenario;

        public bool HasFixedWorkload;
        public int WorkloadBaseWidth;
        public int WorkloadEntities;
        public float WorkloadDetail = 1f;

        public double RenderWidthMultiplier = 1.0;
        public double EntityMultiplier = 1.0;
        public int EntityOverride;
        public double WarmupSeconds = DefaultWarmupSeconds;
        public int SaveSizeMb = DefaultSaveSizeMb;
        public int MemoryBallastMb;
        public int MemoryLeaveFreeMb;
        public int GpuLoadIterations;

        public PresentPath PresentPath = PresentPath.Dxgi;
        public int WindowWidth;
        public int WindowHeight;
        public bool BelowNormalPriority;

        public double AutotestSeconds;
        public string LogPath;
        public string ScreenshotPath;
        public bool ResetSettings;
        public bool Recalibrate;
        public string UnrealSettingsPath;

        public static FixtureOptions Parse(string[] args)
        {
            var options = new FixtureOptions();
            foreach (var argument in args)
            {
                if (ValueOf(argument, "--scenario", out var scenario)) options.ApplyScenario(scenario);
            }

            foreach (var argument in args)
            {
                string value;
                if (ValueOf(argument, "--scenario", out value)) continue;
                else if (ValueOf(argument, "--data-dir", out value)) options.Paths = new SandboxPaths(value);
                else if (ValueOf(argument, "--map-name", out value)) options.MapName = value;
                else if (argument == "--no-telemetry") options.PublishTelemetry = false;
                else if (ValueOf(argument, "--workload", out value)) options.ApplyWorkload(value);
                else if (ValueOf(argument, "--entities", out value)) options.EntityOverride = PositiveInt(argument, value);
                else if (ValueOf(argument, "--warmup-seconds", out value)) options.WarmupSeconds = NonNegativeNumber(argument, value);
                else if (ValueOf(argument, "--save-size-mb", out value)) options.SaveSizeMb = PositiveInt(argument, value);
                else if (ValueOf(argument, "--memory-ballast-mb", out value)) options.MemoryBallastMb = PositiveInt(argument, value);
                else if (ValueOf(argument, "--memory-leave-free-mb", out value)) options.MemoryLeaveFreeMb = PositiveInt(argument, value);
                else if (ValueOf(argument, "--gpu-load", out value)) options.GpuLoadIterations = PositiveInt(argument, value);
                else if (ValueOf(argument, "--present", out value)) options.PresentPath = ParsePresentPath(value);
                else if (ValueOf(argument, "--window", out value)) options.ApplyWindowSize(value);
                else if (ValueOf(argument, "--priority", out value)) options.BelowNormalPriority = ParseBelowNormal(value);
                else if (ValueOf(argument, "--autotest", out value)) options.AutotestSeconds = NonNegativeNumber(argument, value);
                else if (ValueOf(argument, "--log", out value)) options.LogPath = value;
                else if (ValueOf(argument, "--screenshot", out value)) options.ScreenshotPath = value;
                else if (argument == "--reset") options.ResetSettings = true;
                else if (argument == "--recalibrate") options.Recalibrate = true;
                else if (ValueOf(argument, "--unreal-settings", out value)) options.UnrealSettingsPath = value;
                else throw new ArgumentException("Unknown argument: " + argument);
            }

            return options;
        }

        void ApplyScenario(string name)
        {
            Scenario = name;
            switch (name)
            {
                case SingleThreadRenderScenario:
                    break;
                case SimulationBoundScenario:
                    EntityMultiplier = 2.0;
                    RenderWidthMultiplier = 0.6;
                    break;
                case StorageHitchScenario:
                    SaveSizeMb = 48;
                    break;
                case WarmupScenario:
                    WarmupSeconds = 20;
                    break;
                case MemoryPressureScenario:
                    MemoryLeaveFreeMb = 600;
                    break;
                case GpuLoadScenario:
                    GpuLoadIterations = 400;
                    RenderWidthMultiplier = 0.6;
                    EntityMultiplier = 0.7;
                    break;
                default:
                    throw new ArgumentException("Unknown scenario: " + name);
            }
        }

        void ApplyWorkload(string value)
        {
            var parts = value.Split(',');
            if (parts.Length != 3) throw new ArgumentException("--workload expects <base-width>,<entities>,<detail>");
            HasFixedWorkload = true;
            WorkloadBaseWidth = PositiveInt("--workload", parts[0]);
            WorkloadEntities = PositiveInt("--workload", parts[1]);
            WorkloadDetail = (float)Math.Clamp(NonNegativeNumber("--workload", parts[2]), 0.45, 1.0);
        }

        void ApplyWindowSize(string value)
        {
            var parts = value.ToLowerInvariant().Split('x');
            if (parts.Length != 2) throw new ArgumentException("--window expects <width>x<height>");
            WindowWidth = PositiveInt("--window", parts[0]);
            WindowHeight = PositiveInt("--window", parts[1]);
        }

        static bool ValueOf(string argument, string name, out string value)
        {
            if (argument.StartsWith(name + "=", StringComparison.Ordinal))
            {
                value = argument.Substring(name.Length + 1);
                return true;
            }

            value = null;
            return false;
        }

        static int PositiveInt(string argument, string value)
        {
            if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0) return parsed;
            throw new ArgumentException("Expected a positive whole number in " + argument);
        }

        static double NonNegativeNumber(string argument, string value)
        {
            if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) && parsed >= 0) return parsed;
            throw new ArgumentException("Expected a non-negative number in " + argument);
        }

        static PresentPath ParsePresentPath(string value)
        {
            if (string.Equals(value, "dxgi", StringComparison.OrdinalIgnoreCase)) return PresentPath.Dxgi;
            if (string.Equals(value, "gdi", StringComparison.OrdinalIgnoreCase)) return PresentPath.Gdi;
            throw new ArgumentException("--present expects dxgi or gdi");
        }

        static bool ParseBelowNormal(string value)
        {
            if (string.Equals(value, "belownormal", StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(value, "normal", StringComparison.OrdinalIgnoreCase)) return false;
            throw new ArgumentException("--priority expects normal or belownormal");
        }
    }
}
