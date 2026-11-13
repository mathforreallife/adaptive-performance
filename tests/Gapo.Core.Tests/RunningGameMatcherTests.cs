using Gapo.Core.Games;
using Xunit;

namespace Gapo.Core.Tests;

public sealed class RunningGameMatcherTests
{
    private static readonly InstalledSteamGame Shooter = Game(730, "Shooter", @"D:\Lib\steamapps\common\Shooter");
    private static readonly InstalledSteamGame ShooterTwo = Game(731, "Shooter 2", @"D:\Lib\steamapps\common\Shooter 2");
    private static readonly InstalledSteamGame Racer = Game(900, "Racer", @"C:\Steam\steamapps\common\Racer");

    [Fact]
    public void AProcessInsideAGameFolderIsThatGame()
    {
        var running = RunningGameMatcher.FindRunningGames([Shooter, Racer], [new RunningProcess(41, @"d:\lib\steamapps\common\shooter\bin\win64\game.exe", 900)]);

        var found = Assert.Single(running);
        Assert.Equal(Shooter, found.Game);
        Assert.Equal(41, found.ProcessId);
    }

    [Fact]
    public void TheLargestProcessOfAGameIsTheGameNotItsCrashHandler()
    {
        var running = RunningGameMatcher.FindRunningGames(
            [Shooter],
            [
                new RunningProcess(10, @"D:\Lib\steamapps\common\Shooter\CrashHandler64.exe", 12),
                new RunningProcess(11, @"D:\Lib\steamapps\common\Shooter\Shooter.exe", 4_000),
                new RunningProcess(12, @"D:\Lib\steamapps\common\Shooter\launcher.exe", 90),
            ]);

        Assert.Equal(11, Assert.Single(running).ProcessId);
    }

    [Fact]
    public void AFolderNameThatIsOnlyAPrefixDoesNotMatch()
    {
        var running = RunningGameMatcher.FindRunningGames([Shooter, ShooterTwo], [new RunningProcess(5, @"D:\Lib\steamapps\common\Shooter 2\s2.exe", 1)]);

        Assert.Equal(ShooterTwo, Assert.Single(running).Game);
    }

    [Fact]
    public void ProgramsOutsideEveryLibraryAreNotGames()
    {
        var running = RunningGameMatcher.FindRunningGames(
            [Shooter, Racer],
            [
                new RunningProcess(1, @"C:\Windows\explorer.exe", 5_000),
                new RunningProcess(2, @"D:\Lib\steamapps\common\Shooter", 5_000),
                new RunningProcess(3, @"C:\Steam\steam.exe", 5_000),
            ]);

        Assert.Empty(running);
    }

    [Fact]
    public void TwoGamesAtOnceAreListedLargestFirst()
    {
        var running = RunningGameMatcher.FindRunningGames(
            [Shooter, Racer],
            [
                new RunningProcess(1, @"D:\Lib\steamapps\common\Shooter\Shooter.exe", 100),
                new RunningProcess(2, @"C:\Steam\steamapps\common\Racer\Racer.exe", 700),
            ]);

        Assert.Equal([Racer, Shooter], running.Select(found => found.Game));
    }

    private static InstalledSteamGame Game(int appId, string name, string folder) =>
        new(appId, name, folder, Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(folder)))!, FullyInstalled: true, SizeOnDiskBytes: 0);
}
