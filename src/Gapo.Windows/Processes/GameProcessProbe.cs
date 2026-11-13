using System.Runtime.InteropServices;
using Gapo.Core.Telemetry;

namespace Gapo.Windows.Processes;

public sealed class GameProcessProbe : IDisposable
{
    private const uint IdlePriorityClass = 0x00000040;
    private const uint NormalPriorityClass = 0x00000020;
    private const uint AboveNormalPriorityClass = 0x00008000;
    private const uint HighPriorityClass = 0x00000080;
    private const uint RealtimePriorityClass = 0x00000100;

    private readonly double? cyclesPerSecond = ProcessorCycleRate.ReadNominalCyclesPerSecond();
    private nint handle;

    private GameProcessProbe(int processId, nint handle)
    {
        ProcessId = processId;
        this.handle = handle;
    }

    public int ProcessId { get; }

    public static GameProcessProbe? TryOpen(int processId)
    {
        var handle = ProcessNative.OpenProcess(ProcessNative.QueryLimitedInformation | ProcessNative.Synchronize, inheritHandle: false, processId);
        return handle == 0 ? null : new GameProcessProbe(processId, handle);
    }

    public bool IsRunning => handle != 0 && ProcessNative.WaitForSingleObject(handle, 0) == ProcessNative.WaitTimeout;

    public bool TryRead(out GameProcessReading reading)
    {
        reading = default;
        if (handle == 0 || !TryReadProcessorTime(out var processorTime))
        {
            return false;
        }

        var memory = new ProcessNative.ProcessMemoryCountersEx { Size = (uint)Marshal.SizeOf<ProcessNative.ProcessMemoryCountersEx>() };
        if (!ProcessNative.GetProcessMemoryInfo(handle, ref memory, memory.Size) || !ProcessNative.GetProcessIoCounters(handle, out var io))
        {
            return false;
        }

        reading = new GameProcessReading(
            processorTime,
            (long)memory.PrivateUsage,
            (long)memory.WorkingSetSize,
            io.ReadTransferCount,
            io.WriteTransferCount,
            ReadPriority());
        return true;
    }

    public void Dispose()
    {
        if (handle != 0)
        {
            ProcessNative.CloseHandle(handle);
            handle = 0;
        }
    }

    private bool TryReadProcessorTime(out TimeSpan processorTime)
    {
        if (cyclesPerSecond is { } rate && ProcessNative.QueryProcessCycleTime(handle, out var cycles))
        {
            processorTime = TimeSpan.FromSeconds(cycles / rate);
            return true;
        }

        if (ProcessNative.GetProcessTimes(handle, out _, out _, out var kernelTime, out var userTime))
        {
            processorTime = TimeSpan.FromTicks(kernelTime + userTime);
            return true;
        }

        processorTime = default;
        return false;
    }

    private ProcessPriority ReadPriority() => ProcessNative.GetPriorityClass(handle) switch
    {
        IdlePriorityClass => ProcessPriority.Idle,
        ProcessNative.BelowNormalPriorityClass => ProcessPriority.BelowNormal,
        NormalPriorityClass => ProcessPriority.Normal,
        AboveNormalPriorityClass => ProcessPriority.AboveNormal,
        HighPriorityClass => ProcessPriority.High,
        RealtimePriorityClass => ProcessPriority.Realtime,
        _ => ProcessPriority.Unknown,
    };
}
