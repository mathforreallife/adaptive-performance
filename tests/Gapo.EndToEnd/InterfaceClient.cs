using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;
using Gapo.Core.Optimisation;
using Gapo.Core.Protocol;

namespace Gapo.EndToEnd;

public sealed record UnknownGameRun(int LiveSamplesWithFrames, double MedianLiveFps, bool EverOfferedToOptimise, string WatchingDetail, bool RefusedToOptimise, bool SawTheGameLeave, DiagnosisMessage? Diagnosis);

public sealed record ServedRun(int LiveSamples, double FirstLiveFps, int ChartPoints, IReadOnlyList<string> StatusTitles, bool SawWatching, bool SawOptimising, bool SawFinished, string ReportPath);

public sealed class InterfaceClient : IDisposable
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan GameTimeout = TimeSpan.FromSeconds(150);
    private static readonly TimeSpan OptimisationTimeout = TimeSpan.FromSeconds(330);

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
