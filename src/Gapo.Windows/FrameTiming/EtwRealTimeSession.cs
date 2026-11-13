using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Gapo.Windows.FrameTiming;

public enum FrameTimingAvailability
{
    Available,
    AccessDenied,
    Failed,
}

public abstract unsafe class EtwRealTimeSession : IDisposable
{
    private const int SessionNameCapacityBytes = 512;
    private const uint SessionBufferKilobytes = 64;
    private const uint FlushTimerSeconds = 1;
    private const int ConsumerStopTimeoutMs = 3000;
    private const int FilterHeaderBytes = 4;
    private const byte IncludeListedEvents = 1;

    private readonly string sessionName;
    private EtwNative.TraceProperties* properties;
    private ulong sessionHandle;
    private ulong traceHandle = EtwNative.InvalidProcessTraceHandle;
    private GCHandle selfHandle;
    private Thread? consumer;

    protected EtwRealTimeSession(string sessionName)
    {
        this.sessionName = sessionName;
    }

    public long EventsSeen { get; protected set; }

    public uint LastErrorCode { get; protected set; }

    protected bool IsRunning => sessionHandle != 0;

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
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
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

    protected FrameTimingAvailability StartSession()
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

        if (!EnableProviders() || !OpenForConsumption())
        {
            return FrameTimingAvailability.Failed;
        }

        consumer = new Thread(ConsumeUntilStopped) { IsBackground = true, Name = "GAPO " + sessionName };
        consumer.Start();
        return FrameTimingAvailability.Available;
    }

    protected abstract bool EnableProviders();

    private protected abstract void Dispatch(EtwNative.EventRecord* record);

    protected uint Enable(in Guid provider, uint controlCode, ulong anyKeyword, ReadOnlySpan<ushort> wantedEventIds, int processId)
    {
        var filterBytes = FilterHeaderBytes + (wantedEventIds.Length * sizeof(ushort));
        var filter = stackalloc byte[filterBytes];
        filter[0] = IncludeListedEvents;
        filter[1] = 0;
        *(ushort*)(filter + 2) = (ushort)wantedEventIds.Length;
        wantedEventIds.CopyTo(new Span<ushort>(filter + FilterHeaderBytes, wantedEventIds.Length));
        var processIds = stackalloc uint[1];
        processIds[0] = (uint)processId;
        var descriptors = stackalloc EtwNative.EventFilterDescriptor[2];
        descriptors[0] = new EtwNative.EventFilterDescriptor { Pointer = (ulong)filter, Size = (uint)filterBytes, Type = EtwNative.EventIdFilterType };
        descriptors[1] = new EtwNative.EventFilterDescriptor { Pointer = (ulong)processIds, Size = sizeof(uint), Type = EtwNative.ProcessIdFilterType };
        var parameters = new EtwNative.EnableParameters { Version = EtwNative.EnableParametersVersion, FilterDescriptors = descriptors, FilterDescriptorCount = processId > 0 ? 2u : 1u };
        var status = EtwNative.EnableTraceEx2(sessionHandle, provider, controlCode, EtwNative.InformationLevel, anyKeyword, 0, 0, &parameters);
        if (status != EtwNative.Success && processId > 0)
        {
            parameters.FilterDescriptorCount = 1;
            var withoutProcessFilter = EtwNative.EnableTraceEx2(sessionHandle, provider, controlCode, EtwNative.InformationLevel, anyKeyword, 0, 0, &parameters);
            if (withoutProcessFilter != EtwNative.Success)
            {
                EtwNative.EnableTraceEx2(sessionHandle, provider, controlCode, EtwNative.InformationLevel, anyKeyword, 0, 0, null);
            }

            LastErrorCode = status;
            return status;
        }

        if (status != EtwNative.Success)
        {
            status = EtwNative.EnableTraceEx2(sessionHandle, provider, controlCode, EtwNative.InformationLevel, anyKeyword, 0, 0, null);
        }

        LastErrorCode = status;
        return status;
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
        if (GCHandle.FromIntPtr((nint)record->UserContext).Target is EtwRealTimeSession session)
        {
            session.Dispatch(record);
        }
    }
}
