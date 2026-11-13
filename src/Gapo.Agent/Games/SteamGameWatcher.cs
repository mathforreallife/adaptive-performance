using Gapo.Core.Games;
using Gapo.Core.Telemetry;
using Gapo.Windows.Games;
using Gapo.Windows.Telemetry;

namespace Gapo.Agent.Games;

public sealed class SteamGameWatcher : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RescanInterval = TimeSpan.FromMinutes(5);

    private readonly string? steamRoot;
    private readonly ProcessSnapshotReader reader = new();
    private readonly ProcessSnapshot snapshot = new();
    private readonly RunningGameFinder finder = new();
    private IReadOnlyList<InstalledSteamGame> games = [];
    private TimeSpan nextPoll = TimeSpan.Zero;
    private TimeSpan nextScan = TimeSpan.Zero;

    public SteamGameWatcher(string? steamRootOverride)
    {
        steamRoot = steamRootOverride ?? SteamLocator.FindSteamRoot();
    }

    public bool HasSteam => steamRoot is not null;

    public int InstalledGames => games.Count;

    public RunningGame? Current { get; private set; }

    public bool Poll(TimeSpan now)
    {
        if (steamRoot is null || now < nextPoll)
        {
            return false;
        }

        nextPoll = now + PollInterval;
        if (now >= nextScan)
        {
            games = SteamLibraryIndex.Scan(steamRoot, new DiskGameFileSource()).Games;
            nextScan = now + RescanInterval;
        }

        if (games.Count == 0)
        {
            return Change(null);
        }

        reader.Capture(snapshot, now, detailedProcessId: 0);
        var running = finder.Find(snapshot, games);
        return Change(running.Count > 0 ? running[0] : null);
    }

    public void Forget() => Change(null);

    public void Dispose() => reader.Dispose();

    private bool Change(RunningGame? found)
    {
        var changed = found?.Game.AppId != Current?.Game.AppId || found?.ProcessId != Current?.ProcessId;
        Current = found;
        return changed;
    }
}
