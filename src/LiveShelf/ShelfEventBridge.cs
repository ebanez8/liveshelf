using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LiveShelf;

internal sealed class ShelfEventBridge : IDisposable
{
    internal const string PipeName = "LiveShelf.Events";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly CancellationTokenSource _cancellation = new();
    private readonly Task _listenerTask;
    private bool _isDisposed;

    public ShelfEventBridge()
    {
        _listenerTask = Task.Run(() => ListenAsync(_cancellation.Token));
    }

    public event EventHandler<ShelfBridgeEvent>? EventReceived;

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
                var events = JsonSerializer.Deserialize<ShelfBridgeEvent[]>(payload, JsonOptions);
                if (events is null)
                {
                    return;
                }

                foreach (var bridgeEvent in events)
                {
                    EventReceived?.Invoke(this, bridgeEvent);
                }

                return;
            }

            var singleEvent = JsonSerializer.Deserialize<ShelfBridgeEvent>(payload, JsonOptions);
            if (singleEvent is not null)
            {
                EventReceived?.Invoke(this, singleEvent);
            }
        }
        catch (JsonException)
        {
        }
    }
}

internal sealed class ShelfBridgeEvent
{
    [JsonPropertyName("event")]
    public string EventName { get; init; } = string.Empty;

    public string Source { get; init; } = string.Empty;

    public string Status { get; init; } = string.Empty;

    public string Tool { get; init; } = string.Empty;

    public string Title { get; init; } = string.Empty;

    public string WindowTitle { get; init; } = string.Empty;

    public string ProcessName { get; init; } = string.Empty;

    public string Details { get; init; } = string.Empty;

    public string Message { get; init; } = string.Empty;

    public string Url { get; init; } = string.Empty;

    public int? FilesChanged { get; init; }

    public int? ProcessId { get; init; }

    public int? Pid { get; init; }

    public long? Hwnd { get; init; }

    public bool? Success { get; init; }

    [JsonIgnore]
    public int EffectiveProcessId => ProcessId ?? Pid ?? 0;

    [JsonIgnore]
    public string EffectiveTitle => string.IsNullOrWhiteSpace(WindowTitle) ? Title : WindowTitle;
}
