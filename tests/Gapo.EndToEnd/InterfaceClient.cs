using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;
using Gapo.Core.Optimisation;
using Gapo.Core.Protocol;

namespace Gapo.EndToEnd;

public sealed record UnknownGameRun(int LiveSamplesWithFrames, double MedianLiveFps, bool EverOfferedToOptimise, string WatchingDetail, bool RefusedToOptimise, bool SawTheGameLeave, DiagnosisMessage? Diagnosis);

public sealed record SettingControlRun(
    GameSettingsMessage? First,
    bool LockAppeared,
    bool SetAppeared,
    string SetStatus,
    string ShadowsInFileAfterSet,
    bool PutBackAppeared,
    string ShadowsInFileAfterPutBack,
    bool RefusedAnUnknownValue,
    string ReportPath,
    IReadOnlyList<string> Proposals,
    PlanRun Plan);

public sealed record PlanRun(PlanMessage? WhileSwitchedOff, bool OptimiseRefusedWhileSwitchedOff, PlanMessage Plan, IReadOnlyList<ProgressMessage> Progress, double ActualSeconds)
{
    public bool ProgressOnlyMovesForward =>
        Progress.Count > 0 && Progress.Zip(Progress.Skip(1)).All(pair => pair.Second.StepsDone >= pair.First.StepsDone && pair.Second.ElapsedSeconds >= pair.First.ElapsedSeconds) && Progress.All(entry => entry.StepsExpected >= entry.StepsDone);

    public bool EstimateCovered => ActualSeconds <= Plan.SecondsHigh && ActualSeconds >= Plan.SecondsLow / 2.0;
}

public sealed record ServedRestartRun(bool OfferedToOptimise, int RestartsAsked, IReadOnlyList<string> StatusTitles, string ReportPath, PlanRun Plan);

public sealed record ServedRun(int LiveSamples, double FirstLiveFps, int ChartPoints, IReadOnlyList<string> StatusTitles, bool SawWatching, bool SawOptimising, bool SawFinished, string ReportPath);

public sealed class InterfaceClient : IDisposable
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan GameTimeout = TimeSpan.FromSeconds(150);
    private static readonly TimeSpan PlanTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan OptimisationTimeout = TimeSpan.FromSeconds(330);
    private static readonly TimeSpan RestartOptimisationTimeout = TimeSpan.FromMinutes(12);

    private readonly NamedPipeClientStream pipe;
    private readonly BlockingCollection<AgentMessage> received = new();
    private readonly StreamWriter writer;

    private InterfaceClient(NamedPipeClientStream pipe)
    {
        this.pipe = pipe;
        writer = new StreamWriter(pipe, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), leaveOpen: true) { AutoFlush = true, NewLine = "\n" };
        new Thread(Read) { IsBackground = true, Name = "interface client" }.Start();
    }

    public static InterfaceClient Connect(string pipeName)
    {
        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        pipe.Connect(ConnectTimeout);
        return new InterfaceClient(pipe);
    }

    public ServedRun OptimiseAndWaitForTheReport(Goal goal)
    {
        var statusTitles = new List<string>();
        var states = new HashSet<AgentState>();
        var liveSamples = 0;
        var chartPoints = 0;
        var firstLiveFps = 0.0;
        void Observe(AgentMessage message)
        {
            if (message is StatusMessage status)
            {
                statusTitles.Add(status.Title);
                states.Add(status.State);
            }
            else if (message is LiveSampleMessage sample && sample.GameConnected && sample.AverageFps > 0)
            {
                liveSamples++;
                chartPoints = Math.Max(chartPoints, sample.RecentFrameMs.Count);
                firstLiveFps = firstLiveFps > 0 ? firstLiveFps : sample.AverageFps;
            }
        }

        WaitFor(message => message is LiveSampleMessage { GameConnected: true, AverageFps: > 0 }, GameTimeout, Observe, "The agent never reported a running game to the interface.");
        writer.WriteLine(WireFormat.Encode(new OptimiseCommand(goal)));
        var report = (ReportReadyMessage)WaitFor(message => message is ReportReadyMessage, OptimisationTimeout, Observe, "The agent never announced a finished report to the interface.");
        return new ServedRun(
            liveSamples,
            firstLiveFps,
            chartPoints,
            statusTitles,
            states.Contains(AgentState.Watching),
            states.Contains(AgentState.Optimising),
            states.Contains(AgentState.Finished),
            report.Path);
    }

    public UnknownGameRun WatchAnUnknownGame(string expectedName, TimeSpan watchFor, TimeSpan waitForTheGameToLeave)
    {
        var fps = new List<double>();
        var offeredToOptimise = false;
        var watchingDetail = string.Empty;
        var refused = false;
        DiagnosisMessage? diagnosis = null;
        void Observe(AgentMessage message)
        {
            switch (message)
            {
                case DiagnosisMessage found when found.GameName == expectedName:
                    diagnosis = found;
                    break;
                case StatusMessage { State: AgentState.Watching } status when status.Title == "Watching " + expectedName:
                    watchingDetail = status.Detail;
                    break;
                case StatusMessage { State: AgentState.Watching } status when status.Title == expectedName + " cannot be optimised yet":
                    refused = true;
                    break;
                case LiveSampleMessage { GameConnected: true } sample when sample.GameName == expectedName:
                    offeredToOptimise |= sample.CanOptimise;
                    if (sample.AverageFps > 0)
                    {
                        fps.Add(sample.AverageFps);
                    }

                    break;
            }
        }

        WaitFor(message => message is LiveSampleMessage { GameConnected: true, AverageFps: > 0 } sample && sample.GameName == expectedName, GameTimeout, Observe, $"The agent never reported '{expectedName}' with frame times to the interface.");
        var until = DateTime.UtcNow + watchFor;
        while (DateTime.UtcNow < until)
        {
            if (received.TryTake(out var message, TimeSpan.FromMilliseconds(500)))
            {
                Observe(message);
            }
        }

        writer.WriteLine(WireFormat.Encode(new OptimiseCommand(Goal.Balanced)));
        WaitFor(_ => refused, TimeSpan.FromSeconds(10), Observe, "The agent did not answer the Optimise command for a game it cannot optimise.");
        var left = false;
        try
        {
            WaitFor(message => message is LiveSampleMessage { GameConnected: false }, waitForTheGameToLeave, Observe, "The agent did not notice that the game exited.");
            left = true;
        }
        catch (TimeoutException)
        {
        }

        var sorted = fps.Order().ToList();
        return new UnknownGameRun(fps.Count, sorted.Count == 0 ? 0 : sorted[sorted.Count / 2], offeredToOptimise, watchingDetail, refused, left, diagnosis);
    }

    public ServedRestartRun OptimiseThroughRestarts(string expectedName, Goal goal, Action restartTheGame)
    {
        var titles = new List<string>();
        var offered = false;
        var restarts = 0;
        void Observe(AgentMessage message)
        {
            if (message is StatusMessage status)
            {
                titles.Add(status.Title);
                if (status.Title.StartsWith("Restart the game to test", StringComparison.Ordinal))
                {
                    restarts++;
                    restartTheGame();
                }
            }
            else if (message is LiveSampleMessage { GameConnected: true, CanOptimise: true } sample && sample.GameName == expectedName)
            {
                offered = true;
            }
        }

        WaitFor(message => message is LiveSampleMessage { GameConnected: true, CanOptimise: true, AverageFps: > 0 } sample && sample.GameName == expectedName, GameTimeout, Observe, $"The agent never offered to optimise '{expectedName}'.");
        writer.WriteLine(WireFormat.Encode(new PlanCommand(goal)));
        var plan = (PlanMessage)WaitFor(message => message is PlanMessage, PlanTimeout, Observe, "The agent never sent a plan for the restart-based optimisation.");
        var progress = new List<ProgressMessage>();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        writer.WriteLine(WireFormat.Encode(new OptimiseCommand(goal)));
        var report = (ReportReadyMessage)WaitFor(message => message is ReportReadyMessage, RestartOptimisationTimeout, message =>
        {
            Observe(message);
            if (message is ProgressMessage step)
            {
                progress.Add(step);
            }
        }, "The agent never finished the restart-based optimisation.");
        return new ServedRestartRun(offered, restarts, titles, report.Path, new PlanRun(null, false, plan, progress, clock.Elapsed.TotalSeconds));
    }

    public SettingControlRun ExerciseSettingControl(Func<string, string> readSettingFile)
    {
        void Ignore(AgentMessage message)
        {
        }

        var first = (GameSettingsMessage)WaitFor(message => message is GameSettingsMessage { Settings.Count: > 0 }, GameTimeout, Ignore, "The agent never sent the game's settings.");
        writer.WriteLine(WireFormat.Encode(new LockSettingCommand("AntiAliasing", true)));
        var locked = WaitFor(message => message is GameSettingsMessage settings && settings.Settings.Any(setting => setting.Key == "AntiAliasing" && setting.Locked), TimeSpan.FromSeconds(10), Ignore, "Locking anti-aliasing was not confirmed.");
        writer.WriteLine(WireFormat.Encode(new SetSettingCommand("Shadows", "High")));
        var setStatus = string.Empty;
        WaitFor(message =>
        {
            if (message is StatusMessage status)
            {
                setStatus = status.Title;
            }

            return message is GameSettingsMessage settings && settings.Settings.Any(setting => setting.Key == "Shadows" && setting.Value == "High");
        }, TimeSpan.FromSeconds(10), Ignore, "Setting shadows to High was not confirmed.");
        var afterSet = readSettingFile("Shadows");
        writer.WriteLine(WireFormat.Encode(new SetSettingCommand("Shadows", "Purple")));
        var refused = WaitFor(message => message is StatusMessage { Title: "Not changed" }, TimeSpan.FromSeconds(10), Ignore, "An unknown value was not refused.") is StatusMessage;
        writer.WriteLine(WireFormat.Encode(new RevertSettingCommand("Shadows")));
        WaitFor(message => message is GameSettingsMessage settings && settings.Settings.Any(setting => setting.Key == "Shadows" && !setting.ChangedFromOriginal), TimeSpan.FromSeconds(10), Ignore, "Putting shadows back was not confirmed.");
        var afterPutBack = readSettingFile("Shadows");
        writer.WriteLine(WireFormat.Encode(new AgentPreferencesCommand(AskBeforeEachChange: true, AllowFrameCap: false, TargetFps: 0, MostRestarts: 6, OptimiserEnabled: false)));
        writer.WriteLine(WireFormat.Encode(new PlanCommand(Goal.Balanced)));
        var switchedOffPlan = WaitFor(message => message is PlanMessage, PlanTimeout, Ignore, "The agent sent no plan while the optimiser was switched off.") as PlanMessage;
        writer.WriteLine(WireFormat.Encode(new OptimiseCommand(Goal.Balanced)));
        var refusedWhileOff = WaitFor(message => message is StatusMessage { Title: "The optimiser is switched off" } or ProgressMessage or ProposalMessage, PlanTimeout, Ignore, "The agent did not answer Optimise while the optimiser was switched off.") is StatusMessage;
        writer.WriteLine(WireFormat.Encode(new AgentPreferencesCommand(AskBeforeEachChange: true, AllowFrameCap: false, TargetFps: 0, MostRestarts: 6)));
        writer.WriteLine(WireFormat.Encode(new PlanCommand(Goal.Balanced)));
        var plan = (PlanMessage)WaitFor(message => message is PlanMessage, PlanTimeout, Ignore, "The agent never sent a plan.");
        var clock = System.Diagnostics.Stopwatch.StartNew();
        writer.WriteLine(WireFormat.Encode(new OptimiseCommand(Goal.Balanced)));
        var proposals = new List<string>();
        var progress = new List<ProgressMessage>();
        void Answer(AgentMessage message)
        {
            if (message is ProposalMessage proposal)
            {
                proposals.Add(proposal.Title);
                writer.WriteLine(WireFormat.Encode(new AnswerProposalCommand(!proposal.Title.Contains("Physics substeps", StringComparison.Ordinal))));
            }
            else if (message is ProgressMessage step)
            {
                progress.Add(step);
            }
        }

        var report = (ReportReadyMessage)WaitFor(message => message is ReportReadyMessage, OptimisationTimeout, Answer, "The agent never announced a finished report after the setting tests.");
        return new SettingControlRun(first, locked is not null, true, setStatus, afterSet, true, afterPutBack, refused, report.Path, proposals, new PlanRun(switchedOffPlan, refusedWhileOff, plan, progress, clock.Elapsed.TotalSeconds));
    }

    public void Dispose()
    {
        pipe.Dispose();
        received.Dispose();
    }

    private AgentMessage WaitFor(Func<AgentMessage, bool> wanted, TimeSpan timeout, Action<AgentMessage> observe, string failure)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (!received.TryTake(out var message, TimeSpan.FromMilliseconds(500)))
            {
                continue;
            }

            observe(message);
            if (wanted(message))
            {
                return message;
            }
        }

        throw new TimeoutException(failure);
    }

    private void Read()
    {
        try
        {
            using var reader = new StreamReader(pipe, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
            while (reader.ReadLine() is { } line)
            {
                if (WireFormat.DecodeMessage(line) is { } message)
                {
                    received.Add(message);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or InvalidOperationException)
        {
        }
    }
}
