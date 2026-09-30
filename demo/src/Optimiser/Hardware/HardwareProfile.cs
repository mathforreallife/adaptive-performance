using System;
using System.Collections.Generic;

namespace Gapo.Optimiser.Hardware
{
    public enum PcClass { Unknown, Entry, Mainstream, Performance }

    public sealed class CpuInfo
    {
        public string Name = "Unknown CPU", Vendor = "";
        public int Cores, Logical, PerformanceCores, EfficiencyCores;
        public bool Smt, Hybrid;
        public int BaseMhz, MaxMhz, CurrentMhz, MhzLimit;
        public int L2KB, L3KB;
        public string Architecture = "x64";
    }

    public sealed class GpuInfo
    {
        public string Name = "Unknown GPU", Vendor = "", DriverVersion = "", DriverDate = "";
        public long VramBytes;
        public bool Integrated, Virtual, VramFromRegistry;
    }

    public sealed class MemoryModule
    {
        public long CapacityBytes;
        public int SpeedMts, ConfiguredMts;
        public string Type = "", Locator = "", Manufacturer = "";
    }

    public sealed class MemoryInfo
    {
        public long TotalBytes, AvailableBytes, CommitTotalBytes, CommitLimitBytes;
        public int LoadPct;
        public List<MemoryModule> Modules = new List<MemoryModule>();
        public string ChannelMode = "Unknown";
        public bool SingleChannel;
    }

    public sealed class StorageInfo
    {
        public string Role = "", Drive = "", Model = "", MediaType = "Unknown", BusType = "";
        public long TotalBytes, FreeBytes;
        public double FreePct { get { return TotalBytes > 0 ? 100.0 * FreeBytes / TotalBytes : 0; } }
    }

    public sealed class DisplayInfo
    {
        public int Width, Height, RefreshHz, Monitors, ScalePct = 100;
        public bool RefreshUnknown;
    }

    public sealed class PowerInfo
    {
        public bool HasBattery, OnAc = true;
        public int BatteryPct = -1;
        public string PlanName = "Unknown", Overlay = "";
        public bool PowerSaver;
    }

    public sealed class OsInfo
    {
        public string Name = "Windows", Version = "", Edition = "";
        public int Build;
        public bool Is64 = true;
        public bool? GameMode, Hags, BackgroundRecording;
    }

    public sealed class HardwareProfile
    {
        public CpuInfo Cpu = new CpuInfo();
        public List<GpuInfo> Gpus = new List<GpuInfo>();
        public MemoryInfo Memory = new MemoryInfo();
        public List<StorageInfo> Storage = new List<StorageInfo>();
        public DisplayInfo Display = new DisplayInfo();
        public PowerInfo Power = new PowerInfo();
        public OsInfo Os = new OsInfo();
        public PcClass Class = PcClass.Unknown;
        public string ClassReason = "";
        public double ScanMs;
        public List<string> Notes = new List<string>();   // what could not be read, and why

        public GpuInfo PrimaryGpu
        {
            get
            {
                GpuInfo best = null;
                foreach (var g in Gpus)
                    if (best == null || (!g.Virtual && best.Virtual) || (!g.Integrated && best.Integrated && !g.Virtual) || (g.Integrated == best.Integrated && g.VramBytes > best.VramBytes))
                        best = g;
                return best ?? new GpuInfo();
            }
        }

        public int EffectiveRefreshHz { get { return Display.RefreshUnknown || Display.RefreshHz < 24 ? 60 : Display.RefreshHz; } }
    }
}
