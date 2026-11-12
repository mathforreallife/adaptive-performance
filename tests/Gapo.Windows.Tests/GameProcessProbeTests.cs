using Gapo.Core.Telemetry;
using Gapo.Windows.Processes;
using Gapo.Windows.Telemetry;
using Xunit;

namespace Gapo.Windows.Tests;

public sealed class GameProcessProbeTests
{
    private const long Megabyte = 1024 * 1024;

    [Fact]
    public void ReadsCountersOfARunningProcess()
    {
        using var probe = GameProcessProbe.TryOpen(Environment.ProcessId);

        Assert.NotNull(probe);
        Assert.True(probe.IsRunning);
        Assert.True(probe.TryRead(out var reading));
        Assert.True(reading.ProcessorTime > TimeSpan.Zero);
        Assert.InRange(reading.PrivateBytes, 1 * Megabyte, 8192 * Megabyte);
        Assert.InRange(reading.WorkingSetBytes, 1 * Megabyte, 8192 * Megabyte);
        Assert.NotEqual(ProcessPriority.Unknown, reading.Priority);
    }

    [Fact]
    public void DiskWritesShowUpInTheWriteCounter()
    {
        using var probe = GameProcessProbe.TryOpen(Environment.ProcessId)!;
        var path = Path.Combine(Path.GetTempPath(), "gapo-probe-" + Guid.NewGuid().ToString("N") + ".bin");
        try
        {
            Assert.True(probe.TryRead(out var before));
            File.WriteAllBytes(path, new byte[2 * Megabyte]);
            Assert.True(probe.TryRead(out var after));

            Assert.True(after.WriteBytes - before.WriteBytes >= (ulong)(2 * Megabyte));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ReturnsNothingForAProcessThatDoesNotExist()
    {
        const int ImpossibleProcessId = 0x7FFFFFF0;

        Assert.Null(GameProcessProbe.TryOpen(ImpossibleProcessId));
    }

    [Fact]
    public void MemoryStatusIsInternallyConsistent()
    {
        var memory = MemoryStatusProbe.Read();

        Assert.InRange(memory.TotalPhysicalBytes, 512 * Megabyte, 4096L * 1024 * Megabyte);
        Assert.InRange(memory.AvailablePhysicalBytes, 1, memory.TotalPhysicalBytes);
        Assert.InRange(memory.LoadPercent, 1, 100);
        Assert.InRange(memory.CommitPercent, 1.0, 100.0);
    }
}
