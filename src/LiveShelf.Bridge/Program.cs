using System.IO.Pipes;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;

const string PipeName = "LiveShelfAgentEvents";

try
{
    var source = ReadSource(args);
    var input = await Console.In.ReadToEndAsync();
    if (string.IsNullOrWhiteSpace(input))
    {
        input = ReadPayloadArgument(args);
    }

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

static string ReadPayloadArgument(string[] args)
{
    for (var i = 0; i < args.Length; i++)
    {
        if (string.Equals(args[i], "--source", StringComparison.OrdinalIgnoreCase))
        {
            i++;
            continue;
        }

        if (!string.IsNullOrWhiteSpace(args[i]))
        {
            return args[i];
        }
    }

    return string.Empty;
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

    var eventName = FirstString(raw, "hook_event_name", "hookEventName", "event_name", "eventName", "event", "type") ?? "Unknown";
    var sessionId =
        FirstString(raw, "session_id", "sessionId") ??
        FirstString(raw, "conversation_id", "conversationId", "thread_id", "threadId", "thread-id") ??
        FirstString(raw, "transcript_path", "transcriptPath") ??
        "unknown";

    return new JsonObject
    {
        ["source"] = source,
        ["sessionId"] = sessionId,
        ["processId"] = Environment.ProcessId,
        ["parentProcessIds"] = ToJsonArray(GetParentProcessIds(Environment.ProcessId)),
        ["foregroundProcessId"] = GetForegroundProcessId(),
        ["turnId"] = FirstString(raw, "turn_id", "turnId", "turn-id", "submission_id", "submissionId", "submission-id"),
        ["eventName"] = eventName,
        ["cwd"] = FirstString(raw, "cwd"),
        ["transcriptPath"] = FirstString(raw, "transcript_path", "transcriptPath"),
        ["toolName"] = FirstString(raw, "tool_name", "toolName", "tool_slug", "toolSlug", "tool"),
        ["toolUseId"] = FirstString(raw, "tool_use_id", "toolUseId", "call_id", "callId"),
        ["toolInput"] = CloneNode(raw, "tool_input", "toolInput", "tool_args", "toolArgs", "arguments", "args", "input"),
        ["toolResponse"] = CloneNode(raw, "tool_response", "toolResponse", "tool_result", "toolResult", "result", "response", "output"),
        ["lastAssistantMessage"] = FirstString(raw, "last_assistant_message", "lastAssistantMessage", "last-assistant-message"),
        ["error"] = FirstString(raw, "error", "message", "stderr", "failure"),
        ["filePath"] = FirstString(raw, "file_path", "filePath", "filename", "path"),
        ["filesChanged"] = FirstInt(raw, "files_changed", "filesChanged", "changed_files_count", "changedFilesCount"),
        ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
    };
}

static JsonArray ToJsonArray(IEnumerable<int> values)
{
    var array = new JsonArray();
    foreach (var value in values)
    {
        array.Add(value);
    }

    return array;
}

static IReadOnlyList<int> GetParentProcessIds(int processId)
{
    var result = new List<int>();
    var seen = new HashSet<int>();
    var current = processId;
    for (var depth = 0; depth < 16; depth++)
    {
        var parent = GetParentProcessId(current);
        if (parent <= 0 || !seen.Add(parent))
        {
            break;
        }

        result.Add(parent);
        current = parent;
    }

    return result;
}

static int GetParentProcessId(int processId)
{
    try
    {
        using var process = Process.GetProcessById(processId);
        var info = new PROCESS_BASIC_INFORMATION();
        var result = NtQueryInformationProcess(
            process.Handle,
            0,
            ref info,
            Marshal.SizeOf<PROCESS_BASIC_INFORMATION>(),
            out _);
        return result == 0 ? info.InheritedFromUniqueProcessId.ToInt32() : 0;
    }
    catch
    {
        return 0;
    }
}

static int GetForegroundProcessId()
{
    try
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero)
        {
            return 0;
        }

        GetWindowThreadProcessId(hwnd, out var processId);
        return unchecked((int)processId);
    }
    catch
    {
        return 0;
    }
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
            var text = NodeToString(node);
            if (!string.IsNullOrWhiteSpace(text))
            {
                return text;
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
            NodeToInt(node) is int number)
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
    if (TryGetDirectProperty(root, name, out value))
    {
        return true;
    }

    return TryFindProperty(root, name, depth: 0, maxDepth: 8, out value);
}

static bool TryGetDirectProperty(JsonObject root, string name, out JsonNode? value)
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

static bool TryFindProperty(JsonNode? node, string name, int depth, int maxDepth, out JsonNode? value)
{
    if (depth > maxDepth)
    {
        value = null;
        return false;
    }

    if (node is JsonObject obj)
    {
        foreach (var pair in obj)
        {
            if (string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase))
            {
                value = pair.Value;
                return true;
            }
        }

        foreach (var pair in obj)
        {
            if (TryFindProperty(pair.Value, name, depth + 1, maxDepth, out value))
            {
                return true;
            }
        }
    }
    else if (node is JsonArray array)
    {
        foreach (var item in array)
        {
            if (TryFindProperty(item, name, depth + 1, maxDepth, out value))
            {
                return true;
            }
        }
    }

    value = null;
    return false;
}

static string? NodeToString(JsonNode? node)
{
    if (node is JsonValue value)
    {
        if (value.TryGetValue<string>(out var text))
        {
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }

        var serializedValue = value.ToJsonString();
        return string.IsNullOrWhiteSpace(serializedValue) || serializedValue == "null" ? null : serializedValue;
    }

    if (node is JsonObject obj && TryGetDirectProperty(obj, "name", out var named))
    {
        return NodeToString(named);
    }

    return null;
}

static int? NodeToInt(JsonNode? node)
{
    if (node is not JsonValue value)
    {
        return null;
    }

    if (value.TryGetValue<int>(out var number))
    {
        return number;
    }

    return value.TryGetValue<string>(out var text) && int.TryParse(text, out var parsed)
        ? parsed
        : null;
}

[DllImport("ntdll.dll")]
static extern int NtQueryInformationProcess(
    IntPtr processHandle,
    int processInformationClass,
    ref PROCESS_BASIC_INFORMATION processInformation,
    int processInformationLength,
    out int returnLength);

[DllImport("user32.dll")]
static extern IntPtr GetForegroundWindow();

[DllImport("user32.dll")]
static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

[StructLayout(LayoutKind.Sequential)]
struct PROCESS_BASIC_INFORMATION
{
    public IntPtr Reserved1;
    public IntPtr PebBaseAddress;
    public IntPtr Reserved2A;
    public IntPtr Reserved2B;
    public IntPtr UniqueProcessId;
    public IntPtr InheritedFromUniqueProcessId;
}
