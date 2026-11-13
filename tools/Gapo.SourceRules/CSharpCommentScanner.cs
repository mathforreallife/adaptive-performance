namespace Gapo.SourceRules;

public sealed class CSharpCommentScanner : ISourceScanner
{
    public const string Rule = "csharp-comment";

    public bool Handles(string path) => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);

    public IEnumerable<Finding> Scan(string path, string text)
    {
        var tree = CSharpComments.Parse(text);
        foreach (var comment in CSharpComments.FindIn(tree))
        {
            var line = tree.GetLineSpan(comment.Span).StartLinePosition.Line + 1;
            yield return Finding.At(path, line, Rule, comment.ToString());
        }
    }
}
