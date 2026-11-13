using System.IO.Pipes;
using System.Text;
using Gapo.Core.Protocol;
using Microsoft.UI.Dispatching;

namespace Gapo.App;

public sealed class AgentLink : IDisposable
{
    private const int ConnectAttemptMilliseconds = 0;
    private const int RetryDelayMilliseconds = 1000;

    private readonly string pipeName;
    private readonly DispatcherQueue dispatcher;
    private readonly CancellationTokenSource closing = new();
    private readonly Lock writeGate = new();
    private StreamWriter? writer;

    public AgentLink(string pipeName, DispatcherQueue dispatcher)
    {
        this.pipeName = pipeName;
        this.dispatcher = dispatcher;
        _ = Task.Run(RunAsync);
    }

    public event Action<AgentMessage>? MessageReceived;

    public event Action<bool>? ConnectionChanged;

    public bool IsConnected { get; private set; }

    public void Send(UiCommand command)
    {
        lock (writeGate)
        {
            try
            {
                writer?.WriteLine(WireFormat.Encode(command));
            }
            catch (IOException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    public void Dispose()
    {
        closing.Cancel();
        closing.Dispose();
    }

    private async Task RunAsync()
    {
        var token = closing.Token;
        while (!token.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                await pipe.ConnectAsync(ConnectAttemptMilliseconds, token);
                await ConverseAsync(pipe, token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (TimeoutException)
            {
            }
            catch (IOException)
            {
            }

            SetConnected(false);
            try
            {
                await Task.Delay(RetryDelayMilliseconds, token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task ConverseAsync(NamedPipeClientStream pipe, CancellationToken token)
    {
        lock (writeGate)
        {
            writer = new StreamWriter(pipe, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), leaveOpen: true) { AutoFlush = true, NewLine = "\n" };
        }

        SetConnected(true);
        try
        {
            using var reader = new StreamReader(pipe, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
            while (await reader.ReadLineAsync(token) is { } line)
            {
                if (WireFormat.DecodeMessage(line) is { } message)
                {
                    dispatcher.TryEnqueue(() => MessageReceived?.Invoke(message));
                }
            }
        }
        finally
        {
            lock (writeGate)
            {
                writer = null;
            }
        }
    }

    private void SetConnected(bool connected)
    {
        if (IsConnected == connected)
        {
            return;
        }

        IsConnected = connected;
        dispatcher.TryEnqueue(() => ConnectionChanged?.Invoke(connected));
    }
}
