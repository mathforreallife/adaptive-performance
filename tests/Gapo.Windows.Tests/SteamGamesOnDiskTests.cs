using Gapo.Core.Games;
using Gapo.Windows.Games;
using Xunit;

namespace Gapo.Windows.Tests;

public sealed class SteamGamesOnDiskTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "gapo-steam-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ASteamFolderOnDiskIsIndexed()
    {
        var main = Path.Combine(root, "Steam");
        var second = Path.Combine(root, "Second Library");
        Directory.CreateDirectory(Path.Combine(main, "steamapps"));
        Directory.CreateDirectory(Path.Combine(second, "steamapps"));
        var escapedSecond = second.Replace("\\", "\\\\", StringComparison.Ordinal);
        File.WriteAllText(Path.Combine(main, "steamapps", "libraryfolders.vdf"), "\"libraryfolders\"\n{\n\t\"0\"\n\t{\n\t\t\"path\"\t\t\"" + escapedSecond + "\"\n\t}\n}\n");
        File.WriteAllText(Path.Combine(main, "steamapps", "appmanifest_10.acf"), Manifest(10, "First Game", "First"));
        File.WriteAllText(Path.Combine(second, "steamapps", "appmanifest_20.acf"), Manifest(20, "Second Game", "Second"));
        File.WriteAllText(Path.Combine(second, "steamapps", "appmanifest_30.acf"), "not a manifest {");
        File.WriteAllText(Path.Combine(second, "steamapps", "unrelated.txt"), "ignored");

        var scan = SteamLibraryIndex.Scan(main, new DiskGameFileSource());

        Assert.Equal(["First Game", "Second Game"], scan.Games.Select(game => game.Name));
        Assert.Equal(Path.Combine(second, "steamapps", "common", "Second"), scan.Games[1].InstallDirectory);
        Assert.Equal([main, second], scan.Libraries);
        Assert.Contains("appmanifest_30.acf", Assert.Single(scan.CouldNotBeRead), StringComparison.Ordinal);
    }

    [Fact]
    public void AFileThatIsMissingReadsAsNothing()
    {
        var files = new DiskGameFileSource();

        Assert.Null(files.ReadText(Path.Combine(root, "missing.vdf")));
        Assert.Empty(files.ListFiles(Path.Combine(root, "missing"), "*.acf"));
        Assert.False(files.DirectoryExists(Path.Combine(root, "missing")));
    }

    [Fact]
    public void LookingForSteamNeverThrowsAndReturnsAnExistingFolderOrNothing()
    {
        var found = SteamLocator.FindSteamRoot();

        Assert.True(found is null || Directory.Exists(found));
    }

    private static string Manifest(int appId, string name, string installDirectory) =>
        "\"AppState\"\n{\n\t\"appid\"\t\t\"" + appId + "\"\n\t\"name\"\t\t\"" + name + "\"\n\t\"StateFlags\"\t\t\"4\"\n\t\"installdir\"\t\t\"" + installDirectory + "\"\n\t\"SizeOnDisk\"\t\t\"1000\"\n}\n";
}
