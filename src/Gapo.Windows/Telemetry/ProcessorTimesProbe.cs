using System.Runtime.InteropServices;
using Gapo.Core.Telemetry;

namespace Gapo.Windows.Telemetry;

public sealed unsafe partial class ProcessorTimesProbe
{
    private const int SystemProcessorPerformanceInformation = 8;
    private const int Success = 0;

    private readonly NativeProcessorTimes[] buffer;

    public ProcessorTimesProbe()
        : this(Environment.ProcessorCount)
    {
    }

    public ProcessorTimesProbe(int logicalProcessorCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(logicalProcessorCount);
        buffer = new NativeProcessorTimes[logicalProcessorCount];
    }

    public int LogicalProcessorCount => buffer.Length;

    public int Read(Span<ProcessorTimes> destination)
    {
        if (destination.Length < buffer.Length)
        {
            throw new ArgumentException("The destination is smaller than the number of logical processors.", nameof(destination));
        }

        uint returnedBytes;
        int status;
        fixed (NativeProcessorTimes* native = buffer)
        {
            status = NtQuerySystemInformation(
                SystemProcessorPerformanceInformation,
                native,
                (uint)(buffer.Length * sizeof(NativeProcessorTimes)),
                out returnedBytes);
        }

        if (status != Success)
        {
            throw new InvalidOperationException($"NtQuerySystemInformation failed with status 0x{status:X8}.");
        }

        var count = (int)(returnedBytes / (uint)sizeof(NativeProcessorTimes));
        for (var processor = 0; processor < count; processor++)
        {
            destination[processor] = new ProcessorTimes(buffer[processor].IdleTime, buffer[processor].KernelTime, buffer[processor].UserTime);
        }

        return count;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeProcessorTimes
    {
        public long IdleTime;
        public long KernelTime;
        public long UserTime;
        public long DpcTime;
        public long InterruptTime;
        public uint InterruptCount;
    }

    [LibraryImport("ntdll.dll")]
    private static partial int NtQuerySystemInformation(int informationClass, void* information, uint length, out uint returnedLength);
}
