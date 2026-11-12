using System.Runtime.InteropServices;
using System.Text;
using Gapo.Core.Hardware;
using Microsoft.Win32;

namespace Gapo.Windows.Hardware;

internal static unsafe class MemoryModuleProbe
{
    private const int RawSmbiosHeaderBytes = 8;

    internal static IReadOnlyList<MemoryModule> Read()
    {
        var size = HardwareNative.GetSystemFirmwareTable(HardwareNative.RawSmbiosProvider, 0, null, 0);
        if (size <= RawSmbiosHeaderBytes)
        {
            return [];
        }

        var table = new byte[size];
        fixed (byte* buffer = table)
        {
            var written = HardwareNative.GetSystemFirmwareTable(HardwareNative.RawSmbiosProvider, 0, buffer, size);
            if (written <= RawSmbiosHeaderBytes || written > size)
            {
                return [];
            }

            return SmbiosMemoryReader.ReadModules(table.AsSpan(RawSmbiosHeaderBytes, (int)written - RawSmbiosHeaderBytes));
        }
    }
}

internal static unsafe class DisplayProbe
{
    private const double DefaultDotsPerInch = 96.0;
    private const double PercentScale = 100.0;

    internal static DisplayFacts Read()
    {
        var mode = new HardwareNative.DisplayMode { Size = HardwareNative.DisplayMode.SizeInBytes };
        if (!HardwareNative.EnumDisplaySettings(null, HardwareNative.CurrentDisplaySettings, &mode))
        {
            throw new InvalidOperationException("The current display mode could not be read.");
        }

        return new DisplayFacts
        {
            Width = (int)mode.Width,
            Height = (int)mode.Height,
            ReportedRefreshHz = (int)mode.Frequency,
            Monitors = Math.Max(1, HardwareNative.GetSystemMetrics(HardwareNative.MonitorCountMetric)),
            ScalePercent = (int)Math.Round(HardwareNative.GetDpiForSystem() / DefaultDotsPerInch * PercentScale),
        };
    }
}

internal static unsafe class StorageProbe
{
    private const uint SeekPenaltyProperty = 7;
    private const uint DeviceProperty = 0;
    private const int DescriptorBufferBytes = 1024;
    private const int VendorOffsetField = 12;
    private const int ProductOffsetField = 16;
    private const int BusTypeField = 28;
    private const int SeekPenaltyField = 8;

    internal static StorageFacts Read(string driveRoot, string role)
    {
        if (!HardwareNative.GetDiskFreeSpaceEx(driveRoot, out _, out var totalBytes, out var freeBytes))
        {
            throw new InvalidOperationException($"Free space of {driveRoot} could not be read.");
        }

        var facts = new StorageFacts { Drive = driveRoot, Role = role, TotalBytes = (long)totalBytes, FreeBytes = (long)freeBytes };
        var volume = HardwareNative.CreateFile(@"\\.\" + driveRoot.TrimEnd('\\'), 0, HardwareNative.ShareReadWrite, null, HardwareNative.OpenExisting, 0, 0);
        if (volume == HardwareNative.InvalidHandle)
        {
            return facts;
        }

        try
        {
            var buffer = stackalloc byte[DescriptorBufferBytes];
            if (Query(volume, DeviceProperty, buffer))
            {
                facts = facts with
                {
                    Bus = BusName(*(uint*)(buffer + BusTypeField)),
                    Model = (TextAt(buffer, *(uint*)(buffer + VendorOffsetField)) + " " + TextAt(buffer, *(uint*)(buffer + ProductOffsetField))).Trim(),
                };
            }

            if (Query(volume, SeekPenaltyProperty, buffer))
            {
                facts = facts with { Media = buffer[SeekPenaltyField] != 0 ? StorageMedia.HardDisk : StorageMedia.SolidState };
            }

            return facts;
        }
        finally
        {
            HardwareNative.CloseHandle(volume);
        }
    }

    private static bool Query(nint volume, uint propertyId, byte* buffer)
    {
        new Span<byte>(buffer, DescriptorBufferBytes).Clear();
        var query = new HardwareNative.StoragePropertyQuery { PropertyId = propertyId };
        return HardwareNative.DeviceIoControl(volume, HardwareNative.StorageQueryProperty, &query, (uint)sizeof(HardwareNative.StoragePropertyQuery), buffer, DescriptorBufferBytes, out var returned, null)
            && returned > SeekPenaltyField;
    }

    private static string TextAt(byte* buffer, uint offset)
    {
        if (offset == 0 || offset >= DescriptorBufferBytes)
        {
            return string.Empty;
        }

        var text = new ReadOnlySpan<byte>(buffer + offset, DescriptorBufferBytes - (int)offset);
        var terminator = text.IndexOf((byte)0);
        return Encoding.ASCII.GetString(terminator < 0 ? text : text[..terminator]).Trim();
    }

    private static string BusName(uint busType) => busType switch
    {
        3 => "ATA",
        7 => "USB",
        8 => "RAID",
        10 => "SAS",
        11 => "SATA",
        12 => "SD",
        14 or 15 => "Virtual",
        17 => "NVMe",
        _ => string.Empty,
    };
}

internal static unsafe class PowerProbe
{
    private const byte MainsOffline = 0;
    private const byte NoSystemBattery = 128;
    private const byte BatteryStateUnknown = 255;
    private const byte BatteryPercentUnknown = 255;

    private static readonly Guid PowerSaverPlan = new("a1841308-3541-4fab-bc81-f71556f20b4a");
    private static readonly Guid BestEfficiencyOverlay = new("961cc777-2547-4f9d-8174-7d86181b8a7a");
    private static readonly Guid BetterPerformanceOverlay = new("3af9b8d9-7c97-431d-ad78-34a8bfea439f");
    private static readonly Guid BestPerformanceOverlay = new("ded574b5-45a0-4f42-8737-46345c09c238");

    internal static PowerFacts Read()
    {
        var facts = new PowerFacts();
        if (HardwareNative.GetSystemPowerStatus(out var status))
        {
            var hasBattery = status.BatteryFlag != BatteryStateUnknown && (status.BatteryFlag & NoSystemBattery) == 0;
            facts = facts with
            {
                HasBattery = hasBattery,
                OnMainsPower = status.MainsStatus != MainsOffline,
                BatteryPercent = hasBattery && status.BatteryLifePercent != BatteryPercentUnknown ? status.BatteryLifePercent : -1,
            };
        }

        facts = ReadPlan(facts);
        return ReadOverlay(facts);
    }

    private static PowerFacts ReadPlan(PowerFacts facts)
    {
        if (HardwareNative.PowerGetActiveScheme(0, out var scheme) != 0 || scheme == null)
        {
            return facts;
        }

        try
        {
            uint size = 0;
            HardwareNative.PowerReadFriendlyName(0, scheme, null, null, null, ref size);
            var name = string.Empty;
            if (size > 0)
            {
                var buffer = new byte[size];
                fixed (byte* text = buffer)
                {
                    if (HardwareNative.PowerReadFriendlyName(0, scheme, null, null, text, ref size) == 0)
                    {
                        name = Marshal.PtrToStringUni((nint)text) ?? string.Empty;
                    }
                }
            }

            return facts with { PlanName = name, IsSavingPower = *scheme == PowerSaverPlan };
        }
        finally
        {
            HardwareNative.LocalFree(scheme);
        }
    }

    private static PowerFacts ReadOverlay(PowerFacts facts)
    {
        try
        {
            if (HardwareNative.PowerGetEffectiveOverlayScheme(out var overlay) != 0)
            {
                return facts;
            }

            var mode = overlay == BestEfficiencyOverlay ? PowerMode.BestPowerEfficiency
                : overlay == BetterPerformanceOverlay ? PowerMode.BetterPerformance
                : overlay == BestPerformanceOverlay ? PowerMode.BestPerformance
                : overlay == Guid.Empty ? PowerMode.Balanced
                : PowerMode.Unknown;
            return facts with { Mode = mode, IsSavingPower = facts.IsSavingPower || mode == PowerMode.BestPowerEfficiency };
        }
        catch (EntryPointNotFoundException)
        {
            return facts;
        }
    }
}

internal static class OperatingSystemProbe
{
    private const string VersionKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";
    private const string GameBarKey = @"Software\Microsoft\GameBar";
    private const string GraphicsDriversKey = @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers";
    private const string GameRecordingKey = @"Software\Microsoft\Windows\CurrentVersion\GameDVR";
    private const int HardwareSchedulingOn = 2;

    internal static OsFacts Read()
    {
        var facts = new OsFacts();
        using (var version = Registry.LocalMachine.OpenSubKey(VersionKey))
        {
            if (version is not null)
            {
                _ = int.TryParse(version.GetValue("CurrentBuild") as string, out var build);
                facts = facts with
                {
                    Build = build,
                    Name = HardwareRules.WindowsName(version.GetValue("ProductName") as string ?? "Windows", build),
                    Version = version.GetValue("DisplayVersion") as string ?? version.GetValue("ReleaseId") as string ?? string.Empty,
                    Edition = version.GetValue("EditionID") as string ?? string.Empty,
                };
            }
        }

        return facts with
        {
            GameModeEnabled = ReadNumber(Registry.CurrentUser, GameBarKey, "AutoGameModeEnabled") is { } gameMode ? gameMode != 0 : true,
            HardwareGpuSchedulingEnabled = ReadNumber(Registry.LocalMachine, GraphicsDriversKey, "HwSchMode") is { } scheduling ? scheduling == HardwareSchedulingOn : null,
            BackgroundRecordingEnabled = ReadNumber(Registry.CurrentUser, GameRecordingKey, "HistoricalCaptureEnabled") is { } recording && recording == 1,
        };
    }

    private static int? ReadNumber(RegistryKey root, string path, string name)
    {
        using var key = root.OpenSubKey(path);
        return key?.GetValue(name) is int number ? number : null;
    }
}
