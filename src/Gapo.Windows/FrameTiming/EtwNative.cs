using System.Runtime.InteropServices;

namespace Gapo.Windows.FrameTiming;

internal static unsafe partial class EtwNative
{
    internal const uint Success = 0;
    internal const uint AccessDenied = 5;
    internal const uint AlreadyExists = 183;
    internal const uint ContextClosePending = 7007;

    internal const uint TracedGuidFlag = 0x00020000;
    internal const uint QueryPerformanceCounterClock = 1;
    internal const uint RealTimeLogFileMode = 0x00000100;
    internal const uint ControlStop = 1;
    internal const uint EnableProvider = 1;
    internal const byte InformationLevel = 4;

    internal const uint ProcessRealTime = 0x00000100;
    internal const uint ProcessRawTimestamp = 0x00001000;
    internal const uint ProcessEventRecord = 0x10000000;
    internal const ulong InvalidProcessTraceHandle = ulong.MaxValue;

    internal const ushort ThirtyTwoBitHeaderFlag = 0x0020;

    [StructLayout(LayoutKind.Explicit, Size = SizeInBytes)]
    internal struct TraceProperties
    {
        internal const int SizeInBytes = 120;

        [FieldOffset(0)] public uint BufferSize;
        [FieldOffset(40)] public uint ClientContext;
        [FieldOffset(44)] public uint Flags;
        [FieldOffset(48)] public uint SessionBufferKilobytes;
        [FieldOffset(64)] public uint LogFileMode;
        [FieldOffset(68)] public uint FlushTimerSeconds;
        [FieldOffset(88)] public uint EventsLost;
        [FieldOffset(112)] public uint LogFileNameOffset;
        [FieldOffset(116)] public uint LoggerNameOffset;
    }

    [StructLayout(LayoutKind.Explicit, Size = SizeInBytes)]
    internal struct TraceLogFile
    {
        internal const int SizeInBytes = 448;

        [FieldOffset(8)] public char* LoggerName;
        [FieldOffset(28)] public uint ProcessTraceMode;
        [FieldOffset(424)] public delegate* unmanaged[Stdcall]<EventRecord*, void> EventRecordCallback;
        [FieldOffset(440)] public void* Context;
    }

    [StructLayout(LayoutKind.Explicit, Size = SizeInBytes)]
    internal struct EventRecord
    {
        internal const int SizeInBytes = 112;

        [FieldOffset(4)] public ushort Flags;
        [FieldOffset(8)] public uint ThreadId;
        [FieldOffset(12)] public uint ProcessId;
        [FieldOffset(16)] public long TimeStamp;
        [FieldOffset(24)] public Guid ProviderId;
        [FieldOffset(40)] public ushort EventId;
        [FieldOffset(86)] public ushort UserDataLength;
        [FieldOffset(96)] public byte* UserData;
        [FieldOffset(104)] public void* UserContext;
    }

    [LibraryImport("advapi32.dll", EntryPoint = "StartTraceW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial uint StartTrace(out ulong sessionHandle, string sessionName, TraceProperties* properties);

    [LibraryImport("advapi32.dll", EntryPoint = "ControlTraceW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial uint ControlTrace(ulong sessionHandle, string? sessionName, TraceProperties* properties, uint controlCode);

    [LibraryImport("advapi32.dll")]
    internal static partial uint EnableTraceEx2(
        ulong sessionHandle,
        in Guid providerId,
        uint controlCode,
        byte level,
        ulong matchAnyKeyword,
        ulong matchAllKeyword,
        uint timeoutMilliseconds,
        void* enableParameters);

    [LibraryImport("advapi32.dll", EntryPoint = "OpenTraceW")]
    internal static partial ulong OpenTrace(TraceLogFile* logFile);

    [LibraryImport("advapi32.dll")]
    internal static partial uint ProcessTrace(ulong* handles, uint handleCount, void* startTime, void* endTime);

    [LibraryImport("advapi32.dll")]
    internal static partial uint CloseTrace(ulong traceHandle);
}
