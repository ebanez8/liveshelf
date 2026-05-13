using System.IO;
using System.IO.Pipes;
using System.Text.Json;

namespace LiveShelf;

internal sealed class AgentEventService : IDisposable
{
    internal const string PipeName = "LiveShelfAgentEvents";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly TimeSpan QueueDrainInterval = TimeSpan.FromSeconds(15);

    private readonly CancellationTokenSource _cancellation = new();
    private readonly Task _listenerTask;
    private readonly Timer _queueDrainTimer;
    private int _drainInProgress;
    private bool _isDisposed;

    public AgentEventService()
    {
        _listenerTask = Task.Run(() => ListenAsync(_cancellation.Token));
        _queueDrainTimer = new Timer(
            _ => PublishQueuedEvents(),
            state: null,
            dueTime: QueueDrainInterval,
            period: QueueDrainInterval);
    }

    public event EventHandler<AgentEvent>? EventReceived;

    public static string QueueFilePath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LiveShelf",
            "queued-agent-events.jsonl");

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _cancellation.Cancel();
        _queueDrainTimer.Dispose();

        try
        {
            _listenerTask.Wait(TimeSpan.FromMilliseconds(300));
        }
        catch (AggregateException)
        {
        }
        catch (OperationCanceledException)
        {
        }

        _cancellation.Dispose();
    }

    private async Task ListenAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.In,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await pipe.WaitForConnectionAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                pipe?.Dispose();
                break;
            }
            catch (IOException)
            {
                pipe?.Dispose();
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                pipe?.Dispose();
                continue;
            }

            var connectedPipe = pipe;
            _ = Task.Run(() => HandleClientAsync(connectedPipe, cancellationToken), cancellationToken);
            _ = Task.Run(PublishQueuedEvents, cancellationToken);
        }
    }

    private async Task HandleClientAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        try
        {
            using var reader = new StreamReader(pipe);
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(cancellationToken);
                if (line is null)
                {
                    break;
                }

                PublishPayload(line);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
        catch (Exception ex)
        {
            CrashLogger.Log(ex);
        }
    }

    public void PublishQueuedEvents()
    {
        if (Interlocked.Exchange(ref _drainInProgress, 1) == 1)
        {
            return;
        }

        try
        {
            var path = QueueFilePath;
            if (!File.Exists(path))
            {
                return;
            }

            var tempPath = $"{path}.draining-{Guid.NewGuid():N}";
            string[] lines;
            try
            {
                File.Move(path, tempPath);
                lines = File.ReadAllLines(tempPath);
                File.Delete(tempPath);
            }
            catch (IOException)
            {
                return;
            }
            catch (UnauthorizedAccessException)
            {
                return;
            }

            foreach (var line in lines)
            {
                PublishPayload(line);
            }
        }
        finally
        {
            Interlocked.Exchange(ref _drainInProgress, 0);
        }
    }

    private void PublishPayload(string payload)
    {
        payload = payload.Trim();
        if (payload.Length == 0)
        {
            return;
        }

        try
        {
            if (payload.StartsWith("[", StringComparison.Ordinal))
            {
                var events = JsonSerializer.Deserialize<AgentEvent[]>(payload, JsonOptions);
                if (events is null)
                {
                    return;
                }

                foreach (var agentEvent in events)
                {
                    PublishEvent(agentEvent);
                }

                return;
            }

            var singleEvent = JsonSerializer.Deserialize<AgentEvent>(payload, JsonOptions);
            if (singleEvent is not null)
            {
                PublishEvent(singleEvent);
            }
        }
        catch (JsonException)
        {
        }
    }

    private void PublishEvent(AgentEvent agentEvent)
    {
        if (string.IsNullOrWhiteSpace(agentEvent.Source))
        {
            return;
        }

        try
        {
            EventReceived?.Invoke(this, agentEvent);
        }
        catch (Exception ex)
        {
            CrashLogger.Log(ex);
        }
    }
}
