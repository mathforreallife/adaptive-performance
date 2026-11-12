using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Gapo.Core.FrameTiming;

namespace Gapo.Windows.FrameTiming;

public enum FrameTimingAvailability
{
    Available,
    AccessDenied,
    Failed,
}

public sealed unsafe class EtwPresentSession : IDisposable
{
    public const string DefaultSessionName = "GAPO-FrameTiming";

    private const int SessionNameCapacityBytes = 512;
    private const uint SessionBufferKilobytes = 64;
    private const uint FlushTimerSeconds = 1;
    private const int ConsumerStopTimeoutMs = 3000;

    private const ushort DxgiPresentStart = 42;
    private const ushort DxgiPresentStop = 43;
    private const ushort DxgiMultiplanePresentStart = 55;
    private const ushort DxgiMultiplanePresentStop = 56;
    private const ushort Direct3D9PresentStart = 1;
    private const ushort Direct3D9PresentStop = 2;

    private static readonly Guid DxgiProvider = new("CA11C036-0102-4A2D-A6AD-F03CFED5D3C9");
    private static readonly Guid Direct3D9Provider = new("783ACA0A-790E-4D7F-8451-AA850511C6B9");

    private readonly string sessionName;
    private readonly IPresentEventSink sink;
    private EtwNative.TraceProperties* properties;
    private ulong sessionHandle;
    private ulong traceHandle = EtwNative.InvalidProcessTraceHandle;
    private GCHandle selfHandle;
    private Thread? consumer;

    private EtwPresentSession(string sessionName, IPresentEventSink sink)
    {
        this.sessionName = sessionName;
        this.sink = sink;
    }

    public static long TimestampTicksPerSecond => Stopwatch.Frequency;

    public long EventsSeen { get; private set; }

    public uint LastErrorCode { get; private set; }

    public static EtwPresentSession? TryStart(IPresentEventSink sink, out FrameTimingAvailability availability, string sessionName = DefaultSessionName)
    {
        var session = new EtwPresentSession(sessionName, sink);
        availability = session.Start();
        if (availability == FrameTimingAvailability.Available)
        {
            return session;
        }

        session.Dispose();
        return null;
    }

    public uint ReadEventsLost()
    {
        if (sessionHandle == 0)
        {
            return 0;
        }

        var query = AllocateProperties();
        try
        {
            const uint ControlQuery = 0;
            return EtwNative.ControlTrace(sessionHandle, null, query, ControlQuery) == EtwNative.Success ? query->EventsLost : 0;
        }
        finally
        {
            NativeMemory.Free(query);
        }
    }

    public void Dispose()
    {
        if (sessionHandle != 0)
        {
            EtwNative.ControlTrace(sessionHandle, null, properties, EtwNative.ControlStop);
            sessionHandle = 0;
        }

        if (traceHandle != EtwNative.InvalidProcessTraceHandle)
        {
            EtwNative.CloseTrace(traceHandle);
            traceHandle = EtwNative.InvalidProcessTraceHandle;
        }

        consumer?.Join(ConsumerStopTimeoutMs);
        consumer = null;
        if (selfHandle.IsAllocated)
        {
            selfHandle.Free();
        }

        if (properties != null)
        {
            NativeMemory.Free(properties);
            properties = null;
        }
    }

    private FrameTimingAvailability Start()
    {
        properties = AllocateProperties();
        var status = EtwNative.StartTrace(out sessionHandle, sessionName, properties);
        if (status == EtwNative.AlreadyExists)
        {
            var stale = AllocateProperties();
            EtwNative.ControlTrace(0, sessionName, stale, EtwNative.ControlStop);
            NativeMemory.Free(stale);
            status = EtwNative.StartTrace(out sessionHandle, sessionName, properties);
        }

        if (status != EtwNative.Success)
        {
            sessionHandle = 0;
            LastErrorCode = status;
            return status == EtwNative.AccessDenied ? FrameTimingAvailability.AccessDenied : FrameTimingAvailability.Failed;
        }

        if (!Enable(DxgiProvider) || !Enable(Direct3D9Provider) || !OpenForConsumption())
        {
            return FrameTimingAvailability.Failed;
        }

        consumer = new Thread(ConsumeUntilStopped) { IsBackground = true, Name = "GAPO frame timing" };
        consumer.Start();
        return FrameTimingAvailability.Available;
    }

    private static EtwNative.TraceProperties* AllocateProperties()
    {
        var totalBytes = (nuint)(EtwNative.TraceProperties.SizeInBytes + (2 * SessionNameCapacityBytes));
        var allocated = (EtwNative.TraceProperties*)NativeMemory.AllocZeroed(totalBytes);
        allocated->BufferSize = (uint)totalBytes;
        allocated->Flags = EtwNative.TracedGuidFlag;
        allocated->ClientContext = EtwNative.QueryPerformanceCounterClock;
        allocated->LogFileMode = EtwNative.RealTimeLogFileMode;
        allocated->SessionBufferKilobytes = SessionBufferKilobytes;
        allocated->FlushTimerSeconds = FlushTimerSeconds;
        allocated->LoggerNameOffset = EtwNative.TraceProperties.SizeInBytes;
        allocated->LogFileNameOffset = EtwNative.TraceProperties.SizeInBytes + SessionNameCapacityBytes;
        return allocated;
    }

    private bool Enable(in Guid provider)
    {
        var status = EtwNative.EnableTraceEx2(sessionHandle, provider, EtwNative.EnableProvider, EtwNative.InformationLevel, 0, 0, 0, null);
        LastErrorCode = status;
        return status == EtwNative.Success;
    }

    private bool OpenForConsumption()
    {
        selfHandle = GCHandle.Alloc(this);
        var logFile = default(EtwNative.TraceLogFile);
        fixed (char* name = sessionName)
        {
            logFile.LoggerName = name;
            logFile.ProcessTraceMode = EtwNative.ProcessRealTime | EtwNative.ProcessEventRecord | EtwNative.ProcessRawTimestamp;
            logFile.EventRecordCallback = &OnEventRecord;
            logFile.Context = (void*)GCHandle.ToIntPtr(selfHandle);
            traceHandle = EtwNative.OpenTrace(&logFile);
        }

        if (traceHandle == EtwNative.InvalidProcessTraceHandle)
        {
            LastErrorCode = (uint)Marshal.GetLastPInvokeError();
            return false;
        }

        return true;
    }

    private void ConsumeUntilStopped()
    {
        var handle = traceHandle;
        EtwNative.ProcessTrace(&handle, 1, null, null);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void OnEventRecord(EtwNative.EventRecord* record)
    {
        if (GCHandle.FromIntPtr((nint)record->UserContext).Target is EtwPresentSession session)
        {
            session.Dispatch(record);
        }
    }

    private void Dispatch(EtwNative.EventRecord* record)
    {
        EventsSeen++;
        var isDxgi = record->ProviderId == DxgiProvider;
        if (!isDxgi && record->ProviderId != Direct3D9Provider)
        {
            return;
        }

        var eventId = record->EventId;
        if (isDxgi ? eventId is DxgiPresentStart or DxgiMultiplanePresentStart : eventId == Direct3D9PresentStart)
        {
            ReadPresentStart(record, hasSyncInterval: isDxgi, out var swapChain, out var flags, out var syncInterval);
            sink.PresentStarted((int)record->ProcessId, (int)record->ThreadId, record->TimeStamp, swapChain, flags, syncInterval);
        }
        else if (isDxgi ? eventId is DxgiPresentStop or DxgiMultiplanePresentStop : eventId == Direct3D9PresentStop)
        {
            sink.PresentStopped((int)record->ProcessId, (int)record->ThreadId, record->TimeStamp);
        }
    }

    private static void ReadPresentStart(EtwNative.EventRecord* record, bool hasSyncInterval, out ulong swapChain, out uint flags, out int syncInterval)
    {
        var pointerBytes = (record->Flags & EtwNative.ThirtyTwoBitHeaderFlag) != 0 ? sizeof(uint) : sizeof(ulong);
        var data = record->UserData;
        var length = record->UserDataLength;
        swapChain = 0;
        flags = 0;
        syncInterval = 0;
        if (data == null || length < pointerBytes + sizeof(uint))
        {
            return;
        }

        swapChain = pointerBytes == sizeof(uint) ? *(uint*)data : *(ulong*)data;
        flags = *(uint*)(data + pointerBytes);
        if (hasSyncInterval && length >= pointerBytes + sizeof(uint) + sizeof(int))
        {
            syncInterval = *(int*)(data + pointerBytes + sizeof(uint));
        }
    }
}
