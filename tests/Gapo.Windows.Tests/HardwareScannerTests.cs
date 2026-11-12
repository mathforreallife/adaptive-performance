using Gapo.Core.Hardware;
using Gapo.Windows.Hardware;
using Xunit;

namespace Gapo.Windows.Tests;

public sealed class HardwareScannerTests
{
    private const long Megabyte = 1024 * 1024;
    private static readonly HardwareInventory Inventory = HardwareScanner.Scan(Environment.ProcessPath);

    [Fact]
    public void ProcessorFactsAreConsistentWithWhatTheRuntimeSees()
    {
        var cpu = Inventory.Cpu;

        Assert.Equal(Environment.ProcessorCount, cpu.LogicalProcessors);
        Assert.InRange(cpu.Cores, 1, cpu.LogicalProcessors);
        Assert.Equal(cpu.LogicalProcessors > cpu.Cores, cpu.HasSimultaneousMultithreading);
        Assert.False(string.IsNullOrWhiteSpace(cpu.Name));
        Assert.DoesNotContain("  ", cpu.Name);
        Assert.InRange(cpu.BaseMegahertz, 500, 10_000);
        Assert.Equal(cpu.PerformanceCores + cpu.EfficiencyCores, cpu.IsHybrid ? cpu.Cores : 0);
    }

    [Fact]
    public void MemoryTotalIsPlausibleAndModulesNeverContradictItSilently()
    {
        var memory = Inventory.Memory;

        Assert.InRange(memory.TotalBytes, 512 * Megabyte, 4096L * 1024 * Megabyte);
        if (memory.ChannelMode is MemoryChannelMode.Single or MemoryChannelMode.Multiple or MemoryChannelMode.Soldered)
        {
            var installed = memory.Modules.Sum(module => module.CapacityBytes);
            Assert.InRange(installed, memory.TotalBytes * 0.8, memory.TotalBytes * 1.3);
        }
        else
        {
            Assert.Contains(Inventory.CouldNotBeRead, note => note.StartsWith("Memory", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void AtLeastOneGraphicsAdapterIsListedAndClassified()
    {
        Assert.NotEmpty(Inventory.Gpus);
        Assert.NotNull(Inventory.PrimaryGpu);
        Assert.All(Inventory.Gpus, gpu => Assert.False(string.IsNullOrWhiteSpace(gpu.Name)));
        Assert.All(Inventory.Gpus.Where(gpu => gpu.Name.Contains("Basic Render", StringComparison.Ordinal)), gpu => Assert.Equal(GpuKind.Software, gpu.Kind));
    }

    [Fact]
    public void DisplayModeIsReadAndAnUnknownRefreshRateIsExplained()
    {
        var display = Inventory.Display;

        Assert.InRange(display.Width, 320, 16_384);
        Assert.InRange(display.Height, 200, 16_384);
        Assert.InRange(display.ScalePercent, 100, 500);
        Assert.InRange(display.EffectiveRefreshHz, 24, 1000);
        if (!display.RefreshIsKnown)
        {
            Assert.Contains(Inventory.CouldNotBeRead, note => note.StartsWith("Display", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void SystemDriveIsDescribed()
    {
        var systemDrive = Assert.Single(Inventory.Storage, drive => drive.Role.StartsWith("Windows", StringComparison.Ordinal));

        Assert.True(systemDrive.TotalBytes > 0);
        Assert.InRange(systemDrive.FreeBytes, 0, systemDrive.TotalBytes);
    }

    [Fact]
    public void WindowsVersionAndClassAreFilledIn()
    {
        Assert.InRange(Inventory.Os.Build, 10_240, 99_999);
        Assert.StartsWith("Windows", Inventory.Os.Name);
        Assert.Equal(Inventory.Os.Build >= 22_000, Inventory.Os.Name.Contains("11", StringComparison.Ordinal) || Inventory.Os.Name.Contains("Server", StringComparison.Ordinal));
        Assert.NotEqual(PcClass.Unknown, Inventory.Class);
        Assert.False(string.IsNullOrWhiteSpace(Inventory.ClassReason));
    }

    [Fact]
    public void ScanIsFastEnoughToRunBeforeAnyMeasurement()
    {
        var second = HardwareScanner.Scan();

        Assert.True(second.ScanDuration < TimeSpan.FromSeconds(2), $"The scan took {second.ScanDuration.TotalMilliseconds:0} ms.");
    }
}
