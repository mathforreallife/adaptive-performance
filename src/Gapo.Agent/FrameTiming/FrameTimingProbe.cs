using Gapo.Core.FrameTiming;
using Gapo.Core.Statistics;
using Gapo.Windows.FrameTiming;

namespace Gapo.Agent.FrameTiming;

public sealed class FrameTimingProbe : IPresentEventSink, IDisposable
{
    private const int DrainBatch = 512;

    private readonly PresentedFrameQueue queue = new();
    private readonly PresentedFrame[] drained = new PresentedFrame[DrainBatch];
    private readonly EtwPresentSession? session;
    private readonly bool keepsSummaries;
    private volatile PresentTracker? tracker;

    public FrameTimingProbe(bool keepsSummaries = true)
    {
        this.keepsSummaries = keepsSummaries;
        session = EtwPresentSession.TryStart(this, out var availability);
        Availability = availability;
    }

    public FrameTimingAvailability Availability { get; }

    public bool IsTracking => tracker is not null;

    public int TrackedProcessId { get; private set; }

    public Action<PresentedFrame>? FrameObserver { get; set; }

    public SeriesSummary FrameMs { get; } = new();

    public SeriesSummary CpuBusyMs { get; } = new();

    public SeriesSummary InPresentMs { get; } = new();

    public long EventsSeen => session?.EventsSeen ?? 0;

    public bool OnlyTheGamesEventsWereDelivered { get; private set; }

    public uint EventsLost => session?.ReadEventsLost() ?? 0;

    public void Track(int processId)
    {
        TrackedProcessId = processId;
        if (session is not null)
        {
            OnlyTheGamesEventsWereDelivered = session.FocusOn(processId);
            tracker = new PresentTracker(processId, EtwPresentSession.TimestampTicksPerSecond, queue.Enqueue);
        }
    }

    public void StopTracking()
    {
        tracker = null;
        TrackedProcessId = 0;
        session?.FocusOn(0);
    }

    public int Collect()
    {
        var total = 0;
        int count;
        do
        {
            count = queue.Drain(drained);
            for (var index = 0; index < count; index++)
            {
                if (keepsSummaries)
                {
                    FrameMs.Add(drained[index].FrameMs);
                    CpuBusyMs.Add(drained[index].CpuBusyMs);
                    InPresentMs.Add(drained[index].InPresentMs);
                }

                FrameObserver?.Invoke(drained[index]);
            }

            total += count;
        }
        while (count == drained.Length);

        return total;
    }

    public void PresentStarted(int eventProcessId, int threadId, long timestampTicks, ulong swapChain, uint flags, int syncInterval) =>
        tracker?.PresentStarted(eventProcessId, threadId, timestampTicks, swapChain, flags, syncInterval);

    public void PresentStopped(int eventProcessId, int threadId, long timestampTicks) =>
        tracker?.PresentStopped(eventProcessId, threadId, timestampTicks);

    public void Dispose() => session?.Dispose();
}
