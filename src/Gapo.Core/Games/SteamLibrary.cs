using System.Globalization;

namespace Gapo.Core.Games;

public sealed record SteamLibraryFolder(string Path, IReadOnlyList<int> AppIds);

public sealed record InstalledSteamGame(int AppId, string Name, string InstallDirectory, string LibraryPath, bool FullyInstalled, long SizeOnDiskBytes);

public sealed record SteamLibraryScan(IReadOnlyList<InstalledSteamGame> Games, IReadOnlyList<string> Libraries, IReadOnlyList<string> CouldNotBeRead);

public interface IGameFileSource
{
    string? ReadText(string path);

    IReadOnlyList<string> ListFiles(string directory, string pattern);

    IReadOnlyList<string> ListDirectories(string directory);

    bool DirectoryExists(string path);
}

public static class SteamLibraryFiles
{
    public const string AppsDirectoryName = "steamapps";
    public const string CommonDirectoryName = "common";
    public const string LibraryFoldersFileName = "libraryfolders.vdf";
    public const string ConfigDirectoryName = "config";
    public const string ManifestPattern = "appmanifest_*.acf";

    private const string LibraryFoldersSection = "libraryfolders";
    private const string AppStateSection = "AppState";
    private const string PathKey = "path";
    private const string AppsKey = "apps";
    private const int FullyInstalledFlag = 4;

    public static IReadOnlyList<SteamLibraryFolder> ReadLibraryFolders(string libraryFoldersText)
    {
        if (!KeyValuesParser.TryParse(libraryFoldersText, out var root) || root.Child(LibraryFoldersSection) is not { } section)
        {
            return [];
        }

        var folders = new List<SteamLibraryFolder>();
        foreach (var entry in section.Children.Where(entry => IsWholeNumber(entry.Name)))
        {
            var path = entry.IsSection ? entry.ValueOf(PathKey) : entry.Value;
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            var appIds = entry.Child(AppsKey)?.Children
                .Select(app => int.TryParse(app.Name, NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : 0)
                .Where(id => id > 0)
                .ToList() ?? [];
            folders.Add(new SteamLibraryFolder(path, appIds));
        }

        return folders;
    }

    public static InstalledSteamGame? ReadAppManifest(string manifestText, string libraryPath)
    {
        if (!KeyValuesParser.TryParse(manifestText, out var root) || root.Child(AppStateSection) is not { } state)
        {
            return null;
        }

        var name = state.ValueOf("name");
        var installDirectory = state.ValueOf("installdir");
        if (!int.TryParse(state.ValueOf("appid"), NumberStyles.None, CultureInfo.InvariantCulture, out var appId) || appId <= 0
            || string.IsNullOrWhiteSpace(name) || !IsPlainDirectoryName(installDirectory))
        {
            return null;
        }

        _ = int.TryParse(state.ValueOf("StateFlags"), NumberStyles.None, CultureInfo.InvariantCulture, out var flags);
        _ = long.TryParse(state.ValueOf("SizeOnDisk"), NumberStyles.None, CultureInfo.InvariantCulture, out var size);
        return new InstalledSteamGame(
            appId,
            name,
            Path.Combine(libraryPath, AppsDirectoryName, CommonDirectoryName, installDirectory!),
            libraryPath,
            (flags & FullyInstalledFlag) != 0,
            size);
    }

    private static bool IsWholeNumber(string text) => text.Length > 0 && text.All(char.IsAsciiDigit);

    private static bool IsPlainDirectoryName(string? name) =>
        !string.IsNullOrWhiteSpace(name)
        && name is not ("." or "..")
        && name.IndexOfAny(['/', '\\', ':', '*', '?', '"', '<', '>', '|']) < 0
        && !name.Any(char.IsControl);
}

public static class SteamLibraryIndex
{
    private static readonly HashSet<int> AppsThatAreNotGames = [228980];

    public static SteamLibraryScan Scan(string steamRoot, IGameFileSource files)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(steamRoot);
        ArgumentNullException.ThrowIfNull(files);
        var couldNotBeRead = new List<string>();
        var libraries = new List<string> { Normalise(steamRoot) };
        var folderFile = FirstReadable(files,
            Path.Combine(steamRoot, SteamLibraryFiles.AppsDirectoryName, SteamLibraryFiles.LibraryFoldersFileName),
            Path.Combine(steamRoot, SteamLibraryFiles.ConfigDirectoryName, SteamLibraryFiles.LibraryFoldersFileName));
        if (folderFile is null)
        {
            couldNotBeRead.Add("Steam's list of library folders could not be read; only the main Steam folder was searched.");
        }
        else
        {
            foreach (var folder in SteamLibraryFiles.ReadLibraryFolders(folderFile))
            {
                var path = Normalise(folder.Path);
                if (!libraries.Contains(path, StringComparer.OrdinalIgnoreCase))
                {
                    libraries.Add(path);
                }
            }
        }

        var games = new Dictionary<int, InstalledSteamGame>();
        var searched = new List<string>();
        foreach (var library in libraries)
        {
            var apps = Path.Combine(library, SteamLibraryFiles.AppsDirectoryName);
            if (!files.DirectoryExists(apps))
            {
                couldNotBeRead.Add($"The Steam library at {library} is not reachable (a drive that is not connected?).");
                continue;
            }

            searched.Add(library);
            foreach (var manifestPath in files.ListFiles(apps, SteamLibraryFiles.ManifestPattern))
            {
                var game = files.ReadText(manifestPath) is { } text ? SteamLibraryFiles.ReadAppManifest(text, library) : null;
                if (game is null)
                {
                    couldNotBeRead.Add($"{Path.GetFileName(manifestPath)} in {library} could not be read.");
                }
                else if (!AppsThatAreNotGames.Contains(game.AppId))
                {
                    games.TryAdd(game.AppId, game);
                }
            }
        }

        return new SteamLibraryScan(
            games.Values.OrderBy(game => game.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            searched,
            couldNotBeRead);
    }

    private static string? FirstReadable(IGameFileSource files, params string[] paths) =>
        paths.Select(files.ReadText).FirstOrDefault(text => text is not null);

    private static string Normalise(string path)
    {
        var trimmed = path.Trim().Replace('/', Path.DirectorySeparatorChar).TrimEnd(Path.DirectorySeparatorChar);
        return trimmed.EndsWith(Path.VolumeSeparatorChar) ? trimmed + Path.DirectorySeparatorChar : trimmed;
    }
}
