using System.Runtime.InteropServices;

namespace Gapo.Windows.Processes;

public static class BackgroundMode
{
    public static BackgroundModeResult Enter()
    {
        var process = ProcessNative.GetCurrentProcess();
        var priorityLowered = ProcessNative.SetPriorityClass(process, ProcessNative.BelowNormalPriorityClass);
        var throttling = new ProcessNative.PowerThrottlingState
        {
            Version = ProcessNative.PowerThrottlingCurrentVersion,
            ControlMask = ProcessNative.PowerThrottlingExecutionSpeed,
            StateMask = ProcessNative.PowerThrottlingExecutionSpeed,
        };
        var efficiencyModeEnabled = ProcessNative.SetProcessInformation(
            process,
            ProcessNative.ProcessPowerThrottling,
            ref throttling,
            (uint)Marshal.SizeOf<ProcessNative.PowerThrottlingState>());
        return new BackgroundModeResult(priorityLowered, efficiencyModeEnabled);
    }
}

public readonly record struct BackgroundModeResult(bool PriorityLowered, bool EfficiencyModeEnabled);
