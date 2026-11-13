namespace Gapo.Core.FrameTiming;

public readonly record struct PresentedFrame(
    ulong SwapChain,
    double PresentStartMs,
    double FrameMs,
    double CpuBusyMs,
    double InPresentMs,
    int SyncInterval);

public interface IPresentEventSink
{
    void PresentStarted(int eventProcessId, int threadId, long timestampTicks, ulong swapChain, uint flags, int syncInterval);

    void PresentStopped(int eventProcessId, int threadId, long timestampTicks);
}

public sealed class PresentTracker : IPresentEventSink
{
    public const uint TestPresentFlag = 0x1;

    private const double MillisecondsPerSecond = 1000.0;

    private readonly int processId;
    private readonly double millisecondsPerTick;
    private readonly Dictionary<int, PendingPresent> pendingByThread = new();
    private readonly Dictionary<ulong, SwapChainHistory> historyBySwapChain = new();
    private readonly Action<PresentedFrame> onFrame;

    public PresentTracker(int processId, long timestampTicksPerSecond, Action<PresentedFrame> onFrame)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(timestampTicksPerSecond);
        this.processId = processId;
        this.onFrame = onFrame;
        millisecondsPerTick = MillisecondsPerSecond / timestampTicksPerSecond;
    }

    public long PresentsSeen { get; private set; }

    public long FramesEmitted { get; private set; }

    public void PresentStarted(int eventProcessId, int threadId, long timestampTicks, ulong swapChain, uint flags, int syncInterval)
    {
        if (eventProcessId != processId)
        {
            return;
        }

        pendingByThread[threadId] = new PendingPresent(timestampTicks, swapChain, syncInterval, IsTest: (flags & TestPresentFlag) != 0);
    }

    public void PresentStopped(int eventProcessId, int threadId, long timestampTicks)
    {
        if (eventProcessId != processId || !pendingByThread.Remove(threadId, out var pending) || pending.IsTest)
        {
            return;
        }

        PresentsSeen++;
        if (historyBySwapChain.TryGetValue(pending.SwapChain, out var previous))
        {
            FramesEmitted++;
            onFrame(new PresentedFrame(
                pending.SwapChain,
                pending.StartTicks * millisecondsPerTick,
                (pending.StartTicks - previous.StartTicks) * millisecondsPerTick,
                Math.Max(0, pending.StartTicks - previous.StopTicks) * millisecondsPerTick,
                Math.Max(0, timestampTicks - pending.StartTicks) * millisecondsPerTick,
                pending.SyncInterval));
        }

        historyBySwapChain[pending.SwapChain] = new SwapChainHistory(pending.StartTicks, timestampTicks);
    }

    public void Reset()
    {
        pendingByThread.Clear();
        historyBySwapChain.Clear();
    }

    private readonly record struct PendingPresent(long StartTicks, ulong SwapChain, int SyncInterval, bool IsTest);

    private readonly record struct SwapChainHistory(long StartTicks, long StopTicks);
}
