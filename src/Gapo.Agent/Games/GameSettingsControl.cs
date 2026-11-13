using Gapo.Adapters.Sandbox;
using Gapo.Agent.Optimisation;
using Gapo.Core.Games.Unreal;
using Gapo.Core.Optimisation;
using Gapo.Sandbox.Contract;
using Gapo.Windows.Games;

namespace Gapo.Agent.Games;

public interface IGameSettingsControl
{
    string GameKey { get; }

    string GameName { get; }

    bool CanChange { get; }

    bool AppliesAtRestart { get; }

    string Note { get; }

    IReadOnlyList<SettingState> Read(IReadOnlySet<string> locked);

    string? Set(string key, string value);

    string? Revert(string key);
}

public sealed class SandboxSettingsControl(string gameName, string settingsPath, string journalPath) : IGameSettingsControl
{
    private const string SetByYou = "GAPO (set by you)";

    public string GameKey => "sandbox";

    public string GameName => gameName;

    public bool CanChange => settingsPath.Length > 0 && File.Exists(settingsPath);

    public bool AppliesAtRestart => false;

    public string Note => "Changes apply while the game runs.";

    public IReadOnlyList<SettingState> Read(IReadOnlySet<string> locked)
    {
        if (Current() is not { } current)
        {
            return [];
        }

        var original = FileChangeJournal.OriginalFor(journalPath, settingsPath) ?? current;
        return SandboxSettingDefinitions.All
            .Select(definition => SettingState.From(definition, current[definition.Key], original[definition.Key], locked.Contains(definition.Key)))
            .ToList();
    }

    public string? Set(string key, string value)
    {
        if (SandboxSettingDefinitions.All.FirstOrDefault(definition => definition.Key == key) is not { } definition || definition.Find(value) is not { } choice)
        {
            return "That value is not one GAPO knows for this setting.";
        }

        if (Current() is not { } current)
        {
            return "The game's settings file could not be read.";
        }

        var after = current.With(new Dictionary<string, string> { [key] = choice.Value });
        FileChangeJournal.RecordManualChange(journalPath, settingsPath, current, after);
        FileChangeJournal.WriteSettingsFile(settingsPath, after, SetByYou);
        return null;
    }

    public string? Revert(string key)
    {
        if (Current() is not { } current)
        {
            return "The game's settings file could not be read.";
        }

        var original = FileChangeJournal.OriginalFor(journalPath, settingsPath) ?? current;
        return Set(key, original[key]);
    }

    private GameSettings? Current() =>
        SandboxSettings.Load(settingsPath) is { } loaded ? SandboxSettingsMap.ToGameSettings(loaded, loaded.Revision) : null;
}

public sealed class UnrealSettingsControl(string gameKey, string gameName, string? settingsPath, string? antiCheat, string journalPath) : IGameSettingsControl
{
    public string GameKey => gameKey;

    public string GameName => gameName;

    public bool CanChange => antiCheat is null && settingsPath is not null;

    public bool AppliesAtRestart => true;

    public string Note => antiCheat is not null
        ? $"Protected by {antiCheat}: GAPO shows these settings but never changes the files of a game with anti-cheat."
        : settingsPath is null
            ? "No settings file found yet; the game writes one after you change a setting or quit once."
            : "Unreal Engine games read these settings when they start: a change takes effect the next time you start the game.";

    public IReadOnlyList<SettingState> Read(IReadOnlySet<string> locked)
    {
        if (settingsPath is null || IniSettingsFile.Read(settingsPath) is not var (document, _))
        {
            return [];
        }

        var current = UnrealSettings.Read(document);
        var originals = IniChangeJournal.OriginalsFor(journalPath, settingsPath);
        var states = new List<SettingState>();
        foreach (var definition in UnrealSettings.Definitions)
        {
            if (!current.TryGetValue(definition.Key, out var value))
            {
                continue;
            }

            var setting = UnrealSettings.Find(definition.Key)!;
            var original = originals.TryGetValue(new IniKey(setting.Section, setting.Key).ToString(), out var recorded) ? recorded ?? value : value;
            states.Add(SettingState.From(definition, value, original, locked.Contains(definition.Key)));
        }

        return states;
    }

    public string? Set(string key, string value)
    {
        if (!CanChange)
        {
            return Note;
        }

        if (UnrealSettings.Definitions.FirstOrDefault(definition => definition.Key == key) is not { } definition || definition.Find(value) is not { } choice || UnrealSettings.Find(key) is not { } setting)
        {
            return "That value is not one GAPO knows for this setting.";
        }

        if (IniSettingsFile.Read(settingsPath!) is not var (document, _))
        {
            return "The game's settings file could not be read.";
        }

        var before = document.Get(setting.Section, setting.Key);
        var journal = new IniChangeJournal(journalPath);
        var iniKey = new IniKey(setting.Section, setting.Key);
        journal.Begin(settingsPath!);
        journal.TrialStarted(iniKey, before, choice.Value);
        if (!IniSettingsFile.Change(settingsPath!, ini => ini.Set(setting.Section, setting.Key, choice.Value)))
        {
            journal.TrialEnded(iniKey, before);
            return "The game's settings file could not be written.";
        }

        journal.TrialEnded(iniKey, choice.Value);
        journal.Finished();
        return null;
    }

    public string? Revert(string key)
    {
        if (!CanChange)
        {
            return Note;
        }

        if (UnrealSettings.Find(key) is not { } setting)
        {
            return "GAPO does not know that setting.";
        }

        var originals = IniChangeJournal.OriginalsFor(journalPath, settingsPath!);
        if (!originals.TryGetValue(new IniKey(setting.Section, setting.Key).ToString(), out var original))
        {
            return null;
        }

        var journal = new IniChangeJournal(journalPath);
        var iniKey = new IniKey(setting.Section, setting.Key);
        journal.Begin(settingsPath!);
        if (!IniSettingsFile.Change(settingsPath!, ini =>
            {
                if (original is null)
                {
                    ini.Remove(setting.Section, setting.Key);
                }
                else
                {
                    ini.Set(setting.Section, setting.Key, original);
                }
            }))
        {
            return "The game's settings file could not be written.";
        }

        journal.TrialEnded(iniKey, original);
        journal.Finished();
        return null;
    }
}
