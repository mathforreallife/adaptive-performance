using System.Text;
using System.Text.Json;
using Gapo.Core.Games.Unreal;

namespace Gapo.Windows.Games;

public sealed record IniKey(string Section, string Key)
{
    public override string ToString() => Section + "|" + Key;

    public static IniKey Parse(string text)
    {
        var separator = text.LastIndexOf('|');
        return new IniKey(text[..separator], text[(separator + 1)..]);
    }
}

public static class IniSettingsFile
{
    public static (IniDocument Document, Encoding Encoding)? Read(string path)
    {
        try
        {
            using var reader = new StreamReader(path, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), detectEncodingFromByteOrderMarks: true);
            var text = reader.ReadToEnd();
            return (IniDocument.Parse(text), reader.CurrentEncoding);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static void Write(string path, IniDocument document, Encoding encoding)
    {
        var temporaryPath = path + ".gapo.tmp";
        using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            using (var writer = new StreamWriter(stream, encoding))
            {
                writer.Write(document.ToString());
            }
        }

        File.Move(temporaryPath, path, overwrite: true);
    }

    public static bool Change(string path, Action<IniDocument> change)
    {
        if (Read(path) is not var (document, encoding))
        {
            return false;
        }

        change(document);
        Write(path, document, encoding);
        return true;
    }
}

public sealed class IniChangeJournal
{
    private const string BackupSuffix = ".gapo-original";

    private readonly string journalPath;
    private string settingsPath = string.Empty;
    private readonly Dictionary<string, string?> original = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string?> committed = new(StringComparer.Ordinal);
    private string state = "Committed";
    private string trialKey = string.Empty;

    public IniChangeJournal(string journalPath)
    {
        this.journalPath = journalPath;
    }

    public void Begin(string iniPath)
    {
        settingsPath = iniPath;
        if (Read(journalPath) is { } existing && string.Equals(existing.SettingsPath, iniPath, StringComparison.OrdinalIgnoreCase))
        {
            foreach (var (key, value) in existing.Original)
            {
                original[key] = value;
            }

            foreach (var (key, value) in existing.Committed)
            {
                committed[key] = value;
            }
        }

        var backup = iniPath + BackupSuffix;
        if (!File.Exists(backup) && File.Exists(iniPath))
        {
            File.Copy(iniPath, backup);
        }

        state = "Committed";
        trialKey = string.Empty;
        Save();
    }

    public void TrialStarted(IniKey key, string? valueBefore, string valueUnderTest)
    {
        var name = key.ToString();
        original.TryAdd(name, valueBefore);
        committed.TryAdd(name, valueBefore);
        state = "TrialInFlight";
        trialKey = name + "=" + valueUnderTest;
        Save();
    }

    public void TrialEnded(IniKey key, string? committedValue)
    {
        committed[key.ToString()] = committedValue;
        state = "Committed";
        trialKey = string.Empty;
        Save();
    }

    public void Finished()
    {
        state = "Finished";
        trialKey = string.Empty;
        Save();
    }

    public static bool RestoreInterruptedTrial(string journalPath)
    {
        if (Read(journalPath) is not { State: "TrialInFlight" } content || content.TrialKey.Length == 0)
        {
            return false;
        }

        var key = IniKey.Parse(content.TrialKey[..content.TrialKey.IndexOf('=')]);
        var restored = content.Committed.TryGetValue(key.ToString(), out var value) ? value : content.Original.GetValueOrDefault(key.ToString());
        IniSettingsFile.Change(content.SettingsPath, document => Put(document, key, restored));
        Rewrite(journalPath, content with { State = "Committed", TrialKey = string.Empty });
        return true;
    }

    public static bool RevertAll(string journalPath)
    {
        if (Read(journalPath) is not { } content || content.Original.Count == 0)
        {
            return false;
        }

        var changed = IniSettingsFile.Change(content.SettingsPath, document =>
        {
            foreach (var (name, value) in content.Original)
            {
                Put(document, IniKey.Parse(name), value);
            }
        });
        Rewrite(journalPath, content with { State = "Finished", TrialKey = string.Empty, Committed = content.Original });
        return changed;
    }

    private static void Put(IniDocument document, IniKey key, string? value)
    {
        if (value is null)
        {
            document.Remove(key.Section, key.Key);
        }
        else
        {
            document.Set(key.Section, key.Key, value);
        }
    }

    private void Save() => Rewrite(journalPath, new Content(state, settingsPath, trialKey, new Dictionary<string, string?>(original), new Dictionary<string, string?>(committed)));

    private static void Rewrite(string journalPath, Content content)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(journalPath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporaryPath = journalPath + ".tmp";
        using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            using (var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
            {
                json.WriteStartObject();
                json.WriteString("format", "ini");
                json.WriteString("state", content.State);
                json.WriteString("settingsPath", content.SettingsPath);
                json.WriteString("trialKey", content.TrialKey);
                WriteValues(json, "original", content.Original);
                WriteValues(json, "committed", content.Committed);
                json.WriteEndObject();
            }

            stream.Flush(flushToDisk: true);
        }

        File.Move(temporaryPath, journalPath, overwrite: true);
    }

    private static void WriteValues(Utf8JsonWriter json, string name, IReadOnlyDictionary<string, string?> values)
    {
        json.WriteStartObject(name);
        foreach (var (key, value) in values)
        {
            if (value is null)
            {
                json.WriteNull(key);
            }
            else
            {
                json.WriteString(key, value);
            }
        }

        json.WriteEndObject();
    }

    private static Content? Read(string journalPath)
    {
        try
        {
            if (!File.Exists(journalPath))
            {
                return null;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(journalPath));
            var root = document.RootElement;
            return new Content(
                root.GetProperty("state").GetString() ?? string.Empty,
                root.GetProperty("settingsPath").GetString() ?? string.Empty,
                root.GetProperty("trialKey").GetString() ?? string.Empty,
                ReadValues(root.GetProperty("original")),
                ReadValues(root.GetProperty("committed")));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }

    private static Dictionary<string, string?> ReadValues(JsonElement element) =>
        element.EnumerateObject().ToDictionary(property => property.Name, property => property.Value.ValueKind == JsonValueKind.Null ? null : property.Value.GetString(), StringComparer.Ordinal);

    private sealed record Content(string State, string SettingsPath, string TrialKey, IReadOnlyDictionary<string, string?> Original, IReadOnlyDictionary<string, string?> Committed);
}
