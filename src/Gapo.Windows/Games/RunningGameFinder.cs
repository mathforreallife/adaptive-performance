using Gapo.Core.Games;
using Gapo.Core.Telemetry;
using Gapo.Windows.Processes;

namespace Gapo.Windows.Games;

public sealed class RunningGameFinder
{
    private const int LongestPathCharacters = 32_768;
    private const int FirstUserProcessId = 8;

    private readonly Dictionary<(int ProcessId, long CreateTimeTicks), string?> imagePaths = [];
    private readonly HashSet<(int ProcessId, long CreateTimeTicks)> seenThisPass = [];
    private readonly List<RunningProcess> candidates = [];
    private readonly char[] pathBuffer = new char[LongestPathCharacters];

    public int PathsResolved { get; private set; }

    public IReadOnlyList<RunningGame> Find(ProcessSnapshot snapshot, IReadOnlyList<InstalledSteamGame> games)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(games);
        candidates.Clear();
        seenThisPass.Clear();
        foreach (var process in snapshot.Processes)
        {
            if (process.ProcessId < FirstUserProcessId)
            {
                continue;
            }

            var key = (process.ProcessId, process.CreateTimeTicks);
            seenThisPass.Add(key);
            if (!imagePaths.TryGetValue(key, out var path))
            {
                path = ReadImagePath(process.ProcessId);
                imagePaths[key] = path;
                PathsResolved++;
            }

            if (path is not null)
            {
                candidates.Add(new RunningProcess(process.ProcessId, path, process.WorkingSetBytes));
            }
        }

        if (imagePaths.Count > seenThisPass.Count)
        {
            foreach (var key in imagePaths.Keys.Where(key => !seenThisPass.Contains(key)).ToList())
            {
                imagePaths.Remove(key);
            }
        }

        return RunningGameMatcher.FindRunningGames(games, candidates);
    }

    private unsafe string? ReadImagePath(int processId)
    {
        var handle = ProcessNative.OpenProcess(ProcessNative.QueryLimitedInformation, false, processId);
        if (handle == 0)
        {
            return null;
        }

        try
        {
            var size = (uint)pathBuffer.Length;
            fixed (char* buffer = pathBuffer)
            {
                return ProcessNative.QueryFullProcessImageName(handle, 0, buffer, ref size) ? new string(buffer, 0, (int)size) : null;
            }
        }
        finally
        {
            ProcessNative.CloseHandle(handle);
        }
    }
}
