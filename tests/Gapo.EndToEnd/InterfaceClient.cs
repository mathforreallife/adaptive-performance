using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;
using Gapo.Core.Optimisation;
using Gapo.Core.Protocol;

namespace Gapo.EndToEnd;

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
