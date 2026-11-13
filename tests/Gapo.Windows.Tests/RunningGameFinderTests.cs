using Gapo.Core.Games;
using Gapo.Core.Telemetry;
using Gapo.Windows.Games;
using Gapo.Windows.Telemetry;
using Xunit;

namespace Gapo.Windows.Tests;

public sealed class RunningGameFinderTests
{
    private const int FewestProcessesOnAnyWindowsPc = 10;
    private const int MostProcessesThatStartWithinASecond = 20;

    [Fact]
    public void AProcessRunningFromAGameFolderIsFoundAndPathsAreResolvedOnlyOnce()
    {
        var folder = Path.GetDirectoryName(Environment.ProcessPath!)!;
        var game = new InstalledSteamGame(1, "This test host", folder, Path.GetPathRoot(folder)!, FullyInstalled: true, SizeOnDiskBytes: 0);
        var elsewhere = new InstalledSteamGame(2, "Not running", Path.Combine(Path.GetTempPath(), "gapo-no-such-game"), Path.GetTempPath(), FullyInstalled: true, SizeOnDiskBytes: 0);
        using var reader = new ProcessSnapshotReader();
        var snapshot = new ProcessSnapshot();
        var finder = new RunningGameFinder();

        reader.Capture(snapshot, TimeSpan.Zero, detailedProcessId: 0);
        var first = finder.Find(snapshot, [game, elsewhere]);
        var resolvedAfterFirstPass = finder.PathsResolved;
        reader.Capture(snapshot, TimeSpan.FromSeconds(1), detailedProcessId: 0);
        var second = finder.Find(snapshot, [game, elsewhere]);

        var found = Assert.Single(first);
        Assert.Equal(game, found.Game);
        Assert.StartsWith(folder, found.ImagePath, StringComparison.OrdinalIgnoreCase);
        Assert.Single(second);
        Assert.True(resolvedAfterFirstPass > FewestProcessesOnAnyWindowsPc);
        Assert.InRange(finder.PathsResolved - resolvedAfterFirstPass, 0, MostProcessesThatStartWithinASecond);
    }
}
