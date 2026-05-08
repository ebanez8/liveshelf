using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Nodes;

const string PipeName = "LiveShelfAgentEvents";

try
{
    var source = ReadSource(args);
    var input = await Console.In.ReadToEndAsync();
    var normalized = NormalizeEvent(source, input);
    var payload = normalized.ToJsonString(new JsonSerializerOptions { WriteIndented = false });

    if (!await TrySendToLiveShelfAsync(payload))
    {
        QueueEvent(payload);
    }
}
catch (Exception ex)
{
    TryWriteDiagnostic(ex);
}

Environment.ExitCode = 0;

static string ReadSource(string[] args)
{
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (string.Equals(args[i], "--source", StringComparison.OrdinalIgnoreCase))
        {
            return NormalizeSource(args[i + 1]);
        }
    }

    return "agent";
}

static JsonObject NormalizeEvent(string source, string input)
{
    JsonObject raw;
    try
    {
        raw = JsonNode.Parse(string.IsNullOrWhiteSpace(input) ? "{}" : input) as JsonObject ?? [];
    }
    catch (JsonException)
    {
        raw = [];
    }

    var eventName = FirstString(raw, "hook_event_name", "hookEventName", "eventName", "event") ?? "Unknown";
    var sessionId =
        FirstString(raw, "session_id", "sessionId") ??
        FirstString(raw, "transcript_path", "transcriptPath") ??
        FirstString(raw, "cwd") ??
        "unknown";

    return new JsonObject
    {
        ["source"] = source,
        ["sessionId"] = sessionId,
        ["turnId"] = FirstString(raw, "turn_id", "turnId"),
        ["eventName"] = eventName,
        ["cwd"] = FirstString(raw, "cwd"),
        ["transcriptPath"] = FirstString(raw, "transcript_path", "transcriptPath"),
        ["toolName"] = FirstString(raw, "tool_name", "toolName", "tool"),
        ["toolUseId"] = FirstString(raw, "tool_use_id", "toolUseId"),
        ["toolInput"] = CloneNode(raw, "tool_input", "toolInput", "input"),
        ["toolResponse"] = CloneNode(raw, "tool_response", "toolResponse", "response"),
        ["lastAssistantMessage"] = FirstString(raw, "last_assistant_message", "lastAssistantMessage"),
        ["error"] = FirstString(raw, "error", "message"),
        ["filePath"] = FirstString(raw, "file_path", "filePath", "path"),
        ["filesChanged"] = FirstInt(raw, "files_changed", "filesChanged"),
        ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
    };
}

static async Task<bool> TrySendToLiveShelfAsync(string payload)
{
    try
    {
        await using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(300);
        await using var writer = new StreamWriter(pipe);
        await writer.WriteLineAsync(payload);
        await writer.FlushAsync();
        return true;
    }
    catch (TimeoutException)
    {
        return false;
    }
    catch (IOException)
    {
        return false;
    }
    catch (UnauthorizedAccessException)
    {
        return false;
    }
    catch (Exception)
    {
        return false;
    }
}

static void QueueEvent(string payload)
{
    try
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var directory = Path.Combine(localAppData, "LiveShelf");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "queued-agent-events.jsonl");
        File.AppendAllText(path, payload + Environment.NewLine);
    }
    catch (IOException)
    {
    }
    catch (UnauthorizedAccessException)
    {
    }
    catch (Exception)
    {
    }
}

static void TryWriteDiagnostic(Exception exception)
{
    try
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var directory = Path.Combine(localAppData, "LiveShelf");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "bridge-errors.log");
        File.AppendAllText(
            path,
            $"{DateTimeOffset.UtcNow:O} {exception.GetType().Name}: {exception.Message}{Environment.NewLine}");
    }
    catch
    {
    }
}

static string NormalizeSource(string value)
{
    value = value.Trim().ToLowerInvariant();
    if (value.Contains("codex", StringComparison.Ordinal))
    {
        return "codex";
    }

    return value.Contains("claude", StringComparison.Ordinal) ? "claude" : value;
}

static string? FirstString(JsonObject root, params string[] names)
{
    foreach (var name in names)
    {
        if (TryGetProperty(root, name, out var node))
        {
            if (node is JsonValue value &&
                value.TryGetValue<string>(out var text) &&
                !string.IsNullOrWhiteSpace(text))
            {
                return text;
            }

            var serialized = node?.ToJsonString();
            if (!string.IsNullOrWhiteSpace(serialized) && serialized != "null")
            {
                return serialized;
            }
        }
    }

    return null;
}

static int? FirstInt(JsonObject root, params string[] names)
{
    foreach (var name in names)
    {
        if (TryGetProperty(root, name, out var node) &&
            node is JsonValue value &&
            value.TryGetValue<int>(out var number))
        {
            return number;
        }
    }

    return null;
}

static JsonNode? CloneNode(JsonObject root, params string[] names)
{
    foreach (var name in names)
    {
        if (TryGetProperty(root, name, out var node))
        {
            return node?.DeepClone();
        }
    }

    return null;
}

static bool TryGetProperty(JsonObject root, string name, out JsonNode? value)
{
    if (root.TryGetPropertyValue(name, out value))
    {
        return true;
    }

    foreach (var pair in root)
    {
        if (string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase))
        {
            value = pair.Value;
            return true;
        }
    }

    value = null;
    return false;
}
