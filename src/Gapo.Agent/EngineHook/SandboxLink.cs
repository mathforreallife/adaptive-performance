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

    public int ReadNewFrames()
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
