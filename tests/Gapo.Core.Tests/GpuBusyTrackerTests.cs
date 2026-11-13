using Gapo.Core.FrameTiming;
using Xunit;

namespace Gapo.Core.Tests;

public sealed class GpuBusyTrackerTests
{
    private const ulong Adapter = 0xA;
    private const ulong GameDevice = 0x10;
    private const ulong OtherDevice = 0x20;
    private const ulong GameRender = 0x100;
    private const ulong GameCopy = 0x101;
    private const ulong OtherRender = 0x200;
    private const int Game = 42;
    private const int Other = 7;

    [Fact]
    public void BackToBackPacketsCountAsOneBusyStretch()
    {
        var tracker = Tracker();

        tracker.PacketQueued(GameRender, 1, 100);
        tracker.PacketQueued(GameRender, 2, 110);
        tracker.PacketCompleted(GameRender, 1, 150);
        tracker.PacketCompleted(GameRender, 2, 190);

        Assert.Equal(90, tracker.BusyTicks(Game));
    }

    [Fact]
    public void IdleTimeBetweenPacketsIsNotCounted()
    {
        var tracker = Tracker();

        tracker.PacketQueued(GameRender, 1, 100);
        tracker.PacketCompleted(GameRender, 1, 130);
        tracker.PacketQueued(GameRender, 2, 200);
        tracker.PacketCompleted(GameRender, 2, 220);

        Assert.Equal(50, tracker.BusyTicks(Game));
    }

    [Fact]
    public void WorkOnTwoEnginesAtOnceIsCountedOnceForTheProcess()
    {
        var tracker = Tracker();

        tracker.PacketQueued(GameRender, 1, 100);
        tracker.PacketQueued(GameCopy, 1, 120);
        tracker.PacketCompleted(GameCopy, 1, 140);
        tracker.PacketCompleted(GameRender, 1, 160);

        Assert.Equal(60, tracker.BusyTicks(Game));
    }

    [Fact]
    public void AnotherProcessesWorkQueuedAheadDelaysTheGameButIsNotCountedForIt()
    {
        var tracker = Tracker();

        tracker.PacketQueued(OtherRender, 1, 100);
        tracker.PacketQueued(GameRender, 2, 105);
        tracker.PacketCompleted(OtherRender, 1, 140);
        tracker.PacketCompleted(GameRender, 2, 170);

        Assert.Equal(30, tracker.BusyTicks(Game));
        Assert.Equal(40, tracker.BusyTicks(Other));
    }

    [Fact]
    public void AMissedCompletionIsSkippedAndTheTimeGoesToThePacketThatWasRunning()
    {
        var tracker = Tracker();

        tracker.PacketQueued(GameRender, 1, 100);
        tracker.PacketQueued(GameRender, 2, 110);
        tracker.PacketQueued(GameRender, 3, 120);
        tracker.PacketCompleted(GameRender, 2, 170);
        tracker.PacketCompleted(GameRender, 3, 200);

        Assert.Equal(100, tracker.BusyTicks(Game));
    }

    [Fact]
    public void StaleOrUnknownCompletionsAndPacketsWithoutWorkAreIgnored()
    {
        var tracker = Tracker();

        tracker.PacketQueued(GameRender, 0, 50);
        tracker.PacketQueued(0xDEAD, 5, 60);
        tracker.PacketQueued(GameRender, 5, 100);
        tracker.PacketCompleted(GameRender, 4, 120);
        tracker.PacketCompleted(GameRender, 9, 130);
        tracker.PacketCompleted(GameRender, 5, 140);

        Assert.Equal(40, tracker.BusyTicks(Game));
    }

    [Fact]
    public void BusyTimeAccumulatesAndALongQueueGrows()
    {
        var tracker = Tracker();
        tracker.PacketQueued(GameRender, 1, 0);
        tracker.PacketCompleted(GameRender, 1, 10);
        var before = tracker.BusyTicks(Game);

        for (uint sequence = 2; sequence < 60; sequence++)
        {
            tracker.PacketQueued(GameRender, sequence, 100 + sequence);
        }

        for (uint sequence = 2; sequence < 60; sequence++)
        {
            tracker.PacketCompleted(GameRender, sequence, 200 + sequence);
        }

        Assert.Equal(10, before);
        Assert.Equal(10 + 259 - 102, tracker.BusyTicks(Game));
    }

    [Fact]
    public void AContextOnAnUnknownDeviceIsNotTracked()
    {
        var tracker = new GpuBusyTracker();
        tracker.ContextStarted(GameRender, GameDevice, 0, Game);

        tracker.PacketQueued(GameRender, 1, 100);
        tracker.PacketCompleted(GameRender, 1, 150);

        Assert.Equal(0, tracker.BusyTicks(Game));
        Assert.Equal(0, tracker.KnownContexts);
    }

    private static GpuBusyTracker Tracker()
    {
        var tracker = new GpuBusyTracker();
        tracker.DeviceStarted(GameDevice, Adapter);
        tracker.DeviceStarted(OtherDevice, Adapter);
        tracker.ContextStarted(GameRender, GameDevice, 0, Game);
        tracker.ContextStarted(GameCopy, GameDevice, 1, Game);
        tracker.ContextStarted(OtherRender, OtherDevice, 0, Other);
        return tracker;
    }
}
