using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;
using Gapo.Shared;

namespace Gapo.Optimiser.Hardware
{
    /// <summary>
    /// One-shot hardware inventory. Runs once at start-up, before any measurement, because WMI is slow
    /// and its work lands in WmiPrvSE.exe, a separate process that would compete with the game.
    /// Native APIs and the registry are preferred; WMI fills the gaps. Every probe is isolated so a
    /// broken WMI repository or a locked-down PC degrades the report instead of crashing it.
    /// </summary>
    public static class HardwareScanner
    {
        public static HardwareProfile Scan(string gameExePath, Action<string> progress)
        {
            var hw = new HardwareProfile();
            var sw = Stopwatch.StartNew();
            Step(hw, "CPU", progress, () => ScanCpu(hw));
            Step(hw, "Memory", progress, () => ScanMemory(hw));
            Step(hw, "Graphics", progress, () => ScanGpu(hw));
            Step(hw, "Display", progress, () => ScanDisplay(hw));
            Step(hw, "Storage", progress, () => ScanStorage(hw, gameExePath));
            Step(hw, "Power", progress, () => ScanPower(hw));
            Step(hw, "Windows", progress, () => ScanOs(hw));
            Classify(hw);
            hw.ScanMs = sw.Elapsed.TotalMilliseconds;
            return hw;
        }

        static void Step(HardwareProfile hw, string what, Action<string> progress, Action a)
        {
            if (progress != null) progress(what);
            try { a(); }
            catch (Exception ex) { hw.Notes.Add(what + ": could not be read (" + ex.GetType().Name + ")."); }
        }

        // ================================================================= WMI helper

        static List<ManagementBaseObject> Wmi(string scope, string query)
        {
            var list = new List<ManagementBaseObject>();
            var opts = new EnumerationOptions { ReturnImmediately = true, Rewindable = false, Timeout = TimeSpan.FromSeconds(6) };
            using (var s = new ManagementObjectSearcher(new ManagementScope(scope), new ObjectQuery(query), opts))
                foreach (var o in s.Get()) list.Add(o);
            return list;
        }

        static string Str(ManagementBaseObject o, string p) { try { var v = o[p]; return v == null ? "" : v.ToString().Trim(); } catch { return ""; } }
        static long Long(ManagementBaseObject o, string p) { try { var v = o[p]; return v == null ? 0 : Convert.ToInt64(v, CultureInfo.InvariantCulture); } catch { return 0; } }

        // ================================================================= CPU

        static void ScanCpu(HardwareProfile hw)
        {
            var c = hw.Cpu;
            c.Logical = Environment.ProcessorCount;
            using (var k = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0"))
            {
                if (k != null)
                {
                    c.Name = ((k.GetValue("ProcessorNameString") as string) ?? c.Name).Trim();
                    c.Vendor = ((k.GetValue("VendorIdentifier") as string) ?? "").Trim();
                    var mhz = k.GetValue("~MHz");
                    if (mhz is int) c.BaseMhz = (int)mhz;
                }
            }
            while (c.Name.Contains("  ")) c.Name = c.Name.Replace("  ", " ");
            ReadTopology(c);
            ReadClocks(c);
            try
            {
                ushort pm, nm;
                if (NativeMethods.IsWow64Process2(NativeMethods.GetCurrentProcess(), out pm, out nm))
                    c.Architecture = nm == 0xAA64 ? "ARM64" : (nm == 0x8664 ? "x64" : (nm == 0x14c ? "x86" : "x64"));
            }
            catch { c.Architecture = Environment.Is64BitOperatingSystem ? "x64" : "x86"; }
        }

        /// <summary>Cores, SMT, hybrid P/E cores (EfficiencyClass) and caches from GetLogicalProcessorInformationEx.</summary>
        static void ReadTopology(CpuInfo c)
        {
            int len = 0;
            NativeMethods.GetLogicalProcessorInformationEx(0xFFFF, IntPtr.Zero, ref len);
            if (len <= 0) return;
            IntPtr buf = Marshal.AllocHGlobal(len);
            try
            {
                if (!NativeMethods.GetLogicalProcessorInformationEx(0xFFFF, buf, ref len)) return;
                int off = 0, cores = 0, maxClass = -1, minClass = 999;
                var classes = new List<int>();
                long l2 = 0, l3 = 0;
                bool smt = false;
                while (off < len)
                {
                    IntPtr p = buf + off;
                    int rel = Marshal.ReadInt32(p, 0), size = Marshal.ReadInt32(p, 4);
                    if (size <= 0) break;
                    if (rel == 0) // RelationProcessorCore
                    {
                        cores++;
                        byte flags = Marshal.ReadByte(p, 8), eff = Marshal.ReadByte(p, 9);
                        if ((flags & 1) != 0) smt = true;
                        classes.Add(eff);
                        if (eff > maxClass) maxClass = eff;
                        if (eff < minClass) minClass = eff;
                    }
                    else if (rel == 2) // RelationCache
                    {
                        byte level = Marshal.ReadByte(p, 8);
                        int cacheSize = Marshal.ReadInt32(p, 12), type = Marshal.ReadInt32(p, 16);
                        if (level == 2 && type != 1) l2 += cacheSize;   // not an instruction-only cache
                        if (level == 3) l3 += cacheSize;
                    }
                    off += size;
                }
                c.Cores = cores > 0 ? cores : c.Logical;
                c.Smt = smt || c.Logical > c.Cores;
                c.Hybrid = maxClass > minClass;
                if (c.Hybrid)
                {
                    foreach (var e in classes) { if (e == maxClass) c.PerformanceCores++; else c.EfficiencyCores++; }
                }
                c.L2KB = (int)(l2 / 1024); c.L3KB = (int)(l3 / 1024);
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        static void ReadClocks(CpuInfo c)
        {
            int n = Math.Max(1, c.Logical);
            int size = Marshal.SizeOf(typeof(NativeMethods.PROCESSOR_POWER_INFORMATION));
            IntPtr buf = Marshal.AllocHGlobal(size * n);
            try
            {
                if (NativeMethods.CallNtPowerInformation(11, IntPtr.Zero, 0, buf, size * n) != 0) return;
                var p = (NativeMethods.PROCESSOR_POWER_INFORMATION)Marshal.PtrToStructure(buf, typeof(NativeMethods.PROCESSOR_POWER_INFORMATION));
                c.MaxMhz = (int)p.MaxMhz; c.CurrentMhz = (int)p.CurrentMhz; c.MhzLimit = (int)p.MhzLimit;
                if (c.BaseMhz == 0) c.BaseMhz = c.MaxMhz;
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        // ================================================================= memory

        static void ScanMemory(HardwareProfile hw)
        {
            var m = NativeMethods.Memory();
            var mem = hw.Memory;
            mem.TotalBytes = (long)m.ullTotalPhys;
            mem.AvailableBytes = (long)m.ullAvailPhys;
            mem.LoadPct = (int)m.dwMemoryLoad;
            mem.CommitLimitBytes = (long)m.ullTotalPageFile;
            mem.CommitTotalBytes = (long)(m.ullTotalPageFile - m.ullAvailPageFile);
            try
            {
                foreach (var o in Wmi(@"root\cimv2", "SELECT Capacity, Speed, ConfiguredClockSpeed, SMBIOSMemoryType, DeviceLocator, Manufacturer FROM Win32_PhysicalMemory"))
                {
                    var mod = new MemoryModule
                    {
                        CapacityBytes = Long(o, "Capacity"),
                        SpeedMts = (int)Long(o, "Speed"),
                        ConfiguredMts = (int)Long(o, "ConfiguredClockSpeed"),
                        Locator = Str(o, "DeviceLocator"),
                        Manufacturer = Str(o, "Manufacturer"),
                    };
                    switch ((int)Long(o, "SMBIOSMemoryType"))
                    {
                        case 24: mod.Type = "DDR3"; break;
                        case 26: mod.Type = "DDR4"; break;
                        case 29: mod.Type = "LPDDR3"; break;
                        case 30: mod.Type = "LPDDR4"; break;
                        case 34: mod.Type = "DDR5"; break;
                        case 35: mod.Type = "LPDDR5"; break;
                        default: mod.Type = ""; break;
                    }
                    if (mod.CapacityBytes > 0) mem.Modules.Add(mod);
                }
            }
            catch (Exception ex) { hw.Notes.Add("Memory modules: WMI unavailable (" + ex.GetType().Name + ")."); }

            bool lp = mem.Modules.Exists(x => x.Type.StartsWith("LP"));
            long sum = 0; foreach (var x in mem.Modules) sum += x.CapacityBytes;
            if (mem.Modules.Count > 0 && mem.TotalBytes > 0 && (sum < mem.TotalBytes * 0.8 || sum > mem.TotalBytes * 1.3))
            {
                mem.ChannelMode = "Module data inconsistent (virtual machine?)";
                hw.Notes.Add("Memory: installed modules (" + (sum >> 20) + " MB) don't add up to the RAM Windows sees (" + (mem.TotalBytes >> 20) + " MB); channel mode not judged.");
            }
            else if (mem.Modules.Count == 1 && !lp) { mem.ChannelMode = "Single-channel (1 module)"; mem.SingleChannel = true; }
            else if (mem.Modules.Count >= 2) mem.ChannelMode = "Dual-channel (" + mem.Modules.Count + " modules)";
            else if (lp) mem.ChannelMode = "Soldered LPDDR";
            else mem.ChannelMode = "Not reported";
        }

        // ================================================================= GPU

        static void ScanGpu(HardwareProfile hw)
        {
            // Registry first: it holds the true VRAM size (WMI's AdapterRAM is a 32-bit field that caps at 4 GB).
            var regVram = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using (var cls = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}"))
                    if (cls != null)
                        foreach (var sub in cls.GetSubKeyNames())
                        {
                            if (sub.Length != 4) continue;
                            try
                            {
                                using (var k = cls.OpenSubKey(sub))
                                {
                                    if (k == null) continue;
                                    string desc = k.GetValue("DriverDesc") as string;
                                    if (string.IsNullOrEmpty(desc)) continue;
                                    long v = RegSize(k.GetValue("HardwareInformation.qwMemorySize"));
                                    if (v <= 0) v = RegSize(k.GetValue("HardwareInformation.MemorySize"));
                                    if (v > 0 && (!regVram.ContainsKey(desc) || regVram[desc] < v)) regVram[desc] = v;
                                }
                            }
                            catch (System.Security.SecurityException) { }
                        }
            }
            catch (Exception) { hw.Notes.Add("Graphics: VRAM registry keys not readable; VRAM from WMI may be capped at 4 GB."); }

            foreach (var o in Wmi(@"root\cimv2", "SELECT Name, AdapterRAM, DriverVersion, DriverDate, AdapterCompatibility, PNPDeviceID FROM Win32_VideoController"))
            {
                var g = new GpuInfo { Name = Str(o, "Name"), Vendor = Str(o, "AdapterCompatibility"), DriverVersion = Str(o, "DriverVersion") };
                string date = Str(o, "DriverDate");
                if (date.Length >= 8) g.DriverDate = date.Substring(0, 4) + "-" + date.Substring(4, 2) + "-" + date.Substring(6, 2);
                long wmiRam = Long(o, "AdapterRAM");
                if (wmiRam < 0) wmiRam += 1L << 32;
                long reg;
                if (regVram.TryGetValue(g.Name, out reg)) { g.VramBytes = reg; g.VramFromRegistry = true; }
                else g.VramBytes = wmiRam;
                string n = g.Name.ToLowerInvariant();
                g.Virtual = n.Contains("basic display") || n.Contains("hyper-v") || n.Contains("vmware") || n.Contains("virtualbox") || n.Contains("remote display") || n.Contains("parsec") || n.Contains("citrix");
                g.Integrated = !g.Virtual && (
                    (n.Contains("intel") && !n.Contains("arc")) ||
                    ((n.Contains("amd") || n.Contains("radeon")) && !n.Contains(" rx ") && !n.Contains("rx ") && !n.Contains("pro w") && (n.Contains("radeon(tm) graphics") || n.Contains("radeon graphics") || n.Contains("vega"))) ||
                    n.Contains("adreno") || n.Contains("mali") ||
                    (g.VramBytes > 0 && g.VramBytes <= 512L * 1024 * 1024));
                if (!string.IsNullOrEmpty(g.Name)) hw.Gpus.Add(g);
            }
        }

        static long RegSize(object v)
        {
            if (v is long) return (long)v;
            if (v is int) return (uint)(int)v;
            var b = v as byte[];
            if (b != null && b.Length >= 8) return BitConverter.ToInt64(b, 0);
            if (b != null && b.Length >= 4) return BitConverter.ToUInt32(b, 0);
            return 0;
        }

        // ================================================================= display

        static void ScanDisplay(HardwareProfile hw)
        {
            var d = hw.Display;
            var scr = Screen.PrimaryScreen;
            d.Width = scr.Bounds.Width; d.Height = scr.Bounds.Height;
            d.Monitors = Screen.AllScreens.Length;
            var dm = new NativeMethods.DEVMODE { dmSize = (short)Marshal.SizeOf(typeof(NativeMethods.DEVMODE)) };
            if (NativeMethods.EnumDisplaySettings(null, -1, ref dm))
            {
                d.RefreshHz = dm.dmDisplayFrequency;
                if (dm.dmPelsWidth > 0) { d.Width = dm.dmPelsWidth; d.Height = dm.dmPelsHeight; }
            }
            // 0 and 1 both mean "hardware default" — seen on virtual machines and CI runners.
            if (d.RefreshHz <= 1) { d.RefreshUnknown = true; hw.Notes.Add("Display: refresh rate not reported by the driver (assuming 60 Hz)."); }
            using (var g = System.Drawing.Graphics.FromHwnd(IntPtr.Zero)) d.ScalePct = (int)Math.Round(g.DpiX / 96f * 100);
        }

        // ================================================================= storage

        static void ScanStorage(HardwareProfile hw, string gameExePath)
        {
            string sysDrive = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.System));
            string gameDrive = string.IsNullOrEmpty(gameExePath) ? null : Path.GetPathRoot(gameExePath);
            var disks = new Dictionary<string, KeyValuePair<string, string>>(StringComparer.OrdinalIgnoreCase); // letter -> (media, bus+model)
            try
            {
                var diskOfLetter = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var o in Wmi(@"root\Microsoft\Windows\Storage", "SELECT DriveLetter, DiskNumber FROM MSFT_Partition"))
                {
                    var letter = o["DriveLetter"];
                    if (letter == null) continue;
                    char ch = Convert.ToChar(letter, CultureInfo.InvariantCulture);
                    if (ch == '\0') continue;
                    diskOfLetter[ch + ":\\"] = Long(o, "DiskNumber").ToString(CultureInfo.InvariantCulture);
                }
                var info = new Dictionary<string, string[]>();
                foreach (var o in Wmi(@"root\Microsoft\Windows\Storage", "SELECT DeviceId, MediaType, BusType, FriendlyName FROM MSFT_PhysicalDisk"))
                {
                    int media = (int)Long(o, "MediaType"), bus = (int)Long(o, "BusType");
                    string m = media == 3 ? "HDD" : media == 4 ? "SSD" : media == 5 ? "SCM" : "Unknown";
                    string b = bus == 17 ? "NVMe" : bus == 11 ? "SATA" : bus == 7 ? "USB" : bus == 8 ? "RAID" : bus == 10 ? "SAS" : bus == 3 ? "ATA" : bus == 12 ? "SD" : bus == 16 ? "Virtual" : "";
                    info[Str(o, "DeviceId")] = new[] { m, b, Str(o, "FriendlyName") };
                }
                foreach (var kv in diskOfLetter)
                {
                    string[] v;
                    if (info.TryGetValue(kv.Value, out v)) disks[kv.Key] = new KeyValuePair<string, string>(v[0], (v[1] + "|" + v[2]));
                }
            }
            catch (Exception ex) { hw.Notes.Add("Storage: drive type unavailable (" + ex.GetType().Name + ")."); }

            Action<string, string> add = (root, role) =>
            {
                if (root == null) return;
                foreach (var s in hw.Storage) if (string.Equals(s.Drive, root, StringComparison.OrdinalIgnoreCase)) { s.Role += " + " + role; return; }
                var di = new DriveInfo(root);
                var st = new StorageInfo { Drive = root, Role = role, TotalBytes = di.TotalSize, FreeBytes = di.AvailableFreeSpace };
                KeyValuePair<string, string> t;
                if (disks.TryGetValue(root, out t))
                {
                    st.MediaType = t.Key;
                    var parts = t.Value.Split('|');
                    st.BusType = parts[0]; st.Model = parts.Length > 1 ? parts[1] : "";
                }
                hw.Storage.Add(st);
            };
            add(sysDrive, "Windows");
            add(gameDrive, "Game");
        }

        // ================================================================= power

        static readonly Dictionary<Guid, string> Overlays = new Dictionary<Guid, string>
        {
            { new Guid("961cc777-2547-4f9d-8174-7d86181b8a7a"), "Best power efficiency" },
            { new Guid("00000000-0000-0000-0000-000000000000"), "Balanced" },
            { new Guid("3af9b8d9-7c97-431d-ad78-34a8bfea439f"), "Better performance" },
            { new Guid("ded574b5-45a0-4f42-8737-46345c09c238"), "Best performance" },
        };

        static void ScanPower(HardwareProfile hw)
        {
            var p = hw.Power;
            var ps = SystemInformation.PowerStatus;
            p.HasBattery = (ps.BatteryChargeStatus & BatteryChargeStatus.NoSystemBattery) == 0 && ps.BatteryChargeStatus != BatteryChargeStatus.Unknown;
            p.OnAc = ps.PowerLineStatus != PowerLineStatus.Offline;
            if (p.HasBattery) p.BatteryPct = (int)Math.Round(ps.BatteryLifePercent * 100);

            IntPtr g;
            if (NativeMethods.PowerGetActiveScheme(IntPtr.Zero, out g) == 0)
            {
                try
                {
                    var guid = (Guid)Marshal.PtrToStructure(g, typeof(Guid));
                    uint size = 0;
                    NativeMethods.PowerReadFriendlyName(IntPtr.Zero, ref guid, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, ref size);
                    if (size > 0)
                    {
                        IntPtr buf = Marshal.AllocHGlobal((int)size);
                        try
                        {
                            if (NativeMethods.PowerReadFriendlyName(IntPtr.Zero, ref guid, IntPtr.Zero, IntPtr.Zero, buf, ref size) == 0)
                                p.PlanName = Marshal.PtrToStringUni(buf);
                        }
                        finally { Marshal.FreeHGlobal(buf); }
                    }
                    p.PowerSaver = guid == new Guid("a1841308-3541-4fab-bc81-f71556f20b4a");
                }
                finally { NativeMethods.LocalFree(g); }
            }
            try
            {
                Guid overlay;
                if (NativeMethods.PowerGetEffectiveOverlayScheme(out overlay) == 0)
                {
                    string name;
                    p.Overlay = Overlays.TryGetValue(overlay, out name) ? name : "";
                    if (overlay == new Guid("961cc777-2547-4f9d-8174-7d86181b8a7a")) p.PowerSaver = true;
                }
            }
            catch (EntryPointNotFoundException) { }
        }

        // ================================================================= OS

        static void ScanOs(HardwareProfile hw)
        {
            var os = hw.Os;
            os.Is64 = Environment.Is64BitOperatingSystem;
            using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
            {
                if (k != null)
                {
                    os.Name = (k.GetValue("ProductName") as string) ?? "Windows";
                    os.Version = (k.GetValue("DisplayVersion") as string) ?? (k.GetValue("ReleaseId") as string) ?? "";
                    os.Edition = (k.GetValue("EditionID") as string) ?? "";
                    int b; int.TryParse((k.GetValue("CurrentBuild") as string) ?? "0", out b); os.Build = b;
                }
            }
            // Windows 11 still reports "Windows 10" in ProductName; the build number is the reliable signal.
            if (os.Build >= 22000 && os.Name.Contains("Windows 10")) os.Name = os.Name.Replace("Windows 10", "Windows 11");

            os.GameMode = ReadBool(Registry.CurrentUser, @"Software\Microsoft\GameBar", "AutoGameModeEnabled", true);
            object hags = ReadValue(Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode");
            if (hags is int) os.Hags = (int)hags == 2;
            object hist = ReadValue(Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\GameDVR", "HistoricalCaptureEnabled");
            if (hist is int) os.BackgroundRecording = (int)hist == 1; else os.BackgroundRecording = false;
        }

        static object ReadValue(RegistryKey root, string path, string name)
        {
            try { using (var k = root.OpenSubKey(path)) return k == null ? null : k.GetValue(name); } catch { return null; }
        }

        static bool? ReadBool(RegistryKey root, string path, string name, bool defaultWhenMissing)
        {
            var v = ReadValue(root, path, name);
            if (v is int) return (int)v != 0;
            return defaultWhenMissing;
        }

        // ================================================================= class

        static void Classify(HardwareProfile hw)
        {
            var c = hw.Cpu; var g = hw.PrimaryGpu;
            double ramGb = hw.Memory.TotalBytes / 1073741824.0;
            string n = c.Name.ToLowerInvariant();
            bool budgetCpu = n.Contains("celeron") || n.Contains("pentium") || n.Contains("atom") || n.Contains("athlon") || n.Contains(" n100") || n.Contains(" n200") || n.Contains("a4-") || n.Contains("a6-") || n.Contains("a9-") || n.Contains("e2-");
            int score = 0;
            var why = new List<string>();
            if (c.Logical <= 2) { score -= 2; why.Add(c.Logical + " CPU threads"); }
            else if (c.Logical <= 4) { score -= 1; why.Add(c.Logical + " CPU threads"); }
            else if (c.Logical >= 12) score += 1;
            if (budgetCpu) { score -= 2; why.Add("entry-level CPU"); }
            if (ramGb < 6) { score -= 2; why.Add(Math.Round(ramGb) + " GB RAM"); }
            else if (ramGb < 12) score -= 0; else if (ramGb >= 16) score += 1;
            if (g.Virtual) { score -= 1; why.Add("no hardware GPU driver"); }
            else if (g.Integrated) { score -= 1; why.Add("integrated graphics"); }
            else if (g.VramBytes >= 6L << 30) score += 1;
            if (hw.Memory.SingleChannel) why.Add("single-channel RAM");
            hw.Class = score <= -2 ? PcClass.Entry : score >= 2 ? PcClass.Performance : PcClass.Mainstream;
            hw.ClassReason = why.Count > 0 ? string.Join(", ", why) : "balanced components";
        }
    }
}
