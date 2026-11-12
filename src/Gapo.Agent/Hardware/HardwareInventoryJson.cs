using System.Text.Json;
using Gapo.Core.Hardware;

namespace Gapo.Agent.Hardware;

public static class HardwareInventoryJson
{
    private const double BytesPerMegabyte = 1024.0 * 1024.0;

    public static void Write(Utf8JsonWriter json, HardwareInventory inventory)
    {
        json.WriteStartObject("hardware");
        json.WriteNumber("scanMilliseconds", Math.Round(inventory.ScanDuration.TotalMilliseconds, 1));
        json.WriteString("class", inventory.Class.ToString());
        json.WriteString("classReason", inventory.ClassReason);
        WriteProcessor(json, inventory.Cpu);
        WriteMemory(json, inventory.Memory);
        WriteGraphics(json, inventory);
        WriteDisplay(json, inventory.Display);
        WriteStorage(json, inventory.Storage);
        WritePower(json, inventory.Power);
        WriteWindows(json, inventory.Os);
        json.WriteStartArray("couldNotBeRead");
        foreach (var note in inventory.CouldNotBeRead)
        {
            json.WriteStringValue(note);
        }

        json.WriteEndArray();
        json.WriteEndObject();
    }

    private static void WriteProcessor(Utf8JsonWriter json, CpuFacts cpu)
    {
        json.WriteStartObject("processor");
        json.WriteString("name", cpu.Name);
        json.WriteString("vendor", cpu.Vendor);
        json.WriteString("architecture", cpu.Architecture);
        json.WriteNumber("cores", cpu.Cores);
        json.WriteNumber("logicalProcessors", cpu.LogicalProcessors);
        json.WriteBoolean("simultaneousMultithreading", cpu.HasSimultaneousMultithreading);
        json.WriteBoolean("hybrid", cpu.IsHybrid);
        json.WriteNumber("performanceCores", cpu.PerformanceCores);
        json.WriteNumber("efficiencyCores", cpu.EfficiencyCores);
        json.WriteNumber("baseMegahertz", cpu.BaseMegahertz);
        json.WriteNumber("maximumMegahertz", cpu.MaximumMegahertz);
        json.WriteNumber("limitMegahertz", cpu.LimitMegahertz);
        json.WriteBoolean("clockIsLimited", cpu.ClockIsLimited);
        json.WriteNumber("level2CacheKilobytes", cpu.Level2CacheKilobytes);
        json.WriteNumber("level3CacheKilobytes", cpu.Level3CacheKilobytes);
        json.WriteEndObject();
    }

    private static void WriteMemory(Utf8JsonWriter json, MemoryFacts memory)
    {
        json.WriteStartObject("memory");
        json.WriteNumber("totalMegabytes", Megabytes(memory.TotalBytes));
        json.WriteString("channelMode", memory.ChannelMode.ToString());
        json.WriteStartArray("modules");
        foreach (var module in memory.Modules)
        {
            json.WriteStartObject();
            json.WriteNumber("megabytes", Megabytes(module.CapacityBytes));
            json.WriteString("type", module.Type);
            json.WriteNumber("speed", module.SpeedMegatransfers);
            json.WriteNumber("configuredSpeed", module.ConfiguredMegatransfers);
            json.WriteString("locator", module.Locator);
            json.WriteString("manufacturer", module.Manufacturer);
            json.WriteEndObject();
        }

        json.WriteEndArray();
        json.WriteEndObject();
    }

    private static void WriteGraphics(Utf8JsonWriter json, HardwareInventory inventory)
    {
        json.WriteString("primaryGraphics", inventory.PrimaryGpu?.Name ?? string.Empty);
        json.WriteStartArray("graphics");
        foreach (var gpu in inventory.Gpus)
        {
            json.WriteStartObject();
            json.WriteString("name", gpu.Name);
            json.WriteString("vendor", gpu.Vendor);
            json.WriteString("kind", gpu.Kind.ToString());
            json.WriteNumber("dedicatedMegabytes", Megabytes(gpu.DedicatedVideoMemoryBytes));
            json.WriteNumber("sharedMegabytes", Megabytes(gpu.SharedMemoryBytes));
            json.WriteString("driverVersion", gpu.DriverVersion);
            json.WriteString("driverDate", gpu.DriverDate);
            json.WriteEndObject();
        }

        json.WriteEndArray();
    }

    private static void WriteDisplay(Utf8JsonWriter json, DisplayFacts display)
    {
        json.WriteStartObject("display");
        json.WriteNumber("width", display.Width);
        json.WriteNumber("height", display.Height);
        json.WriteNumber("reportedRefreshHz", display.ReportedRefreshHz);
        json.WriteNumber("effectiveRefreshHz", display.EffectiveRefreshHz);
        json.WriteBoolean("refreshIsKnown", display.RefreshIsKnown);
        json.WriteNumber("monitors", display.Monitors);
        json.WriteNumber("scalePercent", display.ScalePercent);
        json.WriteEndObject();
    }

    private static void WriteStorage(Utf8JsonWriter json, IReadOnlyList<StorageFacts> drives)
    {
        json.WriteStartArray("storage");
        foreach (var drive in drives)
        {
            json.WriteStartObject();
            json.WriteString("drive", drive.Drive);
            json.WriteString("role", drive.Role);
            json.WriteString("media", drive.Media.ToString());
            json.WriteString("bus", drive.Bus);
            json.WriteString("model", drive.Model);
            json.WriteNumber("totalMegabytes", Megabytes(drive.TotalBytes));
            json.WriteNumber("freeMegabytes", Megabytes(drive.FreeBytes));
            json.WriteEndObject();
        }

        json.WriteEndArray();
    }

    private static void WritePower(Utf8JsonWriter json, PowerFacts power)
    {
        json.WriteStartObject("power");
        json.WriteBoolean("hasBattery", power.HasBattery);
        json.WriteBoolean("onMainsPower", power.OnMainsPower);
        json.WriteNumber("batteryPercent", power.BatteryPercent);
        json.WriteString("plan", power.PlanName);
        json.WriteString("mode", power.Mode.ToString());
        json.WriteBoolean("savingPower", power.IsSavingPower);
        json.WriteEndObject();
    }

    private static void WriteWindows(Utf8JsonWriter json, OsFacts os)
    {
        json.WriteStartObject("windows");
        json.WriteString("name", os.Name);
        json.WriteString("version", os.Version);
        json.WriteString("edition", os.Edition);
        json.WriteNumber("build", os.Build);
        WriteOptional(json, "gameMode", os.GameModeEnabled);
        WriteOptional(json, "hardwareGpuScheduling", os.HardwareGpuSchedulingEnabled);
        WriteOptional(json, "backgroundRecording", os.BackgroundRecordingEnabled);
        json.WriteEndObject();
    }

    private static void WriteOptional(Utf8JsonWriter json, string name, bool? value)
    {
        if (value is { } known)
        {
            json.WriteBoolean(name, known);
        }
        else
        {
            json.WriteNull(name);
        }
    }

    private static long Megabytes(long bytes) => (long)Math.Round(bytes / BytesPerMegabyte);
}
