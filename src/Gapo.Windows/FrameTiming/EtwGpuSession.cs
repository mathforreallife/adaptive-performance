using Gapo.Core.FrameTiming;

namespace Gapo.Windows.FrameTiming;

public sealed unsafe class EtwGpuSession : EtwRealTimeSession
{
    public const string DefaultSessionName = "GAPO-GpuTiming";

    private const uint CaptureState = 2;
    private const ulong BaseKeyword = 0x1;
    private const ulong ResourceKeyword = 0x40;
    private const ulong LongHaulKeyword = 0x800;

    private const ushort DeviceStart = 0x1b;
    private const ushort DeviceStop = 0x1c;
    private const ushort DeviceRundown = 0x1d;
    private const ushort ContextStart = 0x1e;
    private const ushort ContextStop = 0x1f;
    private const ushort ContextRundown = 0x20;
    private const ushort DmaPacketStart = 0xaf;
    private const ushort DmaPacketInfo = 0xb1;
    private const ushort QueuePacketStart = 0xb2;
    private const ushort QueuePacketStartSecond = 0xf4;
    private const ushort QueuePacketStartThird = 0xf5;

    private static readonly Guid DxgKrnlProvider = new("802EC45A-1E99-4B83-9920-87C98277BA9D");

    private static ReadOnlySpan<ushort> TrackedEvents => [DeviceStart, DeviceStop, DeviceRundown, ContextStart, ContextStop, ContextRundown, DmaPacketStart, DmaPacketInfo, QueuePacketStart, QueuePacketStartSecond, QueuePacketStartThird];

    private static ReadOnlySpan<ushort> RundownEvents => [DeviceRundown, ContextRundown];

    private readonly GpuBusyTracker tracker = new();
    private readonly Dictionary<(ushort Id, byte Version), EventLayout?> layouts = [];
    private readonly Lock gate = new();
    private readonly Dictionary<ulong, (ulong Device, uint Node)> contextsWithoutProcess = [];

    private EtwGpuSession(string sessionName)
        : base(sessionName)
    {
    }

    public long PacketsSeen { get; private set; }

    public static EtwGpuSession? TryStart(out FrameTimingAvailability availability, string sessionName = DefaultSessionName)
    {
        var session = new EtwGpuSession(sessionName);
        availability = session.StartSession();
        if (availability == FrameTimingAvailability.Available)
        {
            return session;
        }

        session.Dispose();
        return null;
    }

    public long BusyTicks(int processId)
    {
        lock (gate)
        {
            return tracker.BusyTicks(processId);
        }
    }

    protected override bool EnableProviders()
    {
        const ulong Keywords = BaseKeyword | ResourceKeyword | LongHaulKeyword;
        if (Enable(DxgKrnlProvider, EtwNative.EnableProvider, Keywords, TrackedEvents, 0) != EtwNative.Success)
        {
            return false;
        }

        Enable(DxgKrnlProvider, CaptureState, ResourceKeyword | LongHaulKeyword, RundownEvents, 0);
        return true;
    }

    private protected override void Dispatch(EtwNative.EventRecord* record)
    {
        EventsSeen++;
        if (record->ProviderId != DxgKrnlProvider || Layout(record) is not { } layout)
        {
            return;
        }

        lock (gate)
        {
            switch (record->EventId)
            {
                case DeviceStart or DeviceRundown:
                    tracker.DeviceStarted(layout.Unsigned(record, "hDevice"), layout.Unsigned(record, "pDxgAdapter"));
                    break;
                case DeviceStop:
                    tracker.DeviceStopped(layout.Unsigned(record, "hDevice"));
                    break;
                case ContextStart:
                    tracker.ContextStarted(layout.Unsigned(record, "hContext"), layout.Unsigned(record, "hDevice"), (uint)layout.Unsigned(record, "NodeOrdinal"), (int)record->ProcessId);
                    break;
                case ContextRundown:
                    RememberContextWithoutProcess(layout, record);
                    break;
                case ContextStop:
                    tracker.ContextStopped(layout.Unsigned(record, "hContext"));
                    break;
                case QueuePacketStart or QueuePacketStartSecond or QueuePacketStartThird:
                    AssignProcess(layout.Unsigned(record, "hContext"), (int)record->ProcessId);
                    break;
                case DmaPacketStart:
                    PacketsSeen++;
                    tracker.PacketQueued(layout.Unsigned(record, "hContext"), (uint)layout.Unsigned(record, "ulQueueSubmitSequence"), record->TimeStamp);
                    break;
                case DmaPacketInfo:
                    tracker.PacketCompleted(layout.Unsigned(record, "hContext"), (uint)layout.Unsigned(record, "ulQueueSubmitSequence"), record->TimeStamp);
                    break;
            }
        }
    }

    private void RememberContextWithoutProcess(EventLayout layout, EtwNative.EventRecord* record) =>
        contextsWithoutProcess[layout.Unsigned(record, "hContext")] = (layout.Unsigned(record, "hDevice"), (uint)layout.Unsigned(record, "NodeOrdinal"));

    private void AssignProcess(ulong context, int processId)
    {
        if (processId == 0 || !contextsWithoutProcess.Remove(context, out var pending))
        {
            return;
        }

        tracker.ContextStarted(context, pending.Device, pending.Node, processId);
    }

    private EventLayout? Layout(EtwNative.EventRecord* record)
    {
        var key = (record->EventId, record->Version);
        if (!layouts.TryGetValue(key, out var layout))
        {
            layout = EventLayout.Read(record);
            layouts[key] = layout;
        }

        return layout;
    }
}
