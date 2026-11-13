using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;
using Gapo.Core.Protocol;

namespace Gapo.Agent.Link;

public sealed class AgentPipeServer : IDisposable
{
    private const int MostQueuedMessages = 256;
    private const int StopTimeoutMs = 2000;

    private readonly string pipeName;
    private readonly ConcurrentQueue<UiCommand> commands = new();
    private readonly BlockingCollection<string> outgoing = new(MostQueuedMessages);
    private readonly CancellationTokenSource stopping = new();
    private readonly Thread connectionThread;
    private volatile bool hasClient;

    public AgentPipeServer(string pipeName)
    {
        this.pipeName = pipeName;
        connectionThread = new Thread(ServeClients) { IsBackground = true, Name = "GAPO interface link" };
        connectionThread.Start();
    }

    public bool HasClient => hasClient;

    public bool TryTakeCommand(out UiCommand command)
    {
        if (commands.TryDequeue(out var taken))
        {
            command = taken;
            return true;
        }

        command = new StopCommand();
        return false;
    }

    public void Send(AgentMessage message)
    {
        if (!hasClient)
        {
            return;
        }

        var line = WireFormat.Encode(message);
        while (!outgoing.TryAdd(line))
        {
            outgoing.TryTake(out _);
        }
    }

    public void Dispose()
    {
        stopping.Cancel();
        outgoing.CompleteAdding();
        connectionThread.Join(StopTimeoutMs);
        stopping.Dispose();
    }

    private void ServeClients()
    {
        while (!stopping.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                pipe.WaitForConnectionAsync(stopping.Token).GetAwaiter().GetResult();
                ServeOneClient(pipe);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (IOException)
            {
            }
            finally
            {
                hasClient = false;
            }
        }
    }

    private void ServeOneClient(NamedPipeServerStream pipe)
    {
        while (outgoing.TryTake(out _))
        {
        }

        hasClient = true;
        using var clientGone = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token);
        var reader = new Thread(() => ReadCommands(pipe, clientGone)) { IsBackground = true, Name = "GAPO interface commands" };
        reader.Start();
        try
        {
            using var writer = new StreamWriter(pipe, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), leaveOpen: true) { AutoFlush = true, NewLine = "\n" };
            foreach (var line in outgoing.GetConsumingEnumerable(clientGone.Token))
            {
                writer.WriteLine(line);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
        }
        finally
        {
            hasClient = false;
            clientGone.Cancel();
            if (pipe.IsConnected)
            {
                pipe.Disconnect();
            }

            reader.Join(StopTimeoutMs);
        }
    }

    private void ReadCommands(NamedPipeServerStream pipe, CancellationTokenSource clientGone)
    {
        try
        {
            using var reader = new StreamReader(pipe, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
            while (!clientGone.IsCancellationRequested && reader.ReadLine() is { } line)
            {
                if (WireFormat.DecodeCommand(line) is { } command)
                {
                    commands.Enqueue(command);
                }
            }
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            clientGone.Cancel();
        }
    }
}
