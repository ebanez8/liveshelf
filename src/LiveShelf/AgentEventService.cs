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

    private readonly CancellationTokenSource _cancellation = new();
    private readonly Task _listenerTask;
    private bool _isDisposed;

    public AgentEventService()
    {
        _listenerTask = Task.Run(() => ListenAsync(_cancellation.Token));
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
            try
            {
                await using var pipe = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.In,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await pipe.WaitForConnectionAsync(cancellationToken);

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
                break;
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    public void PublishQueuedEvents()
    {
        var path = QueueFilePath;
        if (!File.Exists(path))
        {
            return;
        }

        string[] lines;
        try
        {
            lines = File.ReadAllLines(path);
            File.Delete(path);
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

        EventReceived?.Invoke(this, agentEvent);
    }
}
