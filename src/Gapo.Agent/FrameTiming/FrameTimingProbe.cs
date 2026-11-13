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
    private volatile PresentTracker? tracker;

    public FrameTimingProbe()
    {
        session = EtwPresentSession.TryStart(this, out var availability);
        Availability = availability;
    }

    public FrameTimingAvailability Availability { get; }

    public bool IsTracking => tracker is not null;

    public SeriesSummary FrameMs { get; } = new();

    public SeriesSummary CpuBusyMs { get; } = new();

    public SeriesSummary InPresentMs { get; } = new();

    public long EventsSeen => session?.EventsSeen ?? 0;

    public uint EventsLost => session?.ReadEventsLost() ?? 0;

    public void Track(int processId)
    {
        if (session is not null)
        {
            tracker = new PresentTracker(processId, EtwPresentSession.TimestampTicksPerSecond, queue.Enqueue);
        }
    }

    public void StopTracking() => tracker = null;

    public int Collect()
    {
        var total = 0;
        int count;
        do
        {
            count = queue.Drain(drained);
            for (var index = 0; index < count; index++)
            {
                FrameMs.Add(drained[index].FrameMs);
                CpuBusyMs.Add(drained[index].CpuBusyMs);
                InPresentMs.Add(drained[index].InPresentMs);
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
