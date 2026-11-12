namespace Gapo.SourceRules;

public sealed class ScriptCommentScanner : ISourceScanner
{
    public const string Rule = "script-comment";

    private const char CommentMarker = '#';
    private const char BlockCommentOpener = '<';

    private static readonly string[] ScriptExtensions = [".yml", ".yaml", ".ps1", ".psm1", ".psd1"];

    public bool Handles(string path) =>
        ScriptExtensions.Any(extension => path.EndsWith(extension, StringComparison.OrdinalIgnoreCase));

    public IEnumerable<Finding> Scan(string path, string text)
    {
        var lineNumber = 0;
        foreach (var line in text.Split('\n'))
        {
            lineNumber++;
            var commentStart = FindCommentStart(line);
            if (commentStart >= 0)
            {
                yield return Finding.At(path, lineNumber, Rule, line[commentStart..]);
            }
        }
    }

    private static int FindCommentStart(string line)
    {
        var openQuote = '\0';
        for (var index = 0; index < line.Length; index++)
        {
            var character = line[index];
            if (openQuote != '\0')
            {
                if (character == openQuote)
                {
                    openQuote = '\0';
                }

                continue;
            }

            if (character is '"' or '\'')
            {
                if (StartsQuotedText(line, index))
                {
                    openQuote = character;
                }

                continue;
            }

            if (character == CommentMarker && StartsComment(line, index))
            {
                return index;
            }
        }

        return -1;
    }

    private static bool StartsQuotedText(string line, int index) =>
        index == 0 || !char.IsLetterOrDigit(line[index - 1]);

    private static bool StartsComment(string line, int index) =>
        index == 0 || char.IsWhiteSpace(line[index - 1]) || line[index - 1] == BlockCommentOpener;
}
