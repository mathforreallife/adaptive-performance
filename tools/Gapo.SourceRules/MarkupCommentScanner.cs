namespace Gapo.SourceRules;

public sealed class MarkupCommentScanner : ISourceScanner
{
    public const string Rule = "markup-comment";

    private const string CommentOpening = "<!--";

    private static readonly string[] MarkupExtensions =
    [
        ".xaml", ".csproj", ".props", ".targets", ".xml", ".manifest", ".slnx", ".resw", ".config", ".appxmanifest", ".wxs",
    ];

    public bool Handles(string path) =>
        MarkupExtensions.Any(extension => path.EndsWith(extension, StringComparison.OrdinalIgnoreCase));

    public IEnumerable<Finding> Scan(string path, string text)
    {
        var lineNumber = 0;
        foreach (var line in text.Split('\n'))
        {
            lineNumber++;
            var opening = line.IndexOf(CommentOpening, StringComparison.Ordinal);
            if (opening >= 0)
            {
                yield return Finding.At(path, lineNumber, Rule, line[opening..]);
            }
        }
    }
}
