using System.Runtime.InteropServices;

namespace Gapo.Windows.Hardware;

internal static unsafe partial class HardwareNative
{
    internal const int RelationAll = 0xFFFF;
    internal const int ProcessorInformationLevel = 11;
    internal const uint RawSmbiosProvider = 0x52534D42;
    internal const int CurrentDisplaySettings = -1;
    internal const int MonitorCountMetric = 80;
    internal const uint ShareReadWrite = 0x3;
    internal const uint OpenExisting = 3;
    internal const uint StorageQueryProperty = 0x002D1400;
    internal const nint InvalidHandle = -1;

    [StructLayout(LayoutKind.Sequential)]
    internal struct ProcessorPowerInformation
    {
        public uint Number;
        public uint MaximumMegahertz;
        public uint CurrentMegahertz;
        public uint LimitMegahertz;
        public uint MaximumIdleState;
        public uint CurrentIdleState;
    }

    [StructLayout(LayoutKind.Explicit, Size = SizeInBytes)]
    internal struct DisplayMode
    {
        internal const int SizeInBytes = 220;

        [FieldOffset(68)] public ushort Size;
        [FieldOffset(172)] public uint Width;
        [FieldOffset(176)] public uint Height;
        [FieldOffset(184)] public uint Frequency;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SystemPowerStatus
    {
        public byte MainsStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct StoragePropertyQuery
    {
        public uint PropertyId;
        public uint QueryType;
        public uint AdditionalParameters;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct AdapterDescription
    {
        internal const int NameCharacters = 128;

        public fixed char Name[NameCharacters];
        public uint VendorId;
        public uint DeviceId;
        public uint SubsystemId;
        public uint Revision;
        public nuint DedicatedVideoMemory;
        public nuint DedicatedSystemMemory;
        public nuint SharedSystemMemory;
        public long Luid;
        public uint Flags;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetLogicalProcessorInformationEx(int relationship, byte* buffer, ref uint length);

    [LibraryImport("powrprof.dll")]
    internal static partial int CallNtPowerInformation(int level, void* input, uint inputLength, void* output, uint outputLength);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial uint GetSystemFirmwareTable(uint provider, uint tableId, byte* buffer, uint bufferSize);

    [LibraryImport("dxgi.dll")]
    internal static partial int CreateDXGIFactory1(in Guid interfaceId, out nint factory);

    [LibraryImport("user32.dll", EntryPoint = "EnumDisplaySettingsW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool EnumDisplaySettings(char* deviceName, int modeNumber, DisplayMode* mode);

    [LibraryImport("user32.dll")]
    internal static partial int GetSystemMetrics(int index);

    [LibraryImport("user32.dll")]
    internal static partial uint GetDpiForSystem();

    [LibraryImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetDiskFreeSpaceEx(string directory, out ulong freeBytesAvailable, out ulong totalBytes, out ulong totalFreeBytes);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    internal static partial nint CreateFile(string path, uint access, uint share, void* security, uint disposition, uint flags, nint template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DeviceIoControl(nint device, uint controlCode, void* input, uint inputSize, void* output, uint outputSize, out uint returned, void* overlapped);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseHandle(nint handle);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetSystemPowerStatus(out SystemPowerStatus status);

    [LibraryImport("powrprof.dll")]
    internal static partial uint PowerGetActiveScheme(nint userRootPowerKey, out Guid* activeScheme);

    [LibraryImport("powrprof.dll")]
    internal static partial uint PowerReadFriendlyName(nint rootPowerKey, Guid* scheme, Guid* subgroup, Guid* setting, byte* buffer, ref uint bufferSize);

    [LibraryImport("powrprof.dll")]
    internal static partial uint PowerGetEffectiveOverlayScheme(out Guid overlayScheme);

    [LibraryImport("kernel32.dll")]
    internal static partial nint LocalFree(void* memory);
}
