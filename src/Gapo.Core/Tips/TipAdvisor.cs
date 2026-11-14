using System.Globalization;
using Gapo.Core.Hardware;

namespace Gapo.Core.Tips;

public enum TipKind
{
    WindowsSetting,
    Habit,
    HardwareUpgrade,
}

public sealed record Tip(string Title, string Reason, TipKind Kind, string? SettingsPage = null, string? ButtonText = null);

public sealed record TipEvidence
{
    public required HardwareInventory Hardware { get; init; }

    public double? BackgroundPercent { get; init; }

    public int? MemoryLoadPercent { get; init; }

    public int StorageHitches { get; init; }

    public double LongestStorageHitchMs { get; init; }
}

public static class TipAdvisor
{
    public const string PowerSettings = "ms-settings:powersleep";
    public const string StartupApps = "ms-settings:startupapps";
    public const string StorageSettings = "ms-settings:storagesense";
    public const string CaptureSettings = "ms-settings:gaming-gamedvr";
    public const string WindowsUpdate = "ms-settings:windowsupdate";

    private const double BackgroundThatCompetesPercent = 15;
    private const int MemoryPressurePercent = 85;
    private const double ClockLimitShare = 0.8;
    private const double LowFreeShare = 0.1;
    private const long LowFreeBytes = 10L * 1024 * 1024 * 1024;
    private const double BytesPerGigabyte = 1024.0 * 1024 * 1024;

    private static readonly IReadOnlySet<string> SettingsPages = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        PowerSettings,
        StartupApps,
        StorageSettings,
        CaptureSettings,
        WindowsUpdate,
    };

    public static bool IsKnownSettingsPage(string? page) => page is not null && SettingsPages.Contains(page);

    public static IReadOnlyList<Tip> Advise(TipEvidence evidence)
    {
        var hardware = evidence.Hardware;
        var tips = new List<Tip>();
        var power = hardware.Power;
        var cpu = hardware.Cpu;
        var clockLimited = cpu.MaximumMegahertz > 0 && cpu.LimitMegahertz > 0 && cpu.LimitMegahertz < cpu.MaximumMegahertz * ClockLimitShare;
        var clockNote = clockLimited ? Text($" Windows reports the processor limited to {cpu.LimitMegahertz} of {cpu.MaximumMegahertz} MHz.") : string.Empty;

        if (hardware.PrimaryGpu is { Kind: GpuKind.Software })
        {
            tips.Add(new Tip(
                "Install the graphics driver",
                "Windows draws this PC's picture with its basic display driver, so games run on the processor instead of the graphics chip. Windows Update or the PC maker's website has the right driver.",
                TipKind.WindowsSetting,
                WindowsUpdate,
                "Open Windows Update"));
        }

        if (power.HasBattery && !power.OnMainsPower)
        {
            var charge = power.BatteryPercent >= 0 ? Text($" ({power.BatteryPercent}% charged)") : string.Empty;
            tips.Add(new Tip(
                "Plug in the charger while you play",
                Text($"This laptop runs on battery{charge}. Windows and the laptop usually slow the processor and graphics down on battery to make it last.") + clockNote,
                TipKind.Habit,
                PowerSettings,
                "Open power settings"));
        }

        if (power.Mode == PowerMode.BestPowerEfficiency || power.IsSavingPower)
        {
            var current = power.IsSavingPower ? "Battery saver is on" : "The power mode is set to best power efficiency";
            tips.Add(new Tip(
                "Choose a faster power mode",
                Text($"{current}, which holds the processor back.") + clockNote + " GAPO does not change Windows settings by itself.",
                TipKind.WindowsSetting,
                PowerSettings,
                "Open power settings"));
        }

        if (hardware.Memory.ChannelMode == MemoryChannelMode.Single && hardware.PrimaryGpu is { Kind: GpuKind.Integrated })
        {
            tips.Add(new Tip(
                "Add a second memory module",
                "The memory runs in single-channel mode and the graphics chip shares it with the processor, so graphics get half the memory speed they could have. A second matching module is a hardware upgrade; GAPO cannot change this.",
                TipKind.HardwareUpgrade));
        }

        if (evidence.BackgroundPercent is { } background && background >= BackgroundThatCompetesPercent)
        {
            tips.Add(new Tip(
                "Close background apps before you play",
                Text($"Other programs used {background:0}% of the processor while the game ran. GAPO never closes programs; closing them yourself, or stopping them from starting with Windows, leaves more for the game."),
                TipKind.Habit,
                StartupApps,
                "Open startup apps"));
        }

        if (evidence.MemoryLoadPercent is { } memoryLoad && memoryLoad >= MemoryPressurePercent)
        {
            tips.Add(new Tip(
                "Free up memory",
                Text($"{memoryLoad}% of the memory was in use while the game ran. When memory runs out, Windows moves data to the disk and the game stutters. Closing browsers and other large programs helps."),
                TipKind.Habit));
        }

        var gameDrive = hardware.Storage.FirstOrDefault(drive => drive.Role.Contains("game", StringComparison.OrdinalIgnoreCase));
        if (gameDrive is { Media: StorageMedia.HardDisk } && evidence.StorageHitches > 0)
        {
            tips.Add(new Tip(
                "Move the game to a solid-state drive",
                Text($"The game is on a hard disk ({gameDrive.Drive}) and froze for up to {evidence.LongestStorageHitchMs:0} ms while it wrote to the disk, {evidence.StorageHitches} time(s). A solid-state drive writes many times faster. Steam can move a game: Properties → Installed files → Move install folder."),
                TipKind.HardwareUpgrade));
        }

        var windowsDrive = hardware.Storage.FirstOrDefault(drive => drive.Role.StartsWith("Windows", StringComparison.OrdinalIgnoreCase));
        if (windowsDrive is { TotalBytes: > 0 } && (windowsDrive.FreeBytes < windowsDrive.TotalBytes * LowFreeShare || windowsDrive.FreeBytes < LowFreeBytes))
        {
            tips.Add(new Tip(
                "Free up space on the Windows drive",
                Text($"Only {windowsDrive.FreeBytes / BytesPerGigabyte:0.0} GB of {windowsDrive.TotalBytes / BytesPerGigabyte:0} GB is free on {windowsDrive.Drive}. Windows needs free space for its page file and for game updates."),
                TipKind.Habit,
                StorageSettings,
                "Open storage settings"));
        }

        if (hardware.Os.BackgroundRecordingEnabled == true)
        {
            tips.Add(new Tip(
                "Turn off background recording if you do not use it",
                "Xbox Game Bar keeps recording the last minutes of play. Windows itself warns that this can affect game performance.",
                TipKind.WindowsSetting,
                CaptureSettings,
                "Open capture settings"));
        }

        return tips;
    }

    private static string Text(FormattableString value) => value.ToString(CultureInfo.InvariantCulture);
}
