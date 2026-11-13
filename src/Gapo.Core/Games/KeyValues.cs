using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace Gapo.Core.Games;

public sealed class KeyValuesNode
{
    private readonly List<KeyValuesNode> children = [];

    public KeyValuesNode(string name, string? value)
    {
        Name = name;
        Value = value;
    }

    public string Name { get; }

    public string? Value { get; }

    public bool IsSection => Value is null;

    public IReadOnlyList<KeyValuesNode> Children => children;

    public KeyValuesNode? Child(string name) =>
        children.FirstOrDefault(child => string.Equals(child.Name, name, StringComparison.OrdinalIgnoreCase));

    public string? ValueOf(string name) => Child(name)?.Value;

    internal void Add(KeyValuesNode child) => children.Add(child);
}

public static class KeyValuesParser
{
    private const int DeepestNesting = 64;
    private const char Quote = '"';
    private const char Escape = '\\';
    private const char OpenSection = '{';
    private const char CloseSection = '}';
    private const char CommentStart = '/';
    private const char ConditionStart = '[';
    private const char ConditionEnd = ']';

    private enum TokenKind
    {
        End,
        Text,
        Open,
        Close,
        Malformed,
    }

    public static bool TryParse(string text, [NotNullWhen(true)] out KeyValuesNode? root)
    {
        ArgumentNullException.ThrowIfNull(text);
        root = null;
        var top = new KeyValuesNode(string.Empty, null);
        var open = new Stack<KeyValuesNode>();
        open.Push(top);
        var position = 0;
        while (true)
        {
            var (kind, key) = NextToken(text, ref position);
            switch (kind)
            {
                case TokenKind.End:
                    if (open.Count != 1)
                    {
                        return false;
                    }

                    root = top;
                    return true;
                case TokenKind.Close:
                    if (open.Count == 1)
                    {
                        return false;
                    }

                    open.Pop();
                    continue;
                case TokenKind.Text:
                    break;
                default:
                    return false;
            }

            var (valueKind, value) = NextToken(text, ref position);
            if (valueKind == TokenKind.Text)
            {
                open.Peek().Add(new KeyValuesNode(key, value));
            }
            else if (valueKind == TokenKind.Open && open.Count < DeepestNesting)
            {
                var section = new KeyValuesNode(key, null);
                open.Peek().Add(section);
                open.Push(section);
            }
            else
            {
                return false;
            }
        }
    }

    private static (TokenKind Kind, string Text) NextToken(string text, ref int position)
    {
        while (true)
        {
            SkipWhitespaceAndComments(text, ref position);
            if (position >= text.Length)
            {
                return (TokenKind.End, string.Empty);
            }

            var current = text[position];
            if (current == OpenSection)
            {
                position++;
                return (TokenKind.Open, string.Empty);
            }

            if (current == CloseSection)
            {
                position++;
                return (TokenKind.Close, string.Empty);
            }

            if (current == Quote)
            {
                return ReadQuoted(text, ref position);
            }

            var bare = ReadBare(text, ref position);
            if (bare.Length > 1 && bare[0] == ConditionStart && bare[^1] == ConditionEnd)
            {
                continue;
            }

            return (TokenKind.Text, bare);
        }
    }

    private static void SkipWhitespaceAndComments(string text, ref int position)
    {
        while (position < text.Length)
        {
            if (char.IsWhiteSpace(text[position]))
            {
                position++;
            }
            else if (text[position] == CommentStart && position + 1 < text.Length && text[position + 1] == CommentStart)
            {
                while (position < text.Length && text[position] != '\n')
                {
                    position++;
                }
            }
            else
            {
                return;
            }
        }
    }

    private static (TokenKind Kind, string Text) ReadQuoted(string text, ref int position)
    {
        var builder = new StringBuilder();
        position++;
        while (position < text.Length)
        {
            var current = text[position++];
            if (current == Quote)
            {
                return (TokenKind.Text, builder.ToString());
            }

            if (current == Escape && position < text.Length)
            {
                var escaped = text[position++];
                builder.Append(escaped switch
                {
                    'n' => "\n",
                    't' => "\t",
                    Escape => "\\",
                    Quote => "\"",
                    _ => string.Concat(Escape, escaped),
                });
            }
            else
            {
                builder.Append(current);
            }
        }

        return (TokenKind.Malformed, string.Empty);
    }

    private static string ReadBare(string text, ref int position)
    {
        var start = position;
        while (position < text.Length && !char.IsWhiteSpace(text[position]) && text[position] is not (Quote or OpenSection or CloseSection))
        {
            position++;
        }

        return text[start..position];
    }
}
