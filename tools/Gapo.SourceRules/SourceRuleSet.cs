namespace Gapo.SourceRules;

public sealed class SourceRuleSet
{
    private readonly IReadOnlyList<ISourceScanner> scanners;

    public SourceRuleSet(IReadOnlyList<ISourceScanner> scanners)
    {
        this.scanners = scanners;
    }

    public static SourceRuleSet Default { get; } = new(
    [
        new CSharpCommentScanner(),
        new MarkupCommentScanner(),
        new ScriptCommentScanner(),
        new JsonStrictnessScanner(),
    ]);

    public SourceCheckResult Check(SourceTree tree)
    {
        var findings = new List<Finding>();
        var checkedFileCount = 0;
        foreach (var file in tree.EnumerateFiles())
        {
            var applicable = scanners.Where(scanner => scanner.Handles(file)).ToList();
            if (applicable.Count == 0)
            {
                continue;
            }

            checkedFileCount++;
            var relativePath = tree.RelativePath(file);
            var text = File.ReadAllText(file);
            foreach (var scanner in applicable)
            {
                findings.AddRange(scanner.Scan(relativePath, text));
            }
        }

        return new SourceCheckResult(checkedFileCount, findings);
    }
}

public sealed record SourceCheckResult(int CheckedFileCount, IReadOnlyList<Finding> Findings)
{
    public bool IsClean => Findings.Count == 0;
}
