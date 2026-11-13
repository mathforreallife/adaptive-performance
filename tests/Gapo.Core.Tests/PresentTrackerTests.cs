using Gapo.Core.FrameTiming;
using Xunit;

namespace Gapo.Core.Tests;

public sealed class PresentTrackerTests
{
    private const int Game = 500;
    private const int OtherProcess = 900;
    private const int RenderThread = 11;
    private const long TicksPerSecond = 1_000_000;
    private const long TicksPerMillisecond = TicksPerSecond / 1000;
    private const ulong MainSwapChain = 0xA0;
    private const int Precision = 6;

    private readonly List<PresentedFrame> frames = [];
    private readonly PresentTracker tracker;

    public PresentTrackerTests()
    {
        tracker = new PresentTracker(Game, TicksPerSecond, frames.Add);
    }

    private void Present(double startMs, double stopMs, int process = Game, int thread = RenderThread, ulong swapChain = MainSwapChain, uint flags = 0)
    {
        tracker.PresentStarted(process, thread, (long)(startMs * TicksPerMillisecond), swapChain, flags, syncInterval: 0);
        tracker.PresentStopped(process, thread, (long)(stopMs * TicksPerMillisecond));
    }

    [Fact]
    public void FirstPresentOnlyEstablishesTheBaseline()
    {
        Present(0, 1);

        Assert.Empty(frames);
        Assert.Equal(1, tracker.PresentsSeen);
    }

    [Fact]
    public void FrameTimeIsMeasuredFromPresentStartToPresentStart()
    {
        Present(0, 1);
        Present(20, 22);

        var frame = Assert.Single(frames);
        Assert.Equal(20.0, frame.FrameMs, Precision);
        Assert.Equal(20.0, frame.PresentStartMs, Precision);
    }

    [Fact]
    public void CpuBusyExcludesTimeSpentInsideThePreviousPresentCall()
    {
        Present(0, 6);
        Present(20, 21);

        var frame = Assert.Single(frames);
        Assert.Equal(14.0, frame.CpuBusyMs, Precision);
        Assert.Equal(1.0, frame.InPresentMs, Precision);
    }

    [Fact]
    public void BusyPlusWaitingInPresentAddsUpToTheFrameTimeForSteadyFrames()
    {
        Present(0, 4);
        Present(16, 20);
        Present(32, 36);

        Assert.All(frames, frame => Assert.Equal(frame.FrameMs, frame.CpuBusyMs + frame.InPresentMs, Precision));
    }

    [Fact]
    public void IgnoresOtherProcesses()
    {
        Present(0, 1, process: OtherProcess);
        Present(10, 11, process: OtherProcess);

        Assert.Empty(frames);
        Assert.Equal(0, tracker.PresentsSeen);
    }

    [Fact]
    public void IgnoresTestPresentsThatShowNothing()
    {
        Present(0, 1);
        Present(5, 5.1, flags: PresentTracker.TestPresentFlag);
        Present(20, 21);

        var frame = Assert.Single(frames);
        Assert.Equal(20.0, frame.FrameMs, Precision);
    }

    [Fact]
    public void TracksEachSwapChainSeparately()
    {
        const ulong OverlaySwapChain = 0xB0;
        Present(0, 1);
        Present(3, 4, swapChain: OverlaySwapChain);
        Present(16, 17);
        Present(103, 104, swapChain: OverlaySwapChain);

        Assert.Equal(2, frames.Count);
        Assert.Equal(16.0, frames.Single(frame => frame.SwapChain == MainSwapChain).FrameMs, Precision);
        Assert.Equal(100.0, frames.Single(frame => frame.SwapChain == OverlaySwapChain).FrameMs, Precision);
    }

    [Fact]
    public void StopWithoutAStartIsIgnored()
    {
        tracker.PresentStopped(Game, RenderThread, 5 * TicksPerMillisecond);

        Assert.Empty(frames);
        Assert.Equal(0, tracker.PresentsSeen);
    }

    [Fact]
    public void PresentsOnDifferentThreadsDoNotPairWithEachOther()
    {
        tracker.PresentStarted(Game, threadId: 1, 0, MainSwapChain, 0, 0);
        tracker.PresentStopped(Game, threadId: 2, 1 * TicksPerMillisecond);

        Assert.Equal(0, tracker.PresentsSeen);
    }

    [Fact]
    public void ResetForgetsHistorySoAGapIsNotReportedAsAFrame()
    {
        Present(0, 1);
        tracker.Reset();
        Present(5000, 5001);

        Assert.Empty(frames);
    }
}
