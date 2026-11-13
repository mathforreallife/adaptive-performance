using System.Text.Json;
using Gapo.Adapters.Sandbox;
using Gapo.Core.Optimisation;
using Gapo.Sandbox.Contract;

namespace Gapo.Agent.Optimisation;

public enum JournalState
{
    None,
    TrialInFlight,
    Committed,
    Finished,
}

public sealed record JournalContent(JournalState State, string SettingsPath, string TrialSetting, GameSettings Original, GameSettings LastCommitted);

public sealed class FileChangeJournal : IChangeJournal
{
    private const string RevertedBy = "GAPO (reverted to original)";
    private const string RecoveredBy = "GAPO (restored after an interrupted test)";

    private readonly string journalPath;
    private readonly Func<string> settingsPath;
    private GameSettings? original;

    public FileChangeJournal(string journalPath, Func<string> settingsPath)
    {
        this.journalPath = journalPath;
        this.settingsPath = settingsPath;
    }

    public void Begin(GameSettings original)
    {
        var path = settingsPath();
        this.original = Read(journalPath) is { } existing && string.Equals(existing.SettingsPath, path, StringComparison.OrdinalIgnoreCase)
            ? existing.Original
            : original;
        Write(new JournalContent(JournalState.Committed, path, string.Empty, this.original, original));
    }

    public static GameSettings? OriginalFor(string journalPath, string settingsPath) =>
        Read(journalPath) is { } content && string.Equals(content.SettingsPath, settingsPath, StringComparison.OrdinalIgnoreCase) ? content.Original : null;

    public static void RecordManualChange(string journalPath, string settingsPath, GameSettings before, GameSettings after)
    {
        var journal = new FileChangeJournal(journalPath, () => settingsPath);
        journal.Begin(before);
        journal.TrialEnded(after);
    }

    public static void WriteSettingsFile(string settingsPath, GameSettings settings, string modifiedBy) => WriteToGame(settingsPath, settings, modifiedBy);

    public void TrialStarted(GameSettings lastCommitted, SettingCandidate candidate) =>
        Write(new JournalContent(JournalState.TrialInFlight, settingsPath(), candidate.Setting, original ?? lastCommitted, lastCommitted));

    public void TrialEnded(GameSettings committed) =>
        Write(new JournalContent(JournalState.Committed, settingsPath(), string.Empty, original ?? committed, committed));

    public void Finished()
    {
        if (Read(journalPath) is { } content)
        {
            Write(content with { State = JournalState.Finished, TrialSetting = string.Empty });
        }
    }

    public static JournalContent? Read(string journalPath)
    {
        if (!File.Exists(journalPath))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(journalPath));
            var root = document.RootElement;
            return new JournalContent(
                Enum.Parse<JournalState>(root.GetProperty("state").GetString() ?? nameof(JournalState.None)),
                root.GetProperty("settingsPath").GetString() ?? string.Empty,
                root.GetProperty("trialSetting").GetString() ?? string.Empty,
                ReadSettings(root.GetProperty("original")),
                ReadSettings(root.GetProperty("lastCommitted")));
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or ArgumentException or IOException)
        {
            return null;
        }
    }

    public static bool RestoreInterruptedTrial(string journalPath)
    {
        if (Read(journalPath) is not { State: JournalState.TrialInFlight } content)
        {
            return false;
        }

        WriteToGame(content.SettingsPath, content.LastCommitted, RecoveredBy);
        new FileChangeJournal(journalPath, () => content.SettingsPath).Write(content with { State = JournalState.Committed, TrialSetting = string.Empty });
        return true;
    }

    public static bool RevertAll(string journalPath)
    {
        if (Read(journalPath) is not { } content || content.SettingsPath.Length == 0)
        {
            return false;
        }

        WriteToGame(content.SettingsPath, content.Original, RevertedBy);
        new FileChangeJournal(journalPath, () => content.SettingsPath).Write(content with { State = JournalState.Finished, TrialSetting = string.Empty, LastCommitted = content.Original });
        return true;
    }

    private static void WriteToGame(string settingsPath, GameSettings settings, string modifiedBy)
    {
        var revisionOnDisk = SandboxSettings.Load(settingsPath)?.Revision ?? settings.Revision;
        SandboxSettingsMap.ToSandboxSettings(settings.WithRevision(revisionOnDisk + 1), modifiedBy).Save(settingsPath);
    }

    private void Write(JournalContent content)
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
                json.WriteString("state", content.State.ToString());
                json.WriteString("settingsPath", content.SettingsPath);
                json.WriteString("trialSetting", content.TrialSetting);
                WriteSettings(json, "original", content.Original);
                WriteSettings(json, "lastCommitted", content.LastCommitted);
                json.WriteEndObject();
            }

            stream.Flush(flushToDisk: true);
        }

        File.Move(temporaryPath, journalPath, overwrite: true);
    }

    private static void WriteSettings(Utf8JsonWriter json, string name, GameSettings settings)
    {
        json.WriteStartObject(name);
        json.WriteNumber("revision", settings.Revision);
        json.WriteStartObject("values");
        foreach (var (key, value) in settings.Values)
        {
            json.WriteString(key, value);
        }

        json.WriteEndObject();
        json.WriteEndObject();
    }

    private static GameSettings ReadSettings(JsonElement element) =>
        new(
            element.GetProperty("values").EnumerateObject().Select(property => new KeyValuePair<string, string>(property.Name, property.Value.GetString() ?? string.Empty)),
            element.GetProperty("revision").GetInt32());
}
