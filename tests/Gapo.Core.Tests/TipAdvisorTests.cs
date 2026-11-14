using Gapo.Core.Hardware;
using Gapo.Core.Tips;
using Xunit;

namespace Gapo.Core.Tests;

public sealed class TipAdvisorTests
{
    private const long Gigabyte = 1024L * 1024 * 1024;

    private static HardwareInventory Pc(
        GpuKind gpu = GpuKind.Discrete,
        MemoryChannelMode channels = MemoryChannelMode.Multiple,
        PowerFacts? power = null,
        StorageMedia gameMedia = StorageMedia.SolidState,
        long windowsFreeBytes = 200 * Gigabyte,
        bool? backgroundRecording = false,
        int limitMegahertz = 2900) => new()
    {
        Cpu = new CpuFacts { MaximumMegahertz = 2900, LimitMegahertz = limitMegahertz, LogicalProcessors = 4 },
        Gpus = [new GpuFacts { Name = "Graphics", Kind = gpu }],
        Memory = new MemoryFacts { ChannelMode = channels },
        Power = power ?? new PowerFacts(),
        Storage =
        [
            new StorageFacts { Drive = @"C:\", Role = "Windows", Media = StorageMedia.SolidState, TotalBytes = 500 * Gigabyte, FreeBytes = windowsFreeBytes },
            new StorageFacts { Drive = @"D:\", Role = "Game", Media = gameMedia, TotalBytes = 1000 * Gigabyte, FreeBytes = 400 * Gigabyte },
        ],
        Os = new OsFacts { BackgroundRecordingEnabled = backgroundRecording },
    };

    private static IReadOnlyList<Tip> Advise(HardwareInventory pc, double? background = 3, int? memoryLoad = 50, int hitches = 0, double longest = 0) =>
        TipAdvisor.Advise(new TipEvidence { Hardware = pc, BackgroundPercent = background, MemoryLoadPercent = memoryLoad, StorageHitches = hitches, LongestStorageHitchMs = longest });

    [Fact]
    public void AHealthyPcGetsNoTips()
    {
        Assert.Empty(Advise(Pc()));
    }

    [Fact]
    public void OnBatteryWithALimitedClockTheTipNamesTheMeasuredLimit()
    {
        var tips = Advise(Pc(power: new PowerFacts { HasBattery = true, OnMainsPower = false, BatteryPercent = 64 }, limitMegahertz: 1200));

        var tip = Assert.Single(tips);
        Assert.Equal("Plug in the charger while you play", tip.Title);
        Assert.Contains("(64% charged)", tip.Reason, StringComparison.Ordinal);
        Assert.Contains("limited to 1200 of 2900 MHz", tip.Reason, StringComparison.Ordinal);
        Assert.Equal(TipAdvisor.PowerSettings, tip.SettingsPage);
    }

    [Fact]
    public void PowerSavingModesPointToThePowerSettings()
    {
        var efficiency = Assert.Single(Advise(Pc(power: new PowerFacts { Mode = PowerMode.BestPowerEfficiency })));
        var saver = Assert.Single(Advise(Pc(power: new PowerFacts { IsSavingPower = true, Mode = PowerMode.Balanced })));

        Assert.Equal("Choose a faster power mode", efficiency.Title);
        Assert.StartsWith("The power mode is set to best power efficiency", efficiency.Reason, StringComparison.Ordinal);
        Assert.StartsWith("Battery saver is on", saver.Reason, StringComparison.Ordinal);
        Assert.Empty(Advise(Pc(power: new PowerFacts { Mode = PowerMode.Balanced })));
    }

    [Fact]
    public void SingleChannelMemoryMattersOnlyWithIntegratedGraphics()
    {
        var integrated = Assert.Single(Advise(Pc(gpu: GpuKind.Integrated, channels: MemoryChannelMode.Single)));

        Assert.Equal(TipKind.HardwareUpgrade, integrated.Kind);
        Assert.Null(integrated.SettingsPage);
        Assert.Empty(Advise(Pc(gpu: GpuKind.Discrete, channels: MemoryChannelMode.Single)));
        Assert.Empty(Advise(Pc(gpu: GpuKind.Integrated, channels: MemoryChannelMode.Soldered)));
    }

    [Fact]
    public void BackgroundAppsAndMemoryPressureNeedMeasurements()
    {
        var tips = Advise(Pc(), background: 22, memoryLoad: 91);

        Assert.Contains(tips, tip => tip.Title == "Close background apps before you play" && tip.Reason.Contains("used 22% of the processor", StringComparison.Ordinal));
        Assert.Contains(tips, tip => tip.Title == "Free up memory" && tip.Reason.StartsWith("91% of the memory", StringComparison.Ordinal));
        Assert.Empty(Advise(Pc(), background: null, memoryLoad: null));
    }

    [Fact]
    public void AHardDiskIsOnlyBlamedWhenTheGameFrozeWhileWriting()
    {
        var tip = Assert.Single(Advise(Pc(gameMedia: StorageMedia.HardDisk), hitches: 3, longest: 230));

        Assert.Contains(@"(D:\) and froze for up to 230 ms", tip.Reason, StringComparison.Ordinal);
        Assert.Empty(Advise(Pc(gameMedia: StorageMedia.HardDisk)));
        Assert.Empty(Advise(Pc(gameMedia: StorageMedia.SolidState), hitches: 3, longest: 230));
    }

    [Fact]
    public void LowFreeSpaceOnTheWindowsDriveIsReported()
    {
        var tip = Assert.Single(Advise(Pc(windowsFreeBytes: 6 * Gigabyte)));

        Assert.Equal(TipAdvisor.StorageSettings, tip.SettingsPage);
        Assert.Contains(@"Only 6.0 GB of 500 GB is free on C:\", tip.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void BackgroundRecordingAndTheBasicDisplayDriverAreReported()
    {
        var tips = Advise(Pc(gpu: GpuKind.Software, backgroundRecording: true));

        Assert.Equal(["Install the graphics driver", "Turn off background recording if you do not use it"], tips.Select(tip => tip.Title));
        Assert.Empty(Advise(Pc(backgroundRecording: null)));
    }

    [Fact]
    public void OnlyTheKnownSettingsPagesCanBeOpened()
    {
        Assert.True(TipAdvisor.IsKnownSettingsPage("ms-settings:powersleep"));
        Assert.False(TipAdvisor.IsKnownSettingsPage("https://example.com"));
        Assert.False(TipAdvisor.IsKnownSettingsPage("ms-settings:network"));
        Assert.False(TipAdvisor.IsKnownSettingsPage(null));
        Assert.All(Advise(Pc(gpu: GpuKind.Software, power: new PowerFacts { HasBattery = true, OnMainsPower = false, Mode = PowerMode.BestPowerEfficiency }, windowsFreeBytes: Gigabyte, backgroundRecording: true), background: 40),
            tip => Assert.True(tip.SettingsPage is null || TipAdvisor.IsKnownSettingsPage(tip.SettingsPage)));
    }
}
