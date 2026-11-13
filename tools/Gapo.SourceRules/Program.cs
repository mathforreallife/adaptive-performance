using Gapo.SourceRules;

const int Success = 0;
const int ViolationsFound = 1;
const int UsageError = 2;
const string ExcludeOption = "--exclude";

if (args.Length < 2)
{
    return PrintUsage();
}

return args[0] switch
{
    "check" => Check(args[1], ReadExclusions(args.AsSpan(2))),
    "strip" => Strip(args.AsSpan(1)),
    _ => PrintUsage(),
};

int Check(string root, IReadOnlyList<string> exclusions)
{
    if (!Directory.Exists(root))
    {
        Console.Error.WriteLine($"Directory not found: {root}");
        return UsageError;
    }

    var result = SourceRuleSet.Default.Check(new SourceTree(root, exclusions));
    foreach (var finding in result.Findings)
    {
        Console.WriteLine(finding);
    }

    Console.WriteLine(result.IsClean
        ? $"Source rules passed: {result.CheckedFileCount} files, no comments."
        : $"Source rules failed: {result.Findings.Count} findings in {result.CheckedFileCount} files.");
    return result.IsClean ? Success : ViolationsFound;
}

int Strip(ReadOnlySpan<string> paths)
{
    foreach (var path in paths)
    {
        foreach (var file in ExpandToCSharpFiles(path))
        {
            var original = File.ReadAllText(file);
            var stripped = CSharpCommentStripper.Strip(original);
            if (!string.Equals(original, stripped, StringComparison.Ordinal))
            {
                File.WriteAllText(file, stripped);
                Console.WriteLine($"Stripped {file}");
            }
        }
    }

    return Success;
}

IEnumerable<string> ExpandToCSharpFiles(string path) =>
    Directory.Exists(path)
        ? new SourceTree(path, []).EnumerateFiles().Where(file => file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
        : [path];

IReadOnlyList<string> ReadExclusions(ReadOnlySpan<string> options)
{
    var exclusions = new List<string>();
    for (var index = 0; index + 1 < options.Length; index += 2)
    {
        if (options[index] == ExcludeOption)
        {
            exclusions.Add(options[index + 1]);
        }
    }

    return exclusions;
}

int PrintUsage()
{
    Console.Error.WriteLine("Usage:");
    Console.Error.WriteLine("  gapo-source-rules check <root> [--exclude <relative-directory>]...");
    Console.Error.WriteLine("  gapo-source-rules strip <file-or-directory>...");
    return UsageError;
}
