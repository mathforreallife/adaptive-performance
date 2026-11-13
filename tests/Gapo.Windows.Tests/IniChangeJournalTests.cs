using System.Text;
using Gapo.Windows.Games;
using Xunit;

namespace Gapo.Windows.Tests;

public sealed class IniChangeJournalTests : IDisposable
{
    private const string Original =
        "[ScalabilityGroups]\r\n" +
        "sg.ShadowQuality=3\r\n" +
        "sg.ViewDistanceQuality=3\r\n" +
        "; the game wrote this\r\n" +
        "\r\n" +
        "[/Script/Engine.GameUserSettings]\r\n" +
        "FrameRateLimit=0.000000\r\n";

    private static readonly IniKey Shadows = new("ScalabilityGroups", "sg.ShadowQuality");
    private static readonly IniKey Foliage = new("ScalabilityGroups", "sg.FoliageQuality");

    private readonly string directory = Directory.CreateTempSubdirectory("gapo-ini-journal-").FullName;

    private string SettingsPath => Path.Combine(directory, "GameUserSettings.ini");

    private string JournalPath => Path.Combine(directory, "journal-ini.json");

    public void Dispose() => Directory.Delete(directory, recursive: true);

    [Fact]
    public void RevertAllPutsBackExactlyTheKeysGapoTouchedAndKeepsThePlayersOwnChanges()
    {
        File.WriteAllText(SettingsPath, Original);
        var journal = new IniChangeJournal(JournalPath);
        journal.Begin(SettingsPath);
        Change(journal, Shadows, "3", "2");
        journal.TrialEnded(Shadows, "2");
        Change(journal, Foliage, null, "2");
        journal.TrialEnded(Foliage, "2");
        journal.Finished();
        IniSettingsFile.Change(SettingsPath, document => document.Set("ScalabilityGroups", "sg.ViewDistanceQuality", "1"));

        Assert.True(IniChangeJournal.RevertAll(JournalPath));

        Assert.Equal(Original.Replace("sg.ViewDistanceQuality=3", "sg.ViewDistanceQuality=1", StringComparison.Ordinal), File.ReadAllText(SettingsPath));
    }

    [Fact]
    public void RevertAllWithoutOtherChangesRestoresTheFileByteForByte()
    {
        File.WriteAllText(SettingsPath, Original);
        var journal = new IniChangeJournal(JournalPath);
        journal.Begin(SettingsPath);
        Change(journal, Shadows, "3", "1");
        journal.TrialEnded(Shadows, "1");

        IniChangeJournal.RevertAll(JournalPath);

        Assert.Equal(Original, File.ReadAllText(SettingsPath));
        Assert.Equal(Original, File.ReadAllText(SettingsPath + ".gapo-original"));
    }

    [Fact]
    public void ATrialInterruptedByACrashIsUndoneAtTheNextStart()
    {
        File.WriteAllText(SettingsPath, Original);
        var journal = new IniChangeJournal(JournalPath);
        journal.Begin(SettingsPath);
        Change(journal, Shadows, "3", "2");
        journal.TrialEnded(Shadows, "2");
        Change(journal, Foliage, null, "1");

        Assert.True(IniChangeJournal.RestoreInterruptedTrial(JournalPath));
        Assert.False(IniChangeJournal.RestoreInterruptedTrial(JournalPath));

        var text = File.ReadAllText(SettingsPath);
        Assert.Contains("sg.ShadowQuality=2", text, StringComparison.Ordinal);
        Assert.DoesNotContain("sg.FoliageQuality", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ALaterRunStillRevertsToTheTrueOriginal()
    {
        File.WriteAllText(SettingsPath, Original);
        var first = new IniChangeJournal(JournalPath);
        first.Begin(SettingsPath);
        Change(first, Shadows, "3", "2");
        first.TrialEnded(Shadows, "2");
        first.Finished();
        var second = new IniChangeJournal(JournalPath);
        second.Begin(SettingsPath);
        Change(second, Shadows, "2", "1");
        second.TrialEnded(Shadows, "1");

        IniChangeJournal.RevertAll(JournalPath);

        Assert.Equal(Original, File.ReadAllText(SettingsPath));
    }

    [Fact]
    public void TheFilesEncodingAndByteOrderMarkAreKept()
    {
        File.WriteAllText(SettingsPath, Original, Encoding.Unicode);

        IniSettingsFile.Change(SettingsPath, document => document.Set("ScalabilityGroups", "sg.ShadowQuality", "2"));

        var bytes = File.ReadAllBytes(SettingsPath);
        Assert.Equal(0xFF, bytes[0]);
        Assert.Equal(0xFE, bytes[1]);
        Assert.Contains("sg.ShadowQuality=2", File.ReadAllText(SettingsPath, Encoding.Unicode), StringComparison.Ordinal);
    }

    [Fact]
    public void NothingToRevertIsSaidNotFaked() =>
        Assert.False(IniChangeJournal.RevertAll(JournalPath));

    private void Change(IniChangeJournal journal, IniKey key, string? before, string after)
    {
        journal.TrialStarted(key, before, after);
        IniSettingsFile.Change(SettingsPath, document => document.Set(key.Section, key.Key, after));
    }
}
