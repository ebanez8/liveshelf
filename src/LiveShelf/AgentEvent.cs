using System.Text.Json;
using System.Text.Json.Serialization;

namespace LiveShelf;

internal sealed class AgentEvent
{
    public string Source { get; init; } = string.Empty;

    public string SessionId { get; init; } = string.Empty;

    public string TurnId { get; init; } = string.Empty;

    public int ProcessId { get; init; }

    public int[] ParentProcessIds { get; init; } = [];

    public int ForegroundProcessId { get; init; }

    [JsonPropertyName("eventName")]
    public string EventName { get; init; } = string.Empty;

    public string Cwd { get; init; } = string.Empty;

    public string TranscriptPath { get; init; } = string.Empty;

    public string ToolName { get; init; } = string.Empty;

    public string ToolUseId { get; init; } = string.Empty;

    public JsonElement? ToolInput { get; init; }

    public JsonElement? ToolResponse { get; init; }

    public string LastAssistantMessage { get; init; } = string.Empty;

    public string Error { get; init; } = string.Empty;

    public string FilePath { get; init; } = string.Empty;

    public int? FilesChanged { get; init; }

    public long Timestamp { get; init; }

    [JsonIgnore]
    public DateTime TimestampUtc => DateTimeOffset.FromUnixTimeMilliseconds(Timestamp).UtcDateTime;

    [JsonIgnore]
    public string EffectiveEventName => string.IsNullOrWhiteSpace(EventName) ? "Unknown" : EventName;

    [JsonIgnore]
    public string EffectiveSessionId => string.IsNullOrWhiteSpace(SessionId)
        ? $"{Source}:unknown"
        : SessionId;
}
