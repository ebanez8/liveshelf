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

        return new AgentHookInstallResult(true, $"Codex tracking enabled using {bridgePath}");
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

        return new AgentHookInstallResult(true, $"Claude tracking enabled using {bridgePath}");
    }

    public static AgentHookInstallResult EnableAllTracking()
    {
        var codex = EnableCodexTracking();
        var claude = EnableClaudeTracking();
        return codex.Success && claude.Success
            ? new AgentHookInstallResult(true, "Codex and Claude tracking enabled")
            : new AgentHookInstallResult(false, $"{codex.Message}; {claude.Message}");
    }

    private static string EnsureBridgeInstalled()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var installDirectory = Path.Combine(localAppData, "LiveShelf");
        Directory.CreateDirectory(installDirectory);

        var targetPath = Path.Combine(installDirectory, BridgeFileName);
        var sourcePath = Path.Combine(AppContext.BaseDirectory, BridgeFileName);
        if (File.Exists(sourcePath))
        {
            foreach (var bridgeFile in Directory.EnumerateFiles(AppContext.BaseDirectory, "liveshelf-bridge.*"))
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

        throw new FileNotFoundException("Live Shelf bridge executable was not found next to the app.", sourcePath);
    }

    private static string BuildBridgeCommand(string bridgePath, string source)
    {
        return $"\"{bridgePath}\" --source {source}";
    }

    private static void EnsureCodexHooksFeature(string configPath)
    {
        var text = File.Exists(configPath) ? File.ReadAllText(configPath) : string.Empty;
        if (text.Contains("codex_hooks", StringComparison.OrdinalIgnoreCase))
        {
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
        return new JsonObject
        {
            ["hooks"] = new JsonObject
            {
                ["UserPromptSubmit"] = HookArray(command),
                ["PreToolUse"] = HookArray(command, "Bash|apply_patch|Edit|Write"),
                ["PostToolUse"] = HookArray(command, "Bash|apply_patch|Edit|Write"),
                ["PermissionRequest"] = HookArray(command, "Bash|apply_patch|Edit|Write"),
                ["Stop"] = HookArray(command)
            }
        };
    }

    private static JsonObject BuildClaudeHooks(string command)
    {
        return new JsonObject
        {
            ["hooks"] = new JsonObject
            {
                ["UserPromptSubmit"] = HookArray(command),
                ["PreToolUse"] = HookArray(command, "Bash|Edit|Write"),
                ["PostToolUse"] = HookArray(command, "Bash|Edit|Write"),
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
}

internal readonly record struct AgentHookInstallResult(
    bool Success,
    string Message);
