using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LiveShelf;

internal static class AgentHookInstaller
{
    private const string BridgeFileName = "liveshelf-bridge.exe";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public static AgentHookInstallResult EnableCodexTracking()
    {
        var bridgePath = EnsureBridgeInstalled();
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var codexDirectory = Path.Combine(profile, ".codex");
        Directory.CreateDirectory(codexDirectory);

        EnsureCodexHooksFeature(Path.Combine(codexDirectory, "config.toml"));
        WriteHookConfig(
            Path.Combine(codexDirectory, "hooks.json"),
            BuildCodexHooks(BuildBridgeCommand(bridgePath, "codex")));

        var test = TestBridge(bridgePath, "codex");
        return test.Success
            ? new AgentHookInstallResult(true, $"Codex tracking enabled and tested using {bridgePath}")
            : test;
    }

    public static AgentHookInstallResult EnableClaudeTracking()
    {
        var bridgePath = EnsureBridgeInstalled();
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var claudeDirectory = Path.Combine(profile, ".claude");
        Directory.CreateDirectory(claudeDirectory);

        WriteHookConfig(
            Path.Combine(claudeDirectory, "settings.json"),
            BuildClaudeHooks(BuildBridgeCommand(bridgePath, "claude")));

        var test = TestBridge(bridgePath, "claude");
        return test.Success
            ? new AgentHookInstallResult(true, $"Claude tracking enabled and tested using {bridgePath}")
            : test;
    }

    public static AgentHookInstallResult EnableAllTracking()
    {
        var codex = EnableCodexTracking();
        var claude = EnableClaudeTracking();
        return codex.Success && claude.Success
            ? new AgentHookInstallResult(true, "Codex and Claude tracking enabled")
            : new AgentHookInstallResult(false, $"{codex.Message}; {claude.Message}");
    }

    public static AgentHookInstallResult TestCodexTracking()
    {
        return TestBridge(EnsureBridgeInstalled(), "codex");
    }

    public static AgentHookInstallResult TestClaudeTracking()
    {
        return TestBridge(EnsureBridgeInstalled(), "claude");
    }

    private static string EnsureBridgeInstalled()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var installDirectory = Path.Combine(localAppData, "LiveShelf");
        Directory.CreateDirectory(installDirectory);

        var targetPath = Path.Combine(installDirectory, BridgeFileName);
        var sourceDirectory = FindBridgeSourceDirectory();
        if (sourceDirectory is not null)
        {
            foreach (var bridgeFile in Directory.EnumerateFiles(sourceDirectory, "liveshelf-bridge.*"))
            {
                File.Copy(
                    bridgeFile,
                    Path.Combine(installDirectory, Path.GetFileName(bridgeFile)),
                    overwrite: true);
            }

            return targetPath;
        }

        if (File.Exists(targetPath))
        {
            return targetPath;
        }

        throw new FileNotFoundException(
            "Live Shelf bridge executable was not found. Build the app once, then try connecting again.",
            Path.Combine(AppContext.BaseDirectory, BridgeFileName));
    }

    private static string? FindBridgeSourceDirectory()
    {
        if (File.Exists(Path.Combine(AppContext.BaseDirectory, BridgeFileName)))
        {
            return AppContext.BaseDirectory;
        }

        var current = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; current is not null && depth < 8; depth++, current = current.Parent)
        {
            foreach (var candidate in new[]
                     {
                         Path.Combine(current.FullName, "src", "LiveShelf.Bridge", "bin", "Debug", "net8.0"),
                         Path.Combine(current.FullName, "src", "LiveShelf.Bridge", "bin", "Release", "net8.0"),
                         Path.Combine(current.FullName, "src", "LiveShelf.Bridge", "bin", "Scratch"),
                         Path.Combine(current.FullName, "src", "LiveShelf", "bin", "Scratch")
                     })
            {
                if (File.Exists(Path.Combine(candidate, BridgeFileName)))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private static string BuildBridgeCommand(string bridgePath, string source)
    {
        return $"\"{bridgePath}\" --source {source}";
    }

    private static void EnsureCodexHooksFeature(string configPath)
    {
        var text = File.Exists(configPath) ? File.ReadAllText(configPath) : string.Empty;
        var existingHooksSetting = Regex.Match(
            text,
            @"(?im)^(\s*codex_hooks\s*=\s*)(?:true|false)(\s*(?:#.*)?)$");
        if (existingHooksSetting.Success)
        {
            text = Regex.Replace(
                text,
                @"(?im)^(\s*codex_hooks\s*=\s*)(?:true|false)(\s*(?:#.*)?)$",
                "$1true$2");
            File.WriteAllText(configPath, text);
            return;
        }

        var featuresMatch = Regex.Match(text, @"(?m)^\s*\[features\]\s*$");
        if (featuresMatch.Success)
        {
            var insertAt = featuresMatch.Index + featuresMatch.Length;
            text = text.Insert(insertAt, Environment.NewLine + "codex_hooks = true");
            File.WriteAllText(configPath, text);
            return;
        }

        if (text.Length > 0 && !text.EndsWith(Environment.NewLine, StringComparison.Ordinal))
        {
            text += Environment.NewLine;
        }

        text += "[features]" + Environment.NewLine;
        text += "codex_hooks = true" + Environment.NewLine;
        File.WriteAllText(configPath, text);
    }

    private static void WriteHookConfig(string path, JsonObject desiredHooks)
    {
        JsonObject root;
        if (File.Exists(path))
        {
            try
            {
                root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? [];
            }
            catch (JsonException)
            {
                var backupPath = $"{path}.liveshelf-backup";
                File.Copy(path, backupPath, overwrite: true);
                root = [];
            }
        }
        else
        {
            root = [];
        }

        MergeHookConfig(root, desiredHooks);
        File.WriteAllText(path, root.ToJsonString(JsonOptions));
    }

    private static void MergeHookConfig(JsonObject root, JsonObject desiredRoot)
    {
        var hooks = root["hooks"] as JsonObject;
        if (hooks is null)
        {
            hooks = [];
            root["hooks"] = hooks;
        }

        var desiredHooks = desiredRoot["hooks"] as JsonObject;
        if (desiredHooks is null)
        {
            return;
        }

        foreach (var desiredEvent in desiredHooks)
        {
            var mergedEntries = new JsonArray();
            if (hooks[desiredEvent.Key] is JsonArray existingEntries)
            {
                foreach (var existingEntry in existingEntries)
                {
                    if (!ContainsLiveShelfBridge(existingEntry))
                    {
                        mergedEntries.Add(existingEntry?.DeepClone());
                    }
                }
            }

            if (desiredEvent.Value is JsonArray desiredEntries)
            {
                foreach (var desiredEntry in desiredEntries)
                {
                    mergedEntries.Add(desiredEntry?.DeepClone());
                }
            }

            hooks[desiredEvent.Key] = mergedEntries;
        }
    }

    private static bool ContainsLiveShelfBridge(JsonNode? node)
    {
        return node?.ToJsonString().Contains(BridgeFileName, StringComparison.OrdinalIgnoreCase) == true;
    }

    private static JsonObject BuildCodexHooks(string command)
    {
        const string toolMatcher =
            "Bash|Shell|shell_command|functions.shell_command|PowerShell|Cmd|apply_patch|functions.apply_patch|Edit|Write";

        return new JsonObject
        {
            ["hooks"] = new JsonObject
            {
                ["UserPromptSubmit"] = HookArray(command),
                ["PreToolUse"] = HookArray(command, toolMatcher),
                ["PostToolUse"] = HookArray(command, toolMatcher),
                ["PermissionRequest"] = HookArray(command, toolMatcher),
                ["Stop"] = HookArray(command)
            }
        };
    }

    private static JsonObject BuildClaudeHooks(string command)
    {
        const string toolMatcher = "Bash|Shell|shell_command|PowerShell|Cmd|Edit|Write";

        return new JsonObject
        {
            ["hooks"] = new JsonObject
            {
                ["UserPromptSubmit"] = HookArray(command),
                ["PreToolUse"] = HookArray(command, toolMatcher),
                ["PostToolUse"] = HookArray(command, toolMatcher),
                ["PermissionRequest"] = HookArray(command),
                ["Notification"] = HookArray(command),
                ["Stop"] = HookArray(command),
                ["StopFailure"] = HookArray(command)
            }
        };
    }

    private static JsonArray HookArray(string command, string matcher = "")
    {
        var entry = new JsonObject
        {
            ["hooks"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "command",
                    ["command"] = command
                }
            }
        };

        if (!string.IsNullOrWhiteSpace(matcher))
        {
            entry["matcher"] = matcher;
        }

        return new JsonArray(entry);
    }

    private static AgentHookInstallResult TestBridge(string bridgePath, string source)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = bridgePath,
                Arguments = $"--source {source}",
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return new AgentHookInstallResult(false, $"Could not start {source} bridge test");
            }

            var payload = new JsonObject
            {
                ["hook_event_name"] = "LiveShelfHookTest",
                ["session_id"] = $"liveshelf-hook-test-{Guid.NewGuid():N}",
                ["cwd"] = Environment.CurrentDirectory
            }.ToJsonString(new JsonSerializerOptions { WriteIndented = false });

            process.StandardInput.Write(payload);
            process.StandardInput.Close();

            if (!process.WaitForExit(3000))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                }

                return new AgentHookInstallResult(false, $"{FormatSourceName(source)} bridge test timed out");
            }

            return process.ExitCode == 0
                ? new AgentHookInstallResult(true, $"{FormatSourceName(source)} bridge test passed")
                : new AgentHookInstallResult(false, $"{FormatSourceName(source)} bridge exited with code {process.ExitCode}");
        }
        catch (Exception ex)
        {
            return new AgentHookInstallResult(false, $"{FormatSourceName(source)} bridge test failed: {ex.Message}");
        }
    }

    private static string FormatSourceName(string source)
    {
        return source.Equals("codex", StringComparison.OrdinalIgnoreCase)
            ? "Codex"
            : "Claude";
    }
}

internal readonly record struct AgentHookInstallResult(
    bool Success,
    string Message);
