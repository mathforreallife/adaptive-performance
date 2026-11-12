namespace Gapo.SourceRules;

public sealed class SourceTree
{
    private static readonly string[] AlwaysExcludedDirectoryNames = [".git", ".vs", "bin", "obj", "artifacts", "TestResults", "node_modules"];

    private static readonly string[] GeneratedFileSuffixes = [".g.cs", ".g.i.cs", ".designer.cs", ".generated.cs", ".assets.json", ".lock.json"];

    private readonly string root;
    private readonly IReadOnlyList<string> excludedRelativeDirectories;

    public SourceTree(string root, IEnumerable<string> excludedRelativeDirectories)
    {
        this.root = Path.GetFullPath(root);
        this.excludedRelativeDirectories = excludedRelativeDirectories.Select(Normalise).ToList();
    }

    public IEnumerable<string> EnumerateFiles() => EnumerateFiles(root);

    public string RelativePath(string fullPath) => Normalise(Path.GetRelativePath(root, fullPath));

    private IEnumerable<string> EnumerateFiles(string directory)
    {
        foreach (var file in Directory.EnumerateFiles(directory).Order(StringComparer.OrdinalIgnoreCase))
        {
            if (!IsGenerated(file))
            {
                yield return file;
            }
        }

        foreach (var child in Directory.EnumerateDirectories(directory).Order(StringComparer.OrdinalIgnoreCase))
        {
            if (IsExcluded(child))
            {
                continue;
            }

            foreach (var file in EnumerateFiles(child))
            {
                yield return file;
            }
        }
    }

    private bool IsExcluded(string directory)
    {
        var name = Path.GetFileName(directory);
        if (AlwaysExcludedDirectoryNames.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        var relative = RelativePath(directory);
        return excludedRelativeDirectories.Contains(relative, StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsGenerated(string file) =>
        GeneratedFileSuffixes.Any(suffix => file.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));

    private static string Normalise(string path) => path.Replace('\\', '/').Trim('/');
}
