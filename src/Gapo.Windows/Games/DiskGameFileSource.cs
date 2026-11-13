using Gapo.Core.Games;

namespace Gapo.Windows.Games;

public sealed class DiskGameFileSource : IGameFileSource
{
    private const long LargestFileReadBytes = 4 * 1024 * 1024;

    public string? ReadText(string path)
    {
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.Length > LargestFileReadBytes)
            {
                return null;
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException or System.Security.SecurityException)
        {
            return null;
        }
    }

    public IReadOnlyList<string> ListFiles(string directory, string pattern)
    {
        try
        {
            return Directory.GetFiles(directory, pattern, SearchOption.TopDirectoryOnly);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public IReadOnlyList<string> ListDirectories(string directory)
    {
        try
        {
            return Directory.GetDirectories(directory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public bool DirectoryExists(string path) => Directory.Exists(path);
}
