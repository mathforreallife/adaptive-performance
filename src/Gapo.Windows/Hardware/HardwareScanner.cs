using System.Diagnostics;
using System.Globalization;
using Gapo.Core.Hardware;
using Gapo.Windows.Telemetry;

namespace Gapo.Windows.Hardware;

public static class HardwareScanner
{
    private const long Megabyte = 1024 * 1024;

    public static HardwareInventory Scan(string? gameExecutablePath = null)
    {
        var clock = Stopwatch.StartNew();
        var unreadable = new List<string>();
        var cpu = Attempt("Processor", unreadable, ProcessorProbe.Read) ?? new CpuFacts { LogicalProcessors = Environment.ProcessorCount };
        var memory = ReadMemory(unreadable);
        var gpus = Attempt("Graphics", unreadable, GraphicsProbe.Read) ?? [];
        var display = Attempt("Display", unreadable, DisplayProbe.Read) ?? new DisplayFacts();
        if (!display.RefreshIsKnown)
        {
            unreadable.Add($"Display: the driver does not report a refresh rate; {DisplayFacts.AssumedRefreshHz} Hz is assumed.");
        }

        var inventory = new HardwareInventory
        {
            Cpu = cpu,
            Memory = memory,
            Gpus = gpus,
            Display = display,
            Storage = ReadStorage(gameExecutablePath, unreadable),
            Power = Attempt("Power", unreadable, PowerProbe.Read) ?? new PowerFacts(),
            Os = Attempt("Windows", unreadable, OperatingSystemProbe.Read) ?? new OsFacts(),
        };
        var (pcClass, reason) = HardwareRules.Classify(inventory.Cpu, inventory.PrimaryGpu, inventory.Memory);
        return inventory with { Class = pcClass, ClassReason = reason, CouldNotBeRead = unreadable, ScanDuration = clock.Elapsed };
    }

    private static MemoryFacts ReadMemory(List<string> unreadable)
    {
        var totalBytes = Attempt("Memory", unreadable, () => (long?)MemoryStatusProbe.Read().TotalPhysicalBytes) ?? 0;
        var modules = Attempt("Memory modules", unreadable, MemoryModuleProbe.Read) ?? [];
        var channelMode = HardwareRules.JudgeChannels(modules, totalBytes);
        if (channelMode == MemoryChannelMode.Inconsistent)
        {
            var installedMegabytes = modules.Sum(module => module.CapacityBytes) / Megabyte;
            unreadable.Add(string.Create(CultureInfo.InvariantCulture, $"Memory: the installed modules ({installedMegabytes} MB) do not add up to the memory Windows sees ({totalBytes / Megabyte} MB); channel mode is not judged."));
        }
        else if (channelMode == MemoryChannelMode.NotReported)
        {
            unreadable.Add("Memory modules: the firmware does not describe them.");
        }

        return new MemoryFacts { TotalBytes = totalBytes, Modules = modules, ChannelMode = channelMode };
    }

    private static IReadOnlyList<StorageFacts> ReadStorage(string? gameExecutablePath, List<string> unreadable)
    {
        var drives = new List<StorageFacts>();
        var systemRoot = Path.GetPathRoot(Environment.SystemDirectory);
        var gameRoot = string.IsNullOrEmpty(gameExecutablePath) ? null : Path.GetPathRoot(Path.GetFullPath(gameExecutablePath));
        if (!string.IsNullOrEmpty(systemRoot))
        {
            var sameDrive = string.Equals(systemRoot, gameRoot, StringComparison.OrdinalIgnoreCase);
            AddDrive(systemRoot, sameDrive ? "Windows and game" : "Windows");
            gameRoot = sameDrive ? null : gameRoot;
        }

        if (!string.IsNullOrEmpty(gameRoot))
        {
            AddDrive(gameRoot, "Game");
        }

        return drives;

        void AddDrive(string root, string role)
        {
            var facts = Attempt("Storage " + root, unreadable, () => StorageProbe.Read(root, role));
            if (facts is not null)
            {
                drives.Add(facts);
                if (facts.Media == StorageMedia.Unknown)
                {
                    unreadable.Add($"Storage {root}: the drive does not say whether it is a hard disk or solid-state.");
                }
            }
        }
    }

    private static T? Attempt<T>(string what, List<string> unreadable, Func<T> read)
    {
        try
        {
            return read();
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException or System.Security.SecurityException or DllNotFoundException or EntryPointNotFoundException)
        {
            unreadable.Add($"{what}: could not be read ({exception.GetType().Name}).");
            return default;
        }
    }
}
