using System.Diagnostics;
using Gapo.Core.FrameTiming;

namespace Gapo.Windows.FrameTiming;

public sealed unsafe class EtwPresentSession : EtwRealTimeSession
{
    public const string DefaultSessionName = "GAPO-FrameTiming";

    private const ushort DxgiPresentStart = 42;
    private const ushort DxgiPresentStop = 43;
    private const ushort DxgiMultiplanePresentStart = 55;
    private const ushort DxgiMultiplanePresentStop = 56;
    private const ushort Direct3D9PresentStart = 1;
    private const ushort Direct3D9PresentStop = 2;

    private static ReadOnlySpan<ushort> DxgiPresentEvents => [DxgiPresentStart, DxgiPresentStop, DxgiMultiplanePresentStart, DxgiMultiplanePresentStop];

    private static ReadOnlySpan<ushort> Direct3D9PresentEvents => [Direct3D9PresentStart, Direct3D9PresentStop];

    private static readonly Guid DxgiProvider = new("CA11C036-0102-4A2D-A6AD-F03CFED5D3C9");
    private static readonly Guid Direct3D9Provider = new("783ACA0A-790E-4D7F-8451-AA850511C6B9");

    private readonly IPresentEventSink sink;

    private EtwPresentSession(string sessionName, IPresentEventSink sink)
        : base(sessionName)
    {
        this.sink = sink;
    }

    public static long TimestampTicksPerSecond => Stopwatch.Frequency;

    public int FocusedProcessId { get; private set; }

    public static EtwPresentSession? TryStart(IPresentEventSink sink, out FrameTimingAvailability availability, string sessionName = DefaultSessionName)
    {
        var session = new EtwPresentSession(sessionName, sink);
        availability = session.StartSession();
        if (availability == FrameTimingAvailability.Available)
        {
            return session;
        }

        session.Dispose();
        return null;
    }

    public bool FocusOn(int processId)
    {
        if (!IsRunning)
        {
            return false;
        }

        var dxgi = Enable(DxgiProvider, EtwNative.EnableProvider, 0, DxgiPresentEvents, processId) == EtwNative.Success;
        var direct3D9 = Enable(Direct3D9Provider, EtwNative.EnableProvider, 0, Direct3D9PresentEvents, processId) == EtwNative.Success;
        var focused = dxgi && direct3D9;
        FocusedProcessId = focused ? processId : 0;
        return focused;
    }

    protected override bool EnableProviders() =>
        Enable(DxgiProvider, EtwNative.EnableProvider, 0, DxgiPresentEvents, 0) == EtwNative.Success
        && Enable(Direct3D9Provider, EtwNative.EnableProvider, 0, Direct3D9PresentEvents, 0) == EtwNative.Success;

    private protected override void Dispatch(EtwNative.EventRecord* record)
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
