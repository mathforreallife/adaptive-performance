namespace Gapo.Core.Games.Unreal;

public sealed class IniDocument
{
    private const char SectionStart = '[';
    private const char SectionEnd = ']';
    private const char Assignment = '=';
    private const string CommentStart = ";";
    private const string WindowsNewLine = "\r\n";

    private readonly List<string> lines;
    private readonly string newLine;
    private readonly bool endsWithNewLine;

    private IniDocument(List<string> lines, string newLine, bool endsWithNewLine)
    {
        this.lines = lines;
        this.newLine = newLine;
        this.endsWithNewLine = endsWithNewLine;
    }

    public static IniDocument Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0)
        {
            return new IniDocument([], WindowsNewLine, endsWithNewLine: true);
        }

        var newLine = text.Contains(WindowsNewLine, StringComparison.Ordinal) ? WindowsNewLine : "\n";
        var endsWithNewLine = text.EndsWith('\n');
        var body = endsWithNewLine ? text[..^(text.EndsWith(WindowsNewLine, StringComparison.Ordinal) ? 2 : 1)] : text;
        return new IniDocument(body.Split('\n').Select(line => line.TrimEnd('\r')).ToList(), newLine, endsWithNewLine);
    }

    public string? Get(string section, string key)
    {
        var index = FindKey(section, key);
        return index < 0 ? null : ValueOf(lines[index]);
    }

    public IReadOnlyList<string> Sections() =>
        lines.Select(SectionName).Where(name => name is not null).Select(name => name!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    public void Set(string section, string key, string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(value);
        if (value.Contains('\n') || value.Contains('\r'))
        {
            throw new ArgumentException("An INI value cannot span lines.", nameof(value));
        }

        var assignment = key + Assignment + value;
        var index = FindKey(section, key);
        if (index >= 0)
        {
            lines[index] = assignment;
            return;
        }

        var sectionLine = FindSection(section);
        if (sectionLine < 0)
        {
            if (lines.Count > 0 && lines[^1].Length > 0)
            {
                lines.Add(string.Empty);
            }

            lines.Add(SectionStart + section + SectionEnd);
            lines.Add(assignment);
            return;
        }

        var insertAt = sectionLine + 1;
        for (var line = sectionLine + 1; line < lines.Count && SectionName(lines[line]) is null; line++)
        {
            if (lines[line].Trim().Length > 0)
            {
                insertAt = line + 1;
            }
        }

        lines.Insert(insertAt, assignment);
    }

    public bool Remove(string section, string key)
    {
        var index = FindKey(section, key);
        if (index < 0)
        {
            return false;
        }

        lines.RemoveAt(index);
        return true;
    }

    public override string ToString()
    {
        var text = string.Join(newLine, lines);
        return endsWithNewLine && lines.Count > 0 ? text + newLine : text;
    }

    private int FindSection(string section)
    {
        for (var index = 0; index < lines.Count; index++)
        {
            if (string.Equals(SectionName(lines[index]), section, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return -1;
    }

    private int FindKey(string section, string key)
    {
        var found = -1;
        var inSection = false;
        for (var index = 0; index < lines.Count; index++)
        {
            if (SectionName(lines[index]) is { } name)
            {
                inSection = string.Equals(name, section, StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (inSection && string.Equals(KeyOf(lines[index]), key, StringComparison.OrdinalIgnoreCase))
            {
                found = index;
            }
        }

        return found;
    }

    private static string? SectionName(string line)
    {
        var trimmed = line.Trim();
        return trimmed.Length >= 2 && trimmed[0] == SectionStart && trimmed[^1] == SectionEnd ? trimmed[1..^1].Trim() : null;
    }

    private static string? KeyOf(string line)
    {
        var trimmed = line.TrimStart();
        if (trimmed.StartsWith(CommentStart, StringComparison.Ordinal))
        {
            return null;
        }

        var equals = trimmed.IndexOf(Assignment);
        return equals <= 0 ? null : trimmed[..equals].Trim();
    }

    private static string ValueOf(string line)
    {
        var equals = line.IndexOf(Assignment);
        return line[(equals + 1)..].Trim();
    }
}
