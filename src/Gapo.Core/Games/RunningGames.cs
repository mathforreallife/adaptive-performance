namespace Gapo.Core.Games;

public sealed record RunningProcess(int ProcessId, string ImagePath, long WorkingSetBytes);

public sealed record RunningGame(InstalledSteamGame Game, int ProcessId, string ImagePath, long WorkingSetBytes);

public static class RunningGameMatcher
{
    public static IReadOnlyList<RunningGame> FindRunningGames(IReadOnlyList<InstalledSteamGame> games, IEnumerable<RunningProcess> processes)
    {
        ArgumentNullException.ThrowIfNull(games);
        ArgumentNullException.ThrowIfNull(processes);
        var largestPerGame = new Dictionary<int, RunningGame>();
        foreach (var process in processes)
        {
            if (GameContaining(games, process.ImagePath) is not { } game)
            {
                continue;
            }

            if (!largestPerGame.TryGetValue(game.AppId, out var current) || process.WorkingSetBytes > current.WorkingSetBytes)
            {
                largestPerGame[game.AppId] = new RunningGame(game, process.ProcessId, process.ImagePath, process.WorkingSetBytes);
            }
        }

        return largestPerGame.Values.OrderByDescending(running => running.WorkingSetBytes).ToList();
    }

    private static InstalledSteamGame? GameContaining(IReadOnlyList<InstalledSteamGame> games, string imagePath)
    {
        InstalledSteamGame? best = null;
        foreach (var game in games)
        {
            var folder = game.InstallDirectory;
            if (imagePath.Length > folder.Length + 1
                && imagePath.StartsWith(folder, StringComparison.OrdinalIgnoreCase)
                && imagePath[folder.Length] is '\\' or '/'
                && (best is null || folder.Length > best.InstallDirectory.Length))
            {
                best = game;
            }
        }

        return best;
    }
}
