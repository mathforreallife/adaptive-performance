using System;
using System.Runtime.InteropServices;

namespace Gapo.Optimiser.Hardware
{
    /// <summary>Optimiser-only Win32 / NT entry points. Everything here works without admin rights or drivers.</summary>
    static class NativeMethods
    {
        // ---------------------------------------------------------------- memory
        [StructLayout(LayoutKind.Sequential)]
        public struct MEMORYSTATUSEX
        {
            public uint dwLength, dwMemoryLoad;
            public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
        }
        [DllImport("kernel32.dll", SetLastError = true)] public static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);

        public static MEMORYSTATUSEX Memory()
        {
            var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX)) };
            GlobalMemoryStatusEx(ref m);
            return m;
        }

        // ---------------------------------------------------------------- CPU topology
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GetLogicalProcessorInformationEx(int relationship, IntPtr buffer, ref int length);

        // ---------------------------------------------------------------- per-core load
        [StructLayout(LayoutKind.Sequential)]
        public struct SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION
        {
            public long IdleTime, KernelTime, UserTime, DpcTime, InterruptTime;
            public uint InterruptCount;
            public uint Pad;
        }
        [DllImport("ntdll.dll")] public static extern int NtQuerySystemInformation(int infoClass, IntPtr info, int length, out int returnLength);

        // ---------------------------------------------------------------- CPU clocks
        [StructLayout(LayoutKind.Sequential)]
        public struct PROCESSOR_POWER_INFORMATION { public uint Number, MaxMhz, CurrentMhz, MhzLimit, MaxIdleState, CurrentIdleState; }
        [DllImport("powrprof.dll")] public static extern uint CallNtPowerInformation(int level, IntPtr input, int inLen, IntPtr output, int outLen);

        // ---------------------------------------------------------------- power plan
        [DllImport("powrprof.dll")] public static extern uint PowerGetActiveScheme(IntPtr root, out IntPtr guid);
        [DllImport("powrprof.dll")] public static extern uint PowerReadFriendlyName(IntPtr root, ref Guid scheme, IntPtr sub, IntPtr setting, IntPtr buffer, ref uint size);
        [DllImport("powrprof.dll")] public static extern uint PowerGetEffectiveOverlayScheme(out Guid overlay);
        [DllImport("kernel32.dll")] public static extern IntPtr LocalFree(IntPtr mem);

        // ---------------------------------------------------------------- display
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct DEVMODE
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
            public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
            public int dmFields, dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
            public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
            public short dmLogPixels;
            public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
            public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
        }
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool EnumDisplaySettings(string device, int mode, ref DEVMODE dm);

        // ---------------------------------------------------------------- processes
        [StructLayout(LayoutKind.Sequential)]
        public struct IO_COUNTERS { public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount; }
        [DllImport("kernel32.dll", SetLastError = true)] public static extern IntPtr OpenProcess(int access, bool inherit, int pid);
        [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll")] public static extern bool GetProcessIoCounters(IntPtr h, out IO_COUNTERS c);
        [DllImport("kernel32.dll")] public static extern bool GetProcessTimes(IntPtr h, out long creation, out long exit, out long kernel, out long user);
        [DllImport("kernel32.dll")] public static extern bool GetExitCodeProcess(IntPtr h, out int code);
        [DllImport("kernel32.dll")] public static extern bool IsWow64Process2(IntPtr process, out ushort processMachine, out ushort nativeMachine);
        [DllImport("kernel32.dll")] public static extern IntPtr GetCurrentProcess();
        public const int PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr ShellExecute(IntPtr hwnd, string verb, string file, string args, string dir, int show);
    }
}
