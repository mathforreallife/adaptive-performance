using System.Runtime.InteropServices;
using Gapo.Core.Overhead;

namespace Gapo.Windows.Processes;

public sealed class OwnProcessProbe
{
    private readonly nint process = ProcessNative.GetCurrentProcess();
    private readonly double? cyclesPerSecond = ProcessorCycleRate.ReadNominalCyclesPerSecond();

    public bool UsesCycleCounting => cyclesPerSecond is not null;

    public ProcessUsage Read() => new(ReadProcessorTime(), ReadPrivateBytes());

    private TimeSpan ReadProcessorTime()
    {
        if (cyclesPerSecond is { } rate && ProcessNative.QueryProcessCycleTime(process, out var cycles))
        {
            return TimeSpan.FromSeconds(cycles / rate);
        }

        if (!ProcessNative.GetProcessTimes(process, out _, out _, out var kernelTime, out var userTime))
        {
            throw new InvalidOperationException($"GetProcessTimes failed with error {Marshal.GetLastPInvokeError()}.");
        }

        return TimeSpan.FromTicks(kernelTime + userTime);
    }

    private long ReadPrivateBytes()
    {
        var counters = new ProcessNative.ProcessMemoryCountersEx { Size = (uint)Marshal.SizeOf<ProcessNative.ProcessMemoryCountersEx>() };
        if (!ProcessNative.GetProcessMemoryInfo(process, ref counters, counters.Size))
        {
            throw new InvalidOperationException($"GetProcessMemoryInfo failed with error {Marshal.GetLastPInvokeError()}.");
        }

        return (long)counters.PrivateUsage;
    }
}
