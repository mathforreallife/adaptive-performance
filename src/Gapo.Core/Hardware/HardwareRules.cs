namespace Gapo.Core.Hardware;

public static class HardwareRules
{
    private const long Gigabyte = 1024L * 1024 * 1024;
    private const double LowestBelievableModuleShare = 0.8;
    private const double HighestBelievableModuleShare = 1.3;
    private const int FirstWindows11Build = 22000;
    private const double GenerousMemoryGigabytes = 24;
    private const uint MicrosoftVendorId = 0x1414;
    private const uint IntelVendorId = 0x8086;
    private const uint AmdVendorId = 0x1002;
    private const uint NvidiaVendorId = 0x10DE;
    private const long SmallestDiscreteVideoMemoryBytes = 768L * 1024 * 1024;

    private static readonly string[] VirtualAdapterMarkers =
        ["basic display", "hyper-v", "vmware", "virtualbox", "remote display", "parsec", "citrix", "virtio", "qxl"];

    private static readonly string[] SoftwareAdapterMarkers = ["basic render"];

    private static readonly string[] IntegratedAdapterMarkers =
        ["hd graphics", "uhd graphics", "iris", "radeon(tm) graphics", "radeon graphics", "vega", "adreno", "mali"];

    private static readonly string[] EntryLevelCpuMarkers =
        ["celeron", "pentium", "atom", "athlon", " n100", " n200", "a4-", "a6-", "a9-", "e2-"];

    public static MemoryChannelMode JudgeChannels(IReadOnlyList<MemoryModule> modules, long totalBytesSeenByWindows)
    {
        if (modules.Count == 0)
        {
            return MemoryChannelMode.NotReported;
        }

        var installedBytes = modules.Sum(module => module.CapacityBytes);
        if (totalBytesSeenByWindows > 0
            && (installedBytes < totalBytesSeenByWindows * LowestBelievableModuleShare || installedBytes > totalBytesSeenByWindows * HighestBelievableModuleShare))
        {
            return MemoryChannelMode.Inconsistent;
        }

        if (modules.Any(module => module.IsLowPowerSoldered))
        {
            return MemoryChannelMode.Soldered;
        }

        return modules.Count == 1 ? MemoryChannelMode.Single : MemoryChannelMode.Multiple;
    }

    public static GpuKind ClassifyGpu(string name, uint vendorId, long dedicatedVideoMemoryBytes, bool flaggedAsSoftware)
    {
        var lowered = name.ToLowerInvariant();
        if (flaggedAsSoftware || SoftwareAdapterMarkers.Any(lowered.Contains))
        {
            return GpuKind.Software;
        }

        if (VirtualAdapterMarkers.Any(lowered.Contains) || (vendorId == MicrosoftVendorId && dedicatedVideoMemoryBytes == 0))
        {
            return GpuKind.Virtual;
        }

        var looksIntegrated = IntegratedAdapterMarkers.Any(lowered.Contains) && !lowered.Contains("arc");
        var tooLittleMemoryToBeDiscrete = vendorId is IntelVendorId or AmdVendorId
            && dedicatedVideoMemoryBytes < SmallestDiscreteVideoMemoryBytes
            && !lowered.Contains(" rx ")
            && !lowered.Contains("arc");
        return looksIntegrated || tooLittleMemoryToBeDiscrete ? GpuKind.Integrated : GpuKind.Discrete;
    }

    public static string VendorName(uint vendorId) => vendorId switch
    {
        NvidiaVendorId => "NVIDIA",
        AmdVendorId => "AMD",
        IntelVendorId => "Intel",
        MicrosoftVendorId => "Microsoft",
        _ => string.Empty,
    };

    public static string WindowsName(string productName, int build) =>
        build >= FirstWindows11Build ? productName.Replace("Windows 10", "Windows 11", StringComparison.Ordinal) : productName;

    public static (PcClass Class, string Reason) Classify(CpuFacts cpu, GpuFacts? primaryGpu, MemoryFacts memory)
    {
        var score = 0;
        var reasons = new List<string>();
        if (cpu.LogicalProcessors is > 0 and <= 2)
        {
            score -= 2;
            reasons.Add($"{cpu.LogicalProcessors} CPU threads");
        }
        else if (cpu.LogicalProcessors is > 0 and <= 4)
        {
            score -= 1;
            reasons.Add($"{cpu.LogicalProcessors} CPU threads");
        }
        else if (cpu.LogicalProcessors >= 12)
        {
            score += 1;
        }

        if (EntryLevelCpuMarkers.Any(cpu.Name.ToLowerInvariant().Contains))
        {
            score -= 2;
            reasons.Add("entry-level CPU");
        }

        var memoryGigabytes = memory.TotalBytes / (double)Gigabyte;
        if (memory.TotalBytes > 0 && memoryGigabytes < 6)
        {
            score -= 2;
            reasons.Add($"{Math.Round(memoryGigabytes)} GB RAM");
        }
        else if (memoryGigabytes >= GenerousMemoryGigabytes)
        {
            score += 1;
        }

        if (primaryGpu is null || !primaryGpu.IsRealHardware)
        {
            score -= 1;
            reasons.Add("no hardware graphics driver");
        }
        else if (primaryGpu.Kind == GpuKind.Integrated)
        {
            score -= 1;
            reasons.Add("integrated graphics");
        }
        else if (primaryGpu.DedicatedVideoMemoryBytes >= 6 * Gigabyte)
        {
            score += 1;
        }

        if (memory.ChannelMode == MemoryChannelMode.Single)
        {
            reasons.Add("single-channel RAM");
        }

        var pcClass = score <= -2 ? PcClass.Entry : score >= 2 ? PcClass.Performance : PcClass.Mainstream;
        return (pcClass, reasons.Count > 0 ? string.Join(", ", reasons) : "balanced components");
    }
}
