using Gapo.Core.Games;
using Xunit;

namespace Gapo.Core.Tests;

public sealed class SteamLibraryTests
{
    private const string SteamRoot = @"C:\Program Files (x86)\Steam";
    private const string SecondLibrary = @"D:\SteamLibrary";

    private const string LibraryFolders = """
        "libraryfolders"
        {
        	"0"
        	{
        		"path"		"C:\\Program Files (x86)\\Steam"
        		"label"		""
        		"contentid"		"1234567890123456789"
        		"totalsize"		"0"
        		"apps"
        		{
        			"228980"		"181743418"
        			"730"		"35719672311"
        		}
        	}
        	"1"
        	{
        		"path"		"D:\\SteamLibrary"
        		"apps"
        		{
        			"1091500"		"70203477114"
        		}
        	}
        }
        """;

    private const string OldLibraryFolders = """
        "LibraryFolders"
        {
        	"TimeNextStatsReport"		"1612345678"
        	"ContentStatsID"		"-1234567890"
        	"1"		"D:\\SteamLibrary"
        	"2"		"E:\\Games\\Steam"
        }
        """;

    [Fact]
    public void SectionsValuesEscapesAndCommentsAreParsed()
    {
        const string text = """
            // written by a game
            "Root"
            {
                "name"  "Half \"quoted\" name"
                "path"  "C:\\Games\\Thing"
                bare    value   [$WIN32]
                "empty" ""
                "Inner" { "deep" "1" }
            }
            """;

        Assert.True(KeyValuesParser.TryParse(text, out var root));
        var section = root.Child("root");
        Assert.NotNull(section);
        Assert.True(section.IsSection);
        Assert.Equal("Half \"quoted\" name", section.ValueOf("name"));
        Assert.Equal(@"C:\Games\Thing", section.ValueOf("PATH"));
        Assert.Equal("value", section.ValueOf("bare"));
        Assert.Equal(string.Empty, section.ValueOf("empty"));
        Assert.Equal("1", section.Child("Inner")?.ValueOf("deep"));
        Assert.Equal(5, section.Children.Count);
    }

    [Theory]
    [InlineData("\"a\" {")]
    [InlineData("\"a\" { \"b\" \"c\" } }")]
    [InlineData("\"a\"")]
    [InlineData("\"a\" \"unterminated")]
    [InlineData("{ \"a\" \"b\" }")]
    public void DamagedTextIsRejectedInsteadOfGuessedAt(string text) =>
        Assert.False(KeyValuesParser.TryParse(text, out _));

    [Fact]
    public void AbsurdNestingIsRejected()
    {
        var text = string.Concat(Enumerable.Repeat("\"a\" { ", 500)) + string.Concat(Enumerable.Repeat("} ", 500));

        Assert.False(KeyValuesParser.TryParse(text, out _));
    }

    [Fact]
    public void EmptyTextIsAnEmptyDocument()
    {
        Assert.True(KeyValuesParser.TryParse(string.Empty, out var root));
        Assert.Empty(root.Children);
    }

    [Fact]
    public void LibraryFoldersListEveryLibraryWithItsApps()
    {
        var folders = SteamLibraryFiles.ReadLibraryFolders(LibraryFolders);

        Assert.Equal(2, folders.Count);
        Assert.Equal(SteamRoot, folders[0].Path);
        Assert.Equal([228980, 730], folders[0].AppIds);
        Assert.Equal(SecondLibrary, folders[1].Path);
        Assert.Equal([1091500], folders[1].AppIds);
    }

    [Fact]
    public void TheOlderLibraryFoldersFormatIsUnderstood()
    {
        var folders = SteamLibraryFiles.ReadLibraryFolders(OldLibraryFolders);

        Assert.Equal([SecondLibrary, @"E:\Games\Steam"], folders.Select(folder => folder.Path));
        Assert.All(folders, folder => Assert.Empty(folder.AppIds));
    }

    [Fact]
    public void AnAppManifestBecomesAnInstalledGame()
    {
        var game = SteamLibraryFiles.ReadAppManifest(Manifest(730, "Counter-Strike 2", "Counter-Strike Global Offensive", stateFlags: 4), SteamRoot);

        Assert.NotNull(game);
        Assert.Equal(730, game.AppId);
        Assert.Equal("Counter-Strike 2", game.Name);
        Assert.Equal(@"C:\Program Files (x86)\Steam\steamapps\common\Counter-Strike Global Offensive", game.InstallDirectory);
        Assert.True(game.FullyInstalled);
        Assert.Equal(35719672311, game.SizeOnDiskBytes);
    }

    [Fact]
    public void AGameThatIsStillDownloadingIsNotFullyInstalled()
    {
        var game = SteamLibraryFiles.ReadAppManifest(Manifest(730, "Counter-Strike 2", "csgo", stateFlags: 1026), SteamRoot);

        Assert.NotNull(game);
        Assert.False(game.FullyInstalled);
    }

    [Theory]
    [InlineData(@"..\..\Windows")]
    [InlineData(@"C:\Windows")]
    [InlineData("a/b")]
    [InlineData("..")]
    [InlineData("")]
    public void AManifestWhoseFolderLeavesTheLibraryIsRejected(string installDirectory) =>
        Assert.Null(SteamLibraryFiles.ReadAppManifest(Manifest(10, "Trap", installDirectory.Replace(@"\", @"\\"), stateFlags: 4), SteamRoot));

    [Fact]
    public void AManifestWithoutAnAppIdIsRejected() =>
        Assert.Null(SteamLibraryFiles.ReadAppManifest("\"AppState\" { \"name\" \"x\" \"installdir\" \"x\" }", SteamRoot));

    [Fact]
    public void TheScanFindsGamesInEveryLibraryAndLeavesOutTheRedistributables()
    {
        var files = new FakeFiles();
        files.Add(SteamRoot + @"\steamapps\libraryfolders.vdf", LibraryFolders);
        files.Add(SteamRoot + @"\steamapps\appmanifest_730.acf", Manifest(730, "Counter-Strike 2", "Counter-Strike Global Offensive", stateFlags: 4));
        files.Add(SteamRoot + @"\steamapps\appmanifest_228980.acf", Manifest(228980, "Steamworks Common Redistributables", "Steamworks Shared", stateFlags: 4));
        files.Add(SecondLibrary + @"\steamapps\appmanifest_1091500.acf", Manifest(1091500, "Cyberpunk 2077", "Cyberpunk 2077", stateFlags: 4));

        var scan = SteamLibraryIndex.Scan(SteamRoot, files);

        Assert.Equal(["Counter-Strike 2", "Cyberpunk 2077"], scan.Games.Select(game => game.Name));
        Assert.Equal(@"D:\SteamLibrary\steamapps\common\Cyberpunk 2077", scan.Games[1].InstallDirectory);
        Assert.Equal([SteamRoot, SecondLibrary], scan.Libraries);
        Assert.Empty(scan.CouldNotBeRead);
    }

    [Fact]
    public void AMissingDriveAndADamagedManifestAreReportedNotHidden()
    {
        var files = new FakeFiles();
        files.Add(SteamRoot + @"\steamapps\libraryfolders.vdf", LibraryFolders);
        files.Add(SteamRoot + @"\steamapps\appmanifest_730.acf", "\"AppState\" { \"appid\" \"730\"");

        var scan = SteamLibraryIndex.Scan(SteamRoot, files);

        Assert.Empty(scan.Games);
        Assert.Equal([SteamRoot], scan.Libraries);
        Assert.Equal(2, scan.CouldNotBeRead.Count);
        Assert.Contains(scan.CouldNotBeRead, note => note.Contains(SecondLibrary, StringComparison.Ordinal));
        Assert.Contains(scan.CouldNotBeRead, note => note.Contains("appmanifest_730.acf", StringComparison.Ordinal));
    }

    [Fact]
    public void WithoutTheLibraryListOnlyTheMainFolderIsSearchedAndThatIsSaid()
    {
        var files = new FakeFiles();
        files.Add(SteamRoot + @"\steamapps\appmanifest_730.acf", Manifest(730, "Counter-Strike 2", "csgo", stateFlags: 4));

        var scan = SteamLibraryIndex.Scan(SteamRoot + @"\", files);

        Assert.Single(scan.Games);
        Assert.Single(scan.CouldNotBeRead);
    }

    [Fact]
    public void ALibraryAtTheRootOfADriveIsSearched()
    {
        var files = new FakeFiles();
        files.Add(SteamRoot + @"\steamapps\libraryfolders.vdf", "\"libraryfolders\" { \"0\" { \"path\" \"E:\\\\\" } }");
        files.Add(@"E:\steamapps\appmanifest_10.acf", Manifest(10, "Old Game", "Old Game", stateFlags: 4));

        var scan = SteamLibraryIndex.Scan(SteamRoot, files);

        Assert.Equal(@"E:\steamapps\common\Old Game", Assert.Single(scan.Games).InstallDirectory);
    }

    private static string Manifest(int appId, string name, string installDirectory, int stateFlags) =>
        $$"""
        "AppState"
        {
        	"appid"		"{{appId}}"
        	"Universe"		"1"
        	"name"		"{{name}}"
        	"StateFlags"		"{{stateFlags}}"
        	"installdir"		"{{installDirectory}}"
        	"SizeOnDisk"		"35719672311"
        	"buildid"		"15000000"
        	"InstalledDepots"
        	{
        		"731"
        		{
        			"manifest"		"1234567890"
        			"size"		"35719672311"
        		}
        	}
        }
        """;

    internal sealed class FakeFiles : IGameFileSource
    {
        private readonly Dictionary<string, string> texts = new(StringComparer.OrdinalIgnoreCase);

        public void Add(string path, string text) => texts[path] = text;

        public string? ReadText(string path) => texts.GetValueOrDefault(path);

        public IReadOnlyList<string> ListFiles(string directory, string pattern)
        {
            var prefix = pattern[..pattern.IndexOf('*', StringComparison.Ordinal)];
            var suffix = pattern[(pattern.IndexOf('*', StringComparison.Ordinal) + 1)..];
            return texts.Keys
                .Where(path => string.Equals(Path.GetDirectoryName(path), directory, StringComparison.OrdinalIgnoreCase))
                .Where(path => Path.GetFileName(path).StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public IReadOnlyList<string> ListDirectories(string directory) =>
            texts.Keys
                .Where(path => path.StartsWith(directory + @"\", StringComparison.OrdinalIgnoreCase))
                .Select(path => path[(directory.Length + 1)..].Split('\\')[0])
                .Where(first => texts.Keys.Any(path => path.StartsWith(directory + @"\" + first + @"\", StringComparison.OrdinalIgnoreCase)))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(first => directory + @"\" + first)
                .ToList();

        public bool DirectoryExists(string path) =>
            texts.Keys.Any(file => file.StartsWith(path + @"\", StringComparison.OrdinalIgnoreCase));
    }
}
