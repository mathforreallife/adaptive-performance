using System.Text;
using Gapo.Core.Hardware;
using Xunit;

namespace Gapo.Core.Tests;

public sealed class HardwareRulesTests
{
    private const long Megabyte = 1024L * 1024;
    private const long Gigabyte = 1024 * Megabyte;
    private const uint Intel = 0x8086;
    private const uint Amd = 0x1002;
    private const uint Nvidia = 0x10DE;
    private const uint Microsoft = 0x1414;

    private static MemoryModule Module(long gigabytes, string type = "DDR4") => new(gigabytes * Gigabyte, 3200, 3200, type, "DIMM", "Maker");

    [Fact]
    public void OneModuleIsSingleChannel()
    {
        Assert.Equal(MemoryChannelMode.Single, HardwareRules.JudgeChannels([Module(8)], 8 * Gigabyte));
    }

    [Fact]
    public void TwoModulesAreNotFlaggedAsSingleChannel()
    {
        Assert.Equal(MemoryChannelMode.Multiple, HardwareRules.JudgeChannels([Module(8), Module(8)], 16 * Gigabyte - (200 * Megabyte)));
    }

    [Fact]
    public void SolderedLowPowerMemoryIsNeverFlaggedAsSingleChannel()
    {
        Assert.Equal(MemoryChannelMode.Soldered, HardwareRules.JudgeChannels([Module(16, "LPDDR5")], 16 * Gigabyte));
    }

    [Fact]
    public void ModulesThatDoNotAddUpToInstalledMemoryAreNotJudged()
    {
        Assert.Equal(MemoryChannelMode.Inconsistent, HardwareRules.JudgeChannels([Module(1), Module(1)], 8 * Gigabyte));
    }

    [Fact]
    public void NoModuleDataMeansNotReported()
    {
        Assert.Equal(MemoryChannelMode.NotReported, HardwareRules.JudgeChannels([], 8 * Gigabyte));
    }

    [Theory]
    [InlineData("Microsoft Basic Render Driver", Microsoft, 0L, false, GpuKind.Software)]
    [InlineData("Microsoft Hyper-V Video", Microsoft, 0L, false, GpuKind.Virtual)]
    [InlineData("Microsoft Basic Display Adapter", Microsoft, 0L, false, GpuKind.Virtual)]
    [InlineData("Intel(R) HD Graphics 4000", Intel, 32L, false, GpuKind.Integrated)]
    [InlineData("Intel(R) UHD Graphics 770", Intel, 128L, false, GpuKind.Integrated)]
    [InlineData("Intel(R) Arc(TM) A750 Graphics", Intel, 8192L, false, GpuKind.Discrete)]
    [InlineData("AMD Radeon(TM) Graphics", Amd, 512L, false, GpuKind.Integrated)]
    [InlineData("AMD Radeon RX 6600", Amd, 8192L, false, GpuKind.Discrete)]
    [InlineData("NVIDIA GeForce RTX 3060", Nvidia, 12288L, false, GpuKind.Discrete)]
    [InlineData("Some Adapter", 0x9999u, 4096L, true, GpuKind.Software)]
    public void ClassifiesGraphicsAdapters(string name, uint vendorId, long dedicatedMegabytes, bool softwareFlag, GpuKind expected)
    {
        Assert.Equal(expected, HardwareRules.ClassifyGpu(name, vendorId, dedicatedMegabytes * Megabyte, softwareFlag));
    }

    [Fact]
    public void Windows11IsRecognisedByItsBuildNumber()
    {
        Assert.Equal("Windows 11 Pro", HardwareRules.WindowsName("Windows 10 Pro", build: 22631));
        Assert.Equal("Windows 10 Pro", HardwareRules.WindowsName("Windows 10 Pro", build: 19045));
    }

    [Fact]
    public void OldDualCoreLaptopWithIntegratedGraphicsIsEntryLevel()
    {
        var cpu = new CpuFacts { Name = "Intel(R) Core(TM) i7-3520M CPU @ 2.90GHz", LogicalProcessors = 4, Cores = 2 };
        var gpu = new GpuFacts { Name = "Intel(R) HD Graphics 4000", Kind = GpuKind.Integrated };
        var memory = new MemoryFacts { TotalBytes = 16 * Gigabyte, ChannelMode = MemoryChannelMode.Multiple };

        var (pcClass, reason) = HardwareRules.Classify(cpu, gpu, memory);

        Assert.Equal(PcClass.Entry, pcClass);
        Assert.Contains("integrated graphics", reason);
        Assert.Contains("4 CPU threads", reason);
    }

    [Fact]
    public void BudgetProcessorWithLittleMemoryIsEntryLevel()
    {
        var cpu = new CpuFacts { Name = "Intel(R) Celeron(R) N4020", LogicalProcessors = 2, Cores = 2 };
        var gpu = new GpuFacts { Kind = GpuKind.Integrated };
        var memory = new MemoryFacts { TotalBytes = 4 * Gigabyte, ChannelMode = MemoryChannelMode.Single };

        var (pcClass, reason) = HardwareRules.Classify(cpu, gpu, memory);

        Assert.Equal(PcClass.Entry, pcClass);
        Assert.Contains("single-channel RAM", reason);
    }

    [Fact]
    public void ManyThreadsPlentyOfMemoryAndALargeGraphicsCardIsPerformanceClass()
    {
        var cpu = new CpuFacts { Name = "AMD Ryzen 7 7800X3D", LogicalProcessors = 16, Cores = 8 };
        var gpu = new GpuFacts { Kind = GpuKind.Discrete, DedicatedVideoMemoryBytes = 12 * Gigabyte };
        var memory = new MemoryFacts { TotalBytes = 32 * Gigabyte };

        Assert.Equal(PcClass.Performance, HardwareRules.Classify(cpu, gpu, memory).Class);
    }

    [Fact]
    public void MachineWithoutARealGraphicsDriverSaysSo()
    {
        var cpu = new CpuFacts { Name = "AMD EPYC 7763", LogicalProcessors = 4, Cores = 2 };
        var memory = new MemoryFacts { TotalBytes = 16 * Gigabyte };

        var (_, reason) = HardwareRules.Classify(cpu, new GpuFacts { Kind = GpuKind.Virtual }, memory);

        Assert.Contains("no hardware graphics driver", reason);
    }

    [Fact]
    public void UnreportedRefreshRateFallsBackToSixtyHertz()
    {
        Assert.Equal(60, new DisplayFacts { ReportedRefreshHz = 1 }.EffectiveRefreshHz);
        Assert.False(new DisplayFacts { ReportedRefreshHz = 0 }.RefreshIsKnown);
        Assert.Equal(144, new DisplayFacts { ReportedRefreshHz = 144 }.EffectiveRefreshHz);
    }

    [Fact]
    public void PrimaryGraphicsAdapterPrefersRealHardwareOverVirtualOnes()
    {
        var inventory = new HardwareInventory
        {
            Gpus =
            [
                new GpuFacts { Name = "Basic Render", Kind = GpuKind.Software },
                new GpuFacts { Name = "Integrated", Kind = GpuKind.Integrated },
                new GpuFacts { Name = "Discrete", Kind = GpuKind.Discrete, DedicatedVideoMemoryBytes = 8 * Gigabyte },
            ],
        };

        Assert.Equal("Discrete", inventory.PrimaryGpu!.Name);
    }

    [Fact]
    public void ClockLimitBelowMaximumIsReportedAsLimited()
    {
        Assert.True(new CpuFacts { MaximumMegahertz = 2900, LimitMegahertz = 2000 }.ClockIsLimited);
        Assert.False(new CpuFacts { MaximumMegahertz = 2900, LimitMegahertz = 2900 }.ClockIsLimited);
    }

    [Fact]
    public void ReadsMemoryModulesFromFirmwareTables()
    {
        var table = new List<byte>();
        table.AddRange(Structure(type: 0, formatted: new byte[20], "Vendor", "Version"));
        table.AddRange(MemoryDevice(sizeWord: 8192, memoryType: 0x18, speed: 1600, configuredSpeed: 1600, "DIMM A", "Samsung"));
        table.AddRange(MemoryDevice(sizeWord: 0, memoryType: 0x18, speed: 0, configuredSpeed: 0, "DIMM B", "Empty"));
        table.AddRange(MemoryDevice(sizeWord: 0x7FFF, memoryType: 0x22, speed: 5600, configuredSpeed: 4800, "DIMM C", "Kingston", extendedSizeMegabytes: 32768));
        table.AddRange(Structure(type: 127, formatted: new byte[0]));

        var modules = SmbiosMemoryReader.ReadModules(table.ToArray());

        Assert.Equal(2, modules.Count);
        Assert.Equal(new MemoryModule(8 * Gigabyte, 1600, 1600, "DDR3", "DIMM A", "Samsung"), modules[0]);
        Assert.Equal(new MemoryModule(32 * Gigabyte, 5600, 4800, "DDR5", "DIMM C", "Kingston"), modules[1]);
    }

    [Fact]
    public void TruncatedFirmwareTablesAreReadAsFarAsTheyGo()
    {
        var table = MemoryDevice(sizeWord: 4096, memoryType: 0x1A, speed: 2666, configuredSpeed: 2666, "DIMM A", "Maker").ToList();
        table.AddRange([17, 40, 0]);

        Assert.Single(SmbiosMemoryReader.ReadModules(table.ToArray()));
        Assert.Empty(SmbiosMemoryReader.ReadModules([]));
    }

    private static byte[] MemoryDevice(ushort sizeWord, byte memoryType, ushort speed, ushort configuredSpeed, string locator, string manufacturer, uint extendedSizeMegabytes = 0)
    {
        var formatted = new byte[0x22 - 4];
        void Word(int offset, ushort value)
        {
            formatted[offset - 4] = (byte)(value & 0xFF);
            formatted[offset - 3] = (byte)(value >> 8);
        }

        Word(0x0C, sizeWord);
        formatted[0x10 - 4] = 1;
        formatted[0x12 - 4] = memoryType;
        Word(0x15, speed);
        formatted[0x17 - 4] = 2;
        BitConverter.GetBytes(extendedSizeMegabytes).CopyTo(formatted, 0x1C - 4);
        Word(0x20, configuredSpeed);
        return Structure(17, formatted, locator, manufacturer);
    }

    private static byte[] Structure(byte type, byte[] formatted, params string[] strings)
    {
        var bytes = new List<byte> { type, (byte)(formatted.Length + 4), 0, 0 };
        bytes.AddRange(formatted);
        foreach (var text in strings)
        {
            bytes.AddRange(Encoding.ASCII.GetBytes(text));
            bytes.Add(0);
        }

        bytes.Add(0);
        if (strings.Length == 0)
        {
            bytes.Add(0);
        }

        return bytes.ToArray();
    }
}
