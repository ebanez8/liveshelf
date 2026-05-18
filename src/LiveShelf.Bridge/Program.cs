using System.IO.Pipes;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

const string PipeName = "LiveShelfAgentEvents";
const string BridgeVersion = "1.1.0-hookhealth";

if (args.Length > 0 && string.Equals(args[0], "--self-test", StringComparison.OrdinalIgnoreCase))
{
    var ok = await RunSelfTestAsync();
    Environment.ExitCode = ok ? 0 : 3;
    return;
}

var bridgeStart = DateTimeOffset.UtcNow;
var bridgeSource = ReadSource(args);
string rawInput = string.Empty;
string? parseError = null;
JsonObject? parsedRaw = null;
string pipeStatus = "not-attempted";
string queueStatus = "not-attempted";

try
{
    rawInput = await TryReadStdinAsync();
    if (string.IsNullOrWhiteSpace(rawInput))
    {
        rawInput = ReadPayloadArgument(args);
    }

    if (!string.IsNullOrWhiteSpace(rawInput))
    {
        try
        {
            parsedRaw = JsonNode.Parse(rawInput) as JsonObject;
            if (parsedRaw is null)
            {
                parseError = "not-a-json-object";
            }
        }
        catch (JsonException ex)
        {
            parseError = ex.Message;
        }
    }

    var normalized = NormalizeEvent(bridgeSource, parsedRaw);
    var payload = normalized.ToJsonString(BridgeJsonOptions.Value);

    var sendResult = await TrySendToLiveShelfAsync(payload);
    pipeStatus = sendResult.Status;
    if (!sendResult.Success)
    {
        queueStatus = QueueEvent(payload);
    }
    else
    {
        queueStatus = "skipped";
    }

    LogRawHook(bridgeStart, bridgeSource, args, rawInput, parsedRaw, parseError, payload, pipeStatus, queueStatus);
}
catch (Exception ex)
{
    TryWriteDiagnostic(ex);
    try
    {
        var fallback = new JsonObject
        {
            ["source"] = bridgeSource,
            ["eventName"] = "BridgeError",
            ["error"] = ex.Message,
            ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };
        queueStatus = QueueEvent(fallback.ToJsonString(BridgeJsonOptions.Value));
        LogRawHook(bridgeStart, bridgeSource, args, rawInput, parsedRaw, parseError ?? ex.GetType().Name, fallback.ToJsonString(BridgeJsonOptions.Value), pipeStatus, queueStatus);
    }
    catch
    {
    }
}

Environment.ExitCode = 0;
return;

static async Task<string> TryReadStdinAsync()
{
    if (!Console.IsInputRedirected)
    {
        return string.Empty;
    }

    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    try
    {
        return await Console.In.ReadToEndAsync(cts.Token);
    }
    catch (OperationCanceledException)
    {
        return string.Empty;
    }
    catch (IOException)
    {
        return string.Empty;
    }
}

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

static JsonObject NormalizeEvent(string source, JsonObject? rawObject)
{
    var raw = rawObject ?? new JsonObject();

    var eventName = FirstString(raw, "hook_event_name", "hookEventName", "event_name", "eventName", "event", "type") ?? "Unknown";
    var sessionId =
        FirstString(raw, "session_id", "sessionId") ??
        FirstString(raw, "conversation_id", "conversationId", "thread_id", "threadId", "thread-id") ??
        FirstString(raw, "transcript_path", "transcriptPath") ??
        "unknown";

    var envSource = Environment.GetEnvironmentVariable("LIVESHELF_AGENT_SOURCE");
    var liveShelfSource = string.IsNullOrWhiteSpace(envSource)
        ? source
        : NormalizeSource(envSource);
    var launchCwd = Environment.GetEnvironmentVariable("LIVESHELF_LAUNCH_CWD");
    var terminalSession = FirstNonEmpty(
        Environment.GetEnvironmentVariable("LIVESHELF_TERMINAL_SESSION"),
        Environment.GetEnvironmentVariable("WT_SESSION"));

    return new JsonObject
    {
        ["source"] = liveShelfSource,
        ["liveShelfAgentToken"] = Environment.GetEnvironmentVariable("LIVESHELF_AGENT_TOKEN"),
        ["terminalSessionId"] = terminalSession,
        ["launchCwd"] = launchCwd,
        ["liveShelfParentProcessId"] = FirstIntString(Environment.GetEnvironmentVariable("LIVESHELF_PARENT_PROCESS_ID")),
        ["termProgram"] = Environment.GetEnvironmentVariable("TERM_PROGRAM"),
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

static string? FirstNonEmpty(params string?[] values)
{
    foreach (var value in values)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            return value;
        }
    }

    return null;
}

static int? FirstIntString(string? value)
{
    return int.TryParse(value, out var parsed) ? parsed : null;
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
    var seen = new HashSet<int> { processId };
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

static async Task<PipeSendResult> TrySendToLiveShelfAsync(string payload)
{
    try
    {
        await using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(1500);
        await using var writer = new StreamWriter(pipe);
        await writer.WriteLineAsync(payload);
        await writer.FlushAsync();
        return new PipeSendResult(true, "delivered");
    }
    catch (TimeoutException)
    {
        return new PipeSendResult(false, "timeout");
    }
    catch (IOException ex)
    {
        return new PipeSendResult(false, $"io:{ex.Message}");
    }
    catch (UnauthorizedAccessException)
    {
        return new PipeSendResult(false, "unauthorized");
    }
    catch (Exception ex)
    {
        return new PipeSendResult(false, $"error:{ex.GetType().Name}");
    }
}

static string QueueEvent(string payload)
{
    try
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var directory = Path.Combine(localAppData, "LiveShelf");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "queued-agent-events.jsonl");
        File.AppendAllText(path, payload + Environment.NewLine);
        return "queued";
    }
    catch (IOException ex)
    {
        return $"queue-io-error:{ex.Message}";
    }
    catch (UnauthorizedAccessException)
    {
        return "queue-unauthorized";
    }
    catch (Exception ex)
    {
        return $"queue-error:{ex.GetType().Name}";
    }
}

static void LogRawHook(
    DateTimeOffset startedAt,
    string source,
    string[] argv,
    string rawInput,
    JsonObject? parsedRaw,
    string? parseError,
    string normalizedPayload,
    string pipeStatus,
    string queueStatus)
{
    try
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var logDir = Path.Combine(localAppData, "LiveShelf", "logs");
        Directory.CreateDirectory(logDir);
        var path = Path.Combine(logDir, "raw-hooks.jsonl");

        var entry = new JsonObject
        {
            ["timestamp"] = startedAt.ToString("O"),
            ["source"] = source,
            ["bridgeVersion"] = BridgeVersion,
            ["argv"] = ToJsonNodeArray(argv.Select(a => (JsonNode?)JsonValue.Create(a))),
            ["cwd"] = Environment.CurrentDirectory,
            ["stdinLength"] = rawInput?.Length ?? 0,
            ["raw"] = parsedRaw?.DeepClone(),
            ["parseError"] = parseError,
            ["normalized"] = JsonNode.Parse(normalizedPayload),
            ["pipeStatus"] = pipeStatus,
            ["queueStatus"] = queueStatus,
            ["liveShelfAgentToken"] = Environment.GetEnvironmentVariable("LIVESHELF_AGENT_TOKEN"),
            ["wtSession"] = Environment.GetEnvironmentVariable("WT_SESSION"),
            ["termProgram"] = Environment.GetEnvironmentVariable("TERM_PROGRAM"),
            ["processId"] = Environment.ProcessId
        };

        File.AppendAllText(path, entry.ToJsonString(BridgeJsonOptions.Value) + Environment.NewLine);

        TruncateIfTooLarge(path, 4 * 1024 * 1024);
    }
    catch
    {
    }
}

static void TruncateIfTooLarge(string path, long maxBytes)
{
    try
    {
        var info = new FileInfo(path);
        if (info.Exists && info.Length > maxBytes)
        {
            var lines = File.ReadAllLines(path);
            File.WriteAllLines(path, lines.Skip(Math.Max(0, lines.Length - 500)));
        }
    }
    catch
    {
    }
}

static JsonArray ToJsonNodeArray(IEnumerable<JsonNode?> values)
{
    var array = new JsonArray();
    foreach (var value in values)
    {
        array.Add(value);
    }

    return array;
}

static async Task<bool> RunSelfTestAsync()
{
    var startedAt = DateTimeOffset.UtcNow;
    var synthetic = new JsonObject
    {
        ["source"] = "self-test",
        ["sessionId"] = $"self-test-{Guid.NewGuid():N}",
        ["eventName"] = "BridgeSelfTest",
        ["timestamp"] = startedAt.ToUnixTimeMilliseconds(),
        ["bridgeVersion"] = BridgeVersion
    };

    var payload = synthetic.ToJsonString(BridgeJsonOptions.Value);
    var send = await TrySendToLiveShelfAsync(payload);
    var queueStatus = send.Success ? "skipped" : QueueEvent(payload);
    LogRawHook(startedAt, "self-test", new[] { "--self-test" }, payload, synthetic, null, payload, send.Status, queueStatus);
    return send.Success;
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
            $"{DateTimeOffset.UtcNow:O} {exception}{Environment.NewLine}");
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

        var serializedValue = value.ToJsonString(BridgeJsonOptions.Value);
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

readonly record struct PipeSendResult(bool Success, string Status);

static class BridgeJsonOptions
{
    public static readonly JsonSerializerOptions Value = new()
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        WriteIndented = false
    };
}
