using Gapo.Sandbox.Contract;
using Gapo.Windows.Processes;

namespace Gapo.Agent.EngineHook;

public sealed class SandboxLink : IDisposable
{
    private const int ReadBufferFrames = 1024;
    private const int BacklogFramesOnConnect = 600;

    private readonly string mapName;
    private readonly FrameRecord[] readBuffer = new FrameRecord[ReadBufferFrames];
    private TelemetryChannel? channel;
    private ProcessLiveness? game;
    private long nextFrameIndex;

    public SandboxLink(string mapName)
    {
        this.mapName = mapName;
    }

    public bool IsConnected => channel is not null;

    public bool HasEverConnected { get; private set; }

    public int GameProcessId => game?.ProcessId ?? 0;

    public long FramesRead { get; private set; }

    public FrameRecord LatestFrame { get; private set; }

    public void Reconcile()
    {
        if (channel is null)
        {
            TryConnect();
        }
        else if (game is null || !game.IsRunning || channel.State == SandboxState.Exiting)
        {
            Disconnect();
        }
    }

    public delegate void FrameObserver(in FrameRecord frame);

    public int ReadNewFrames(FrameObserver? observer = null)
    {
        if (channel is null)
        {
            return 0;
        }

        var total = 0;
        int count;
        do
        {
            count = channel.ReadNewFrames(ref nextFrameIndex, readBuffer);
            for (var index = 0; observer is not null && index < count; index++)
            {
                observer(in readBuffer[index]);
            }

            if (count > 0)
            {
                LatestFrame = readBuffer[count - 1];
                total += count;
            }
        }
        while (count == readBuffer.Length);

        FramesRead += total;
        return total;
    }

    public bool GameIsRunningNormally => channel is not null && channel.State == SandboxState.Running;

    public int AppliedSettingsRevision => channel?.AppliedRevision ?? 0;

    public bool LastCommandWasAcknowledged => channel is not null && channel.CommandAcknowledged == channel.CommandSequence;

    public bool TrySendCommand(SandboxCommand command, int argument)
    {
        if (channel is null)
        {
            return false;
        }

        channel.SendCommand(command, argument);
        return true;
    }

    public void SkipBacklog()
    {
        if (channel is not null)
        {
            nextFrameIndex = channel.FramesWritten;
        }
    }

    public void Dispose() => Disconnect();

    private void TryConnect()
    {
        var candidate = TelemetryChannel.TryOpenForReader(mapName);
        if (candidate is null)
        {
            return;
        }

        var watchedGame = candidate.Pid == 0 ? null : ProcessLiveness.TryWatch(candidate.Pid);
        if (watchedGame is null || !watchedGame.IsRunning || candidate.State == SandboxState.Exiting)
        {
            watchedGame?.Dispose();
            candidate.Dispose();
            return;
        }

        channel = candidate;
        game = watchedGame;
        nextFrameIndex = Math.Max(0, candidate.FramesWritten - BacklogFramesOnConnect);
        HasEverConnected = true;
    }

    private void Disconnect()
    {
        game?.Dispose();
        channel?.Dispose();
        game = null;
        channel = null;
    }
}
