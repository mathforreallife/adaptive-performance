using System.Runtime.InteropServices;
using Xunit;

namespace Gapo.Sandbox.Contract.Tests;

public sealed class TelemetryContractTests
{
    private static string UniqueMapName() => "GAPO_Sandbox_Test_" + Guid.NewGuid().ToString("N");

    [Fact]
    public void FrameRecordKeepsTheDemoSizeOf120Bytes()
    {
        Assert.Equal(120, Marshal.SizeOf<FrameRecord>());
        Assert.Equal(120, TelemetryChannel.RecordSize);
    }

    [Theory]
    [InlineData(nameof(FrameRecord.FrameIndex), 0)]
    [InlineData(nameof(FrameRecord.TimeMs), 8)]
    [InlineData(nameof(FrameRecord.FrameMs), 16)]
    [InlineData(nameof(FrameRecord.WorkMs), 20)]
    [InlineData(nameof(FrameRecord.SimMs), 24)]
    [InlineData(nameof(FrameRecord.RenderMs), 40)]
    [InlineData(nameof(FrameRecord.PresentMs), 72)]
    [InlineData(nameof(FrameRecord.IoMs), 76)]
    [InlineData(nameof(FrameRecord.WaitMs), 80)]
    [InlineData(nameof(FrameRecord.Revision), 84)]
    [InlineData(nameof(FrameRecord.Flags), 88)]
    [InlineData(nameof(FrameRecord.RenderThreads), 104)]
    [InlineData(nameof(FrameRecord.Reserved), 116)]
    public void FrameRecordFieldsKeepTheirOffsets(string field, int expectedOffset)
    {
        Assert.Equal(expectedOffset, (int)Marshal.OffsetOf<FrameRecord>(field));
    }

    [Fact]
    public void MapSizeMatchesHeaderPlusRing()
    {
        Assert.Equal(495_616, TelemetryChannel.TotalSize);
    }

    [Fact]
    public void ReaderFindsNothingWhenNoWriterExists()
    {
        Assert.Null(TelemetryChannel.TryOpenForReader(UniqueMapName()));
    }

    [Fact]
    public void ReaderSeesHeaderValuesWrittenByTheGame()
    {
        var mapName = UniqueMapName();
        using var writer = TelemetryChannel.CreateForWriter(mapName);
        writer.Pid = 4321;
        writer.State = SandboxState.Running;
        writer.AppliedRevision = 7;
        writer.CalibratedUltraMs = 30.5f;
        writer.SettingsPath = @"C:\games\sandbox-settings.ini";
        writer.GameTitle = "GAPO Sandbox";

        using var reader = TelemetryChannel.TryOpenForReader(mapName);

        Assert.NotNull(reader);
        Assert.Equal(4321, reader.Pid);
        Assert.Equal(SandboxState.Running, reader.State);
        Assert.Equal(7, reader.AppliedRevision);
        Assert.Equal(30.5f, reader.CalibratedUltraMs);
        Assert.Equal(@"C:\games\sandbox-settings.ini", reader.SettingsPath);
        Assert.Equal("GAPO Sandbox", reader.GameTitle);
    }

    [Fact]
    public void ReaderReceivesFramesInOrder()
    {
        var mapName = UniqueMapName();
        using var writer = TelemetryChannel.CreateForWriter(mapName);
        using var reader = TelemetryChannel.TryOpenForReader(mapName)!;
        for (var index = 0; index < 5; index++)
        {
            var record = new FrameRecord { FrameIndex = index, FrameMs = 10 + index, Revision = 3 };
            writer.WriteFrame(ref record);
        }

        var buffer = new FrameRecord[16];
        long next = 0;
        var count = reader.ReadNewFrames(ref next, buffer);

        Assert.Equal(5, count);
        Assert.Equal(5, next);
        Assert.Equal([0L, 1L, 2L, 3L, 4L], buffer.Take(count).Select(frame => frame.FrameIndex));
        Assert.Equal(14f, buffer[4].FrameMs);
        Assert.Equal(0, reader.ReadNewFrames(ref next, buffer));
    }

    [Fact]
    public void LappedReaderSkipsToFramesThatAreStillIntact()
    {
        var mapName = UniqueMapName();
        using var writer = TelemetryChannel.CreateForWriter(mapName);
        using var reader = TelemetryChannel.TryOpenForReader(mapName)!;
        const int Written = TelemetryChannel.Capacity + 500;
        for (var index = 0; index < Written; index++)
        {
            var record = new FrameRecord { FrameIndex = index };
            writer.WriteFrame(ref record);
        }

        var buffer = new FrameRecord[TelemetryChannel.Capacity];
        long next = 0;
        var count = reader.ReadNewFrames(ref next, buffer);

        Assert.Equal(Written, next);
        Assert.True(count < TelemetryChannel.Capacity);
        Assert.Equal(Written - 1, buffer[count - 1].FrameIndex);
        Assert.Equal(Written - count, buffer[0].FrameIndex);
    }

    [Fact]
    public void ReaderRestartsWhenTheGameRestarted()
    {
        var mapName = UniqueMapName();
        using var writer = TelemetryChannel.CreateForWriter(mapName);
        using var reader = TelemetryChannel.TryOpenForReader(mapName)!;
        var record = new FrameRecord { FrameIndex = 0 };
        writer.WriteFrame(ref record);

        long next = 900;
        var count = reader.ReadNewFrames(ref next, new FrameRecord[4]);

        Assert.Equal(0, count);
        Assert.Equal(1, next);
    }

    [Fact]
    public void CommandIsDeliveredOnceAndAcknowledged()
    {
        var mapName = UniqueMapName();
        using var game = TelemetryChannel.CreateForWriter(mapName);
        using var optimiser = TelemetryChannel.TryOpenForReader(mapName)!;
        var lastSequence = 0;

        optimiser.SendCommand(SandboxCommand.RestartBenchmarkPath, 5);

        Assert.True(game.TryTakeCommand(ref lastSequence, out var command, out var argument));
        Assert.Equal(SandboxCommand.RestartBenchmarkPath, command);
        Assert.Equal(5, argument);
        Assert.Equal(optimiser.CommandSequence, optimiser.CommandAcknowledged);
        Assert.False(game.TryTakeCommand(ref lastSequence, out _, out _));
    }

    [Fact]
    public void ToastIsDeliveredOnceAndTruncatedToItsCapacity()
    {
        var mapName = UniqueMapName();
        using var game = TelemetryChannel.CreateForWriter(mapName);
        using var optimiser = TelemetryChannel.TryOpenForReader(mapName)!;
        var lastSequence = 0;

        optimiser.ShowToast(new string('x', 500));

        Assert.True(game.TryTakeToast(ref lastSequence, out var text));
        Assert.Equal(127, text.Length);
        Assert.False(game.TryTakeToast(ref lastSequence, out _));
    }
}
