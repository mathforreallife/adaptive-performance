using System.Text;

namespace Gapo.SourceRules;

public static class CSharpCommentStripper
{
    public static string Strip(string text)
    {
        var spans = CSharpComments.SpansIn(CSharpComments.Parse(text));
        if (spans.Count == 0)
        {
            return text;
        }

        var isCommentCharacter = new bool[text.Length];
        foreach (var span in spans)
        {
            Array.Fill(isCommentCharacter, true, span.Start, span.Length);
        }

        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var stripped = new StringBuilder(text.Length);
        var lineStart = 0;
        while (lineStart < text.Length)
        {
            var lineEnd = text.IndexOf('\n', lineStart);
            var nextLineStart = lineEnd < 0 ? text.Length : lineEnd + 1;
            AppendLineWithoutComments(text, isCommentCharacter, lineStart, nextLineStart, stripped, newline);
            lineStart = nextLineStart;
        }

        return stripped.ToString();
    }

    private static void AppendLineWithoutComments(
        string text,
        bool[] isCommentCharacter,
        int lineStart,
        int nextLineStart,
        StringBuilder stripped,
        string newline)
    {
        var kept = new StringBuilder(nextLineStart - lineStart);
        var lineHadComment = false;
        for (var index = lineStart; index < nextLineStart; index++)
        {
            if (isCommentCharacter[index])
            {
                lineHadComment = true;
            }
            else if (text[index] is not ('\r' or '\n'))
            {
                kept.Append(text[index]);
            }
        }

        if (!lineHadComment)
        {
            stripped.Append(text, lineStart, nextLineStart - lineStart);
            return;
        }

        var remaining = kept.ToString().TrimEnd();
        if (remaining.Length > 0)
        {
            stripped.Append(remaining).Append(newline);
        }
    }
}
