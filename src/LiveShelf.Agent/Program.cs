using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

const string PipeName = "LiveShelfAgentEvents";

var source = args.Length > 0 ? NormalizeSource(args[0]) : string.Empty;
if (source is not ("codex" or "claude"))
{
    Console.Error.WriteLine("Usage: liveshelf-agent codex|claude [agent args...]");
    return 2;
}

var token = Guid.NewGuid().ToString("N");
var cwd = Environment.CurrentDirectory;
var terminalSession = FirstNonEmpty(
    Environment.GetEnvironmentVariable("LIVESHELF_TERMINAL_SESSION"),
    Environment.GetEnvironmentVariable("WT_SESSION"));
var parentProcessId = GetParentProcessId(Environment.ProcessId);
var foregroundProcessId = GetForegroundProcessId();

await PublishStartEventAsync(source, token, cwd, terminalSession, parentProcessId, foregroundProcessId);

var command = ResolveCommand(source == "codex" ? "codex" : "claude");
var childArgs = args.Skip(1).ToArray();
var startInfo = CreateAgentStartInfo(command, childArgs, cwd);

startInfo.Environment["LIVESHELF_AGENT_TOKEN"] = token;
startInfo.Environment["LIVESHELF_AGENT_SOURCE"] = source;
startInfo.Environment["LIVESHELF_LAUNCH_CWD"] = cwd;
startInfo.Environment["LIVESHELF_PARENT_PROCESS_ID"] = parentProcessId.ToString();
if (!string.IsNullOrWhiteSpace(terminalSession))
{
    startInfo.Environment["LIVESHELF_TERMINAL_SESSION"] = terminalSession;
}

using var child = Process.Start(startInfo);
if (child is null)
{
    Console.Error.WriteLine($"Failed to start {command}.");
    return 1;
}

child.WaitForExit();
return child.ExitCode;

static ProcessStartInfo CreateAgentStartInfo(string command, string[] args, string cwd)
{
    var extension = Path.GetExtension(command);
    var startInfo = string.Equals(extension, ".cmd", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(extension, ".bat", StringComparison.OrdinalIgnoreCase)
        ? new ProcessStartInfo(Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe")
        : new ProcessStartInfo(command);

    startInfo.UseShellExecute = false;
    startInfo.RedirectStandardInput = false;
    startInfo.RedirectStandardOutput = false;
    startInfo.RedirectStandardError = false;
    startInfo.WorkingDirectory = cwd;

    if (string.Equals(extension, ".cmd", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(extension, ".bat", StringComparison.OrdinalIgnoreCase))
    {
        startInfo.ArgumentList.Add("/d");
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add("call");
        startInfo.ArgumentList.Add(command);
    }

    foreach (var arg in args)
    {
        startInfo.ArgumentList.Add(arg);
    }

    return startInfo;
}

static string ResolveCommand(string command)
{
    if (Path.IsPathFullyQualified(command) && File.Exists(command))
    {
        return command;
    }

    var paths = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
        .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    var extensions = (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT")
        .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    var candidates = Path.HasExtension(command)
        ? paths.Select(path => Path.Combine(path, command))
        : paths.SelectMany(path => extensions.Select(extension => Path.Combine(path, command + extension.ToLowerInvariant())));

    return candidates.FirstOrDefault(File.Exists) ?? command;
}

static async Task PublishStartEventAsync(
    string source,
    string token,
    string cwd,
    string? terminalSession,
    int parentProcessId,
    int foregroundProcessId)
{
    var payload = new JsonObject
    {
        ["source"] = source,
        ["sessionId"] = $"liveshelf-pending-{token}",
        ["eventName"] = "LiveShelfTrackedAgentStart",
        ["liveShelfAgentToken"] = token,
        ["terminalSessionId"] = terminalSession,
        ["launchCwd"] = cwd,
        ["cwd"] = cwd,
        ["processId"] = Environment.ProcessId,
        ["parentProcessIds"] = ToJsonArray(GetParentProcessIds(Environment.ProcessId)),
        ["foregroundProcessId"] = foregroundProcessId,
        ["liveShelfParentProcessId"] = parentProcessId,
        ["termProgram"] = Environment.GetEnvironmentVariable("TERM_PROGRAM"),
        ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
    }.ToJsonString(JsonOptions.Value);

    if (!await TrySendToLiveShelfAsync(payload))
    {
        QueueEvent(payload);
    }
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
        await pipe.ConnectAsync(1500);
        await using var writer = new StreamWriter(pipe);
        await writer.WriteLineAsync(payload);
        await writer.FlushAsync();
        return true;
    }
    catch
    {
        return false;
    }
}

static void QueueEvent(string payload)
{
    try
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LiveShelf");
        Directory.CreateDirectory(directory);
        File.AppendAllText(Path.Combine(directory, "queued-agent-events.jsonl"), payload + Environment.NewLine);
    }
    catch
    {
    }
}

static string? FirstNonEmpty(params string?[] values) =>
    values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

static string NormalizeSource(string value)
{
    value = value.Trim().ToLowerInvariant();
    if (value.Contains("codex", StringComparison.Ordinal))
    {
        return "codex";
    }

    return value.Contains("claude", StringComparison.Ordinal) ? "claude" : value;
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

static class JsonOptions
{
    public static readonly JsonSerializerOptions Value = new()
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        WriteIndented = false
    };
}
