namespace Gapo.Core.Hardware;

public enum PcClass
{
    Unknown,
    Entry,
    Mainstream,
    Performance,
}

public sealed record CpuFacts
{
    public string Name { get; init; } = "Unknown CPU";

    public string Vendor { get; init; } = string.Empty;

    public string Architecture { get; init; } = "x64";

    public int Cores { get; init; }

    public int LogicalProcessors { get; init; }

    public int PerformanceCores { get; init; }

    public int EfficiencyCores { get; init; }

    public bool HasSimultaneousMultithreading { get; init; }

    public bool IsHybrid { get; init; }

    public int BaseMegahertz { get; init; }

    public int MaximumMegahertz { get; init; }

    public int LimitMegahertz { get; init; }

    public int Level2CacheKilobytes { get; init; }

    public int Level3CacheKilobytes { get; init; }

    public bool ClockIsLimited => LimitMegahertz > 0 && MaximumMegahertz > 0 && LimitMegahertz < MaximumMegahertz;
}

public enum GpuKind
{
    Discrete,
    Integrated,
    Software,
    Virtual,
}

public sealed record GpuFacts
{
    public string Name { get; init; } = "Unknown GPU";

    public string Vendor { get; init; } = string.Empty;

    public uint VendorId { get; init; }

    public uint DeviceId { get; init; }

    public long DedicatedVideoMemoryBytes { get; init; }

    public long SharedMemoryBytes { get; init; }

    public string DriverVersion { get; init; } = string.Empty;

    public string DriverDate { get; init; } = string.Empty;

    public GpuKind Kind { get; init; } = GpuKind.Discrete;

    public bool IsRealHardware => Kind is GpuKind.Discrete or GpuKind.Integrated;
}

public sealed record MemoryModule(long CapacityBytes, int SpeedMegatransfers, int ConfiguredMegatransfers, string Type, string Locator, string Manufacturer)
{
    public bool IsLowPowerSoldered => Type.StartsWith("LP", StringComparison.Ordinal);
}

public enum MemoryChannelMode
{
    NotReported,
    Inconsistent,
    Single,
    Multiple,
    Soldered,
}

public sealed record MemoryFacts
{
    public long TotalBytes { get; init; }

    public IReadOnlyList<MemoryModule> Modules { get; init; } = [];

    public MemoryChannelMode ChannelMode { get; init; } = MemoryChannelMode.NotReported;
}

public enum StorageMedia
{
    Unknown,
    HardDisk,
    SolidState,
}

public sealed record StorageFacts
{
    public string Drive { get; init; } = string.Empty;

    public string Role { get; init; } = string.Empty;

    public string Model { get; init; } = string.Empty;

    public string Bus { get; init; } = string.Empty;

    public StorageMedia Media { get; init; } = StorageMedia.Unknown;

    public long TotalBytes { get; init; }

    public long FreeBytes { get; init; }
}

public sealed record DisplayFacts
{
    public const int AssumedRefreshHz = 60;

    private const int LowestBelievableRefreshHz = 24;

    public int Width { get; init; }

    public int Height { get; init; }

    public int ReportedRefreshHz { get; init; }

    public int Monitors { get; init; } = 1;

    public int ScalePercent { get; init; } = 100;

    public bool RefreshIsKnown => ReportedRefreshHz >= LowestBelievableRefreshHz;

    public int EffectiveRefreshHz => RefreshIsKnown ? ReportedRefreshHz : AssumedRefreshHz;
}

public enum PowerMode
{
    Unknown,
    BestPowerEfficiency,
    Balanced,
    BetterPerformance,
    BestPerformance,
}

public sealed record PowerFacts
{
    public bool HasBattery { get; init; }

    public bool OnMainsPower { get; init; } = true;

    public int BatteryPercent { get; init; } = -1;

    public string PlanName { get; init; } = string.Empty;

    public PowerMode Mode { get; init; } = PowerMode.Unknown;

    public bool IsSavingPower { get; init; }
}

public sealed record OsFacts
{
    public string Name { get; init; } = "Windows";

    public string Version { get; init; } = string.Empty;

    public string Edition { get; init; } = string.Empty;

    public int Build { get; init; }

    public bool? GameModeEnabled { get; init; }

    public bool? HardwareGpuSchedulingEnabled { get; init; }

    public bool? BackgroundRecordingEnabled { get; init; }
}

public sealed record HardwareInventory
{
    public CpuFacts Cpu { get; init; } = new();

    public IReadOnlyList<GpuFacts> Gpus { get; init; } = [];

    public MemoryFacts Memory { get; init; } = new();

    public IReadOnlyList<StorageFacts> Storage { get; init; } = [];

    public DisplayFacts Display { get; init; } = new();

    public PowerFacts Power { get; init; } = new();

    public OsFacts Os { get; init; } = new();

    public PcClass Class { get; init; } = PcClass.Unknown;

    public string ClassReason { get; init; } = string.Empty;

    public IReadOnlyList<string> CouldNotBeRead { get; init; } = [];

    public TimeSpan ScanDuration { get; init; }

    public GpuFacts? PrimaryGpu => Gpus
        .OrderByDescending(gpu => gpu.Kind == GpuKind.Discrete)
        .ThenByDescending(gpu => gpu.Kind == GpuKind.Integrated)
        .ThenByDescending(gpu => gpu.DedicatedVideoMemoryBytes)
        .FirstOrDefault();
}
