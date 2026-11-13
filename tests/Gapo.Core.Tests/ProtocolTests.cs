using Gapo.Core.Measurement;
using Gapo.Core.Optimisation;
using Gapo.Core.Protocol;
using Xunit;

namespace Gapo.Core.Tests;

public sealed class ProtocolTests
{
    private const int Precision = 6;

    [Fact]
    public void StatusSurvivesTheWire()
    {
        var sent = new StatusMessage(AgentState.Optimising, "Testing: Shadows: Ultra → High", "Predicted saving about 6.1 ms.");

        Assert.Equal(sent, WireFormat.DecodeMessage(WireFormat.Encode(sent)));
    }

    [Fact]
    public void TheGameProcessAndItsBeforeAndAfterFpsSurviveTheWire()
    {
        var sent = new LiveSampleMessage(true, "Some game", 58.2, 17.2, 9.1, 0, 2.4, 40, 0.1, 12, false, [17.0], CanOptimise: true, GameProcessId: 4242, BeforeFps: 25.1, AfterFps: 60.0);

        var received = Assert.IsType<LiveSampleMessage>(WireFormat.DecodeMessage(WireFormat.Encode(sent)));

        Assert.Equal(4242, received.GameProcessId);
        Assert.Equal(25.1, received.BeforeFps);
        Assert.Equal(60.0, received.AfterFps);
    }

    [Fact]
    public void AGameThatCannotBeOptimisedSaysSoOnTheWire()
    {
        var sent = new LiveSampleMessage(true, "Some Steam game", 58.2, 17.2, 9.1, 0, 2.4, 40, 0.1, 12, false, [17.0], CanOptimise: false);

        var received = Assert.IsType<LiveSampleMessage>(WireFormat.DecodeMessage(WireFormat.Encode(sent)));

        Assert.False(received.CanOptimise);
    }

    [Fact]
    public void AnOlderAgentWithoutTheFlagMeansTheGameCanBeOptimised()
    {
        const string line = "{\"type\":\"sample\",\"gameConnected\":true,\"gameName\":\"x\",\"ownUseIsOverBudget\":false,\"recentFrameMs\":[]}";

        var received = Assert.IsType<LiveSampleMessage>(WireFormat.DecodeMessage(line));

        Assert.True(received.CanOptimise);
    }

    [Fact]
    public void ADiagnosisSurvivesTheWire()
    {
        var sent = new DiagnosisMessage("Some Steam game", "CpuOneThreadLimit", "The game is limited by one CPU thread", "Its busiest thread uses 97% of one processor.", ["Storage hitch: froze for 120 ms while saving."], "Observed for 20 s while the game ran.");

        var received = Assert.IsType<DiagnosisMessage>(WireFormat.DecodeMessage(WireFormat.Encode(sent)));

        Assert.Equal(sent with { AlsoNoticed = [] }, received with { AlsoNoticed = [] });
        Assert.Equal(sent.AlsoNoticed, received.AlsoNoticed);
    }

    [Fact]
    public void GameSettingsSurviveTheWire()
    {
        IReadOnlyList<SettingChoice> levels = [new("0", "Low"), new("3", "Epic")];
        var sent = new GameSettingsMessage("Some game", true, true, "Applies at the next start.", [new SettingState("sg.ShadowQuality", "Shadows", "3", "3", true, levels), new SettingState("ResolutionSizeX", "Resolution width", "1920", "1920", false, [])]);

        var received = Assert.IsType<GameSettingsMessage>(WireFormat.DecodeMessage(WireFormat.Encode(sent)));

        Assert.Equal(sent with { Settings = [] }, received with { Settings = [] });
        Assert.Equal(2, received.Settings.Count);
        Assert.Equal(sent.Settings[0] with { Choices = [] }, received.Settings[0] with { Choices = [] });
        Assert.Equal(levels, received.Settings[0].Choices);
        Assert.Empty(received.Settings[1].Choices);
    }

    [Fact]
    public void SettingCommandsSurviveTheWire()
    {
        UiCommand[] sent = [new LockSettingCommand("Shadows", true), new SetSettingCommand("sg.ShadowQuality", "2"), new RevertSettingCommand("Fog"), new AnswerProposalCommand(true), new AnswerProposalCommand(false), new AgentPreferencesCommand(true, false, 90, 3)];

        Assert.Equal(sent, sent.Select(command => WireFormat.DecodeCommand(WireFormat.Encode(command))));
        Assert.Null(WireFormat.DecodeCommand("{\"type\":\"set\",\"value\":\"2\"}"));
    }

    [Fact]
    public void AProposalSurvivesTheWire()
    {
        var sent = new ProposalMessage("Try Shadows: Ultra → High?", "Predicted saving about 6 ms; visual cost 3/10.");

        Assert.Equal(sent, WireFormat.DecodeMessage(WireFormat.Encode(sent)));
    }

    [Fact]
    public void LiveSampleSurvivesTheWire()
    {
        var sent = new LiveSampleMessage(true, "GAPO Sandbox", 24.9, 40.1, 39.6, 11.2, 0.99, 31, 0.13, 10.5, false, [40.0, 41.5, 120.25]);

        var received = Assert.IsType<LiveSampleMessage>(WireFormat.DecodeMessage(WireFormat.Encode(sent)));

        Assert.Equal(sent with { RecentFrameMs = [] }, received with { RecentFrameMs = [] });
        Assert.Equal(sent.RecentFrameMs, received.RecentFrameMs);
    }

    [Fact]
    public void ReportPathSurvivesTheWire()
    {
        var sent = new ReportReadyMessage(@"C:\Users\someone\AppData\Local\GAPO\reports\last.json");

        Assert.Equal(sent, WireFormat.DecodeMessage(WireFormat.Encode(sent)));
    }

    [Theory]
    [InlineData(Goal.Balanced)]
    [InlineData(Goal.MaximumFps)]
    [InlineData(Goal.Quality)]
    [InlineData(Goal.Smoothness)]
    public void OptimiseCommandCarriesItsGoal(Goal goal)
    {
        Assert.Equal(new OptimiseCommand(goal), WireFormat.DecodeCommand(WireFormat.Encode(new OptimiseCommand(goal))));
    }

    [Fact]
    public void CommandsWithoutArgumentsSurviveTheWire()
    {
        Assert.IsType<RevertAllCommand>(WireFormat.DecodeCommand(WireFormat.Encode(new RevertAllCommand())));
        Assert.IsType<StopCommand>(WireFormat.DecodeCommand(WireFormat.Encode(new StopCommand())));
    }

    [Fact]
    public void EachMessageIsASingleLine()
    {
        var encoded = WireFormat.Encode(new StatusMessage(AgentState.Watching, "Line one\nline two", "detail"));

        Assert.DoesNotContain('\n', encoded);
        Assert.Equal("Line one\nline two", Assert.IsType<StatusMessage>(WireFormat.DecodeMessage(encoded)).Title);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("{\"type\":\"somethingNew\"}")]
    public void UnreadableLinesAreIgnoredInsteadOfBreakingTheLink(string line)
    {
        Assert.Null(WireFormat.DecodeMessage(line));
        Assert.Null(WireFormat.DecodeCommand(line));
    }

    [Fact]
    public void PipeNameIsPerWindowsSession()
    {
        Assert.Equal("GAPO.Agent.3", WireFormat.PipeNameFor(3));
    }

    [Fact]
    public void LiveWindowKeepsOnlyTheRecentSpan()
    {
        var window = new LiveFrameWindow(TimeSpan.FromSeconds(2));
        for (var index = 1; index <= 300; index++)
        {
            window.Add(new MeasuredFrame(index * 20, 20, 18, 1, FrameMarks.None));
        }

        Assert.InRange(window.Count, 100, 102);
        Assert.Equal(50.0, window.Summarise().AverageFps, Precision);
    }

    [Fact]
    public void ChartKeepsTheWorstFrameOfEachBucketSoSpikesStayVisible()
    {
        var window = new LiveFrameWindow(TimeSpan.FromMinutes(1));
        for (var index = 1; index <= 600; index++)
        {
            window.Add(new MeasuredFrame(index * 10, index == 333 ? 95 : 10, 9, 1, FrameMarks.None));
        }

        var points = window.ChartPoints(150);

        Assert.Equal(150, points.Count);
        Assert.Equal(95.0, points.Max(), Precision);
        Assert.Equal(149, points.Count(point => Math.Abs(point - 10) < 0.001));
    }

    [Fact]
    public void ShortHistoriesAreChartedAsTheyAre()
    {
        var window = new LiveFrameWindow(TimeSpan.FromMinutes(1));
        window.Add(new MeasuredFrame(10, 12, 9, 1, FrameMarks.None));
        window.Add(new MeasuredFrame(20, 14, 9, 1, FrameMarks.None));

        Assert.Equal([12.0, 14.0], window.ChartPoints());
    }
}
