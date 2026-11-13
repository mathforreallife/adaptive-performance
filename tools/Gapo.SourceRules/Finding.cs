namespace Gapo.SourceRules;

public readonly record struct Finding(string Path, int Line, string Rule, string Excerpt)
{
    private const int MaximumExcerptLength = 80;

    public static Finding At(string path, int line, string rule, string offendingText)
    {
        var firstLine = offendingText.AsSpan().Trim();
        var lineBreak = firstLine.IndexOfAny('\r', '\n');
        if (lineBreak >= 0)
        {
            firstLine = firstLine[..lineBreak];
        }

        if (firstLine.Length > MaximumExcerptLength)
        {
            firstLine = firstLine[..MaximumExcerptLength];
        }

        return new Finding(path, line, rule, firstLine.ToString());
    }

    public override string ToString() => $"{Path}({Line}): {Rule}: {Excerpt}";
}
