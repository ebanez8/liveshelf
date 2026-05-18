using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LiveShelf;

internal static class AgentHookInstaller
{
    private const string BridgeFileName = "liveshelf-bridge.exe";
    private const string AgentFileName = "liveshelf-agent.exe";
    private const string CodexShimFileName = "liveshelf-codex-hook.cmd";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public static AgentHookInstallResult EnableCodexTracking()
    {
        var bridgePath = EnsureBridgeInstalled();
        var shimPath = EnsureCodexHookShim(bridgePath);
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var codexDirectory = Path.Combine(profile, ".codex");
        Directory.CreateDirectory(codexDirectory);

        EnsureCodexConfig(Path.Combine(codexDirectory, "config.toml"), bridgePath);
        WriteHookConfig(
            Path.Combine(codexDirectory, "hooks.json"),
            BuildCodexHooks(BuildCodexShimCommand(shimPath)));

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

    public static AgentTrackingInstallStatus GetInstalledTrackingStatus()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var codexDirectory = Path.Combine(profile, ".codex");
        var claudeDirectory = Path.Combine(profile, ".claude");

        return new AgentTrackingInstallStatus(
            IsCodexTrackingInstalled(
                Path.Combine(codexDirectory, "config.toml"),
                Path.Combine(codexDirectory, "hooks.json")),
            IsHookConfigInstalled(Path.Combine(claudeDirectory, "settings.json"), "claude"));
    }

    private static string EnsureBridgeInstalled()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var installDirectory = Path.Combine(localAppData, "LiveShelf");
        Directory.CreateDirectory(installDirectory);

        var targetPath = Path.Combine(installDirectory, BridgeFileName);
        var bridgeSourceDirectory = FindToolSourceDirectory(BridgeFileName, "LiveShelf.Bridge");
        if (bridgeSourceDirectory is not null)
        {
            foreach (var bridgeFile in Directory.EnumerateFiles(bridgeSourceDirectory, "liveshelf-bridge.*"))
            {
                File.Copy(
                    bridgeFile,
                    Path.Combine(installDirectory, Path.GetFileName(bridgeFile)),
                    overwrite: true);
            }
        }

        var agentSourceDirectory = FindToolSourceDirectory(AgentFileName, "LiveShelf.Agent");
        if (agentSourceDirectory is not null)
        {
            foreach (var agentFile in Directory.EnumerateFiles(agentSourceDirectory, "liveshelf-agent.*"))
            {
                File.Copy(
                    agentFile,
                    Path.Combine(installDirectory, Path.GetFileName(agentFile)),
                    overwrite: true);
            }
        }

        if (File.Exists(targetPath))
        {
            return targetPath;
        }

        throw new FileNotFoundException(
            "Live Shelf bridge executable was not found. Build the app once, then try connecting again.",
            Path.Combine(AppContext.BaseDirectory, BridgeFileName));
    }

    private static string? FindToolSourceDirectory(string fileName, string projectName)
    {
        if (File.Exists(Path.Combine(AppContext.BaseDirectory, fileName)))
        {
            return AppContext.BaseDirectory;
        }

        var current = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; current is not null && depth < 8; depth++, current = current.Parent)
        {
            foreach (var candidate in new[]
                     {
                         Path.Combine(current.FullName, "src", projectName, "bin", "Debug", "net8.0"),
                         Path.Combine(current.FullName, "src", projectName, "bin", "Release", "net8.0"),
                         Path.Combine(current.FullName, "src", projectName, "bin", "Scratch"),
                         Path.Combine(current.FullName, "src", "LiveShelf", "bin", "Scratch")
                     })
            {
                if (File.Exists(Path.Combine(candidate, fileName)))
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

    private static string EnsureCodexHookShim(string bridgePath)
    {
        var directory = Path.GetDirectoryName(bridgePath)
            ?? throw new DirectoryNotFoundException("Live Shelf bridge install directory was not found.");
        Directory.CreateDirectory(directory);

        var shimPath = Path.Combine(directory, CodexShimFileName);
        var content = string.Join(
            Environment.NewLine,
            [
                "@echo off",
                $"\"{bridgePath}\" --source codex %*",
                "exit /b 0",
                string.Empty
            ]);
        File.WriteAllText(shimPath, content, Encoding.ASCII);
        return shimPath;
    }

    private static string BuildCodexShimCommand(string shimPath)
    {
        return $"cmd.exe /d /c call \"{shimPath}\"";
    }

    private static void EnsureCodexHooksFeature(string configPath)
    {
        var text = File.Exists(configPath) ? File.ReadAllText(configPath) : string.Empty;
        var updated = SetFeatureFlag(text, "hooks", "true", removeKeys: ["codex_hooks"]);
        File.WriteAllText(configPath, updated);
    }

    private static void EnsureCodexConfig(string configPath, string bridgePath)
    {
        var text = File.Exists(configPath) ? File.ReadAllText(configPath) : string.Empty;
        var updated = SetFeatureFlag(text, "hooks", "true", removeKeys: ["codex_hooks"]);
        updated = SetTopLevelArray(updated, "notify", [bridgePath, "--source", "codex"]);
        updated = SetSectionKey(updated, "tui", "notification_condition", "\"always\"");
        File.WriteAllText(configPath, updated);
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
        var text = node?.ToJsonString();
        return text?.Contains(BridgeFileName, StringComparison.OrdinalIgnoreCase) == true ||
               text?.Contains(CodexShimFileName, StringComparison.OrdinalIgnoreCase) == true;
    }

    private static bool IsCodexTrackingInstalled(string configPath, string hooksPath)
    {
        var hasHooks = IsHookConfigInstalled(hooksPath, "codex");
        if (!File.Exists(configPath))
        {
            return hasHooks;
        }

        var config = File.ReadAllText(configPath);
        var hasHookFeature = Regex.IsMatch(
            config,
            @"(?im)^\s*hooks\s*=\s*true\s*(?:#.*)?$");
        var hasNotify = config.Contains(BridgeFileName, StringComparison.OrdinalIgnoreCase) &&
                        config.Contains("--source", StringComparison.OrdinalIgnoreCase) &&
                        config.Contains("codex", StringComparison.OrdinalIgnoreCase);

        return hasNotify || (hasHooks && hasHookFeature);
    }

    private static bool IsHookConfigInstalled(string path, string source)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            var text = File.ReadAllText(path);
            return text.Contains(BridgeFileName, StringComparison.OrdinalIgnoreCase) &&
                   text.Contains(source, StringComparison.OrdinalIgnoreCase);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static JsonObject BuildCodexHooks(string command)
    {
        const string toolMatcher = ".*";

        return new JsonObject
        {
            ["hooks"] = new JsonObject
            {
                ["SessionStart"] = HookArray(command, timeoutSeconds: 5),
                ["UserPromptSubmit"] = HookArray(command),
                ["PreToolUse"] = HookArray(command, toolMatcher, timeoutSeconds: 5),
                ["PostToolUse"] = HookArray(command, toolMatcher, timeoutSeconds: 5),
                ["PermissionRequest"] = HookArray(command, timeoutSeconds: 5),
                ["Notification"] = HookArray(command),
                ["Stop"] = HookArray(command),
                ["StopFailure"] = HookArray(command)
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

    private static JsonArray HookArray(string command, string matcher = "", int? timeoutSeconds = null)
    {
        var hook = new JsonObject
        {
            ["type"] = "command",
            ["command"] = command
        };
        if (timeoutSeconds is > 0)
        {
            hook["timeout"] = timeoutSeconds.Value;
        }

        var entry = new JsonObject
        {
            ["hooks"] = new JsonArray
            {
                hook
            }
        };

        if (!string.IsNullOrWhiteSpace(matcher))
        {
            entry["matcher"] = matcher;
        }

        return new JsonArray(entry);
    }

    private static string SetFeatureFlag(
        string text,
        string key,
        string value,
        IReadOnlyCollection<string>? removeKeys = null)
    {
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = Regex.Split(text, "\r\n|\n").ToList();
        if (lines.Count > 0 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        var featuresStart = -1;
        var featuresEnd = lines.Count;
        for (var i = 0; i < lines.Count; i++)
        {
            if (!Regex.IsMatch(lines[i], @"^\s*\[features\]\s*(?:#.*)?$", RegexOptions.IgnoreCase))
            {
                continue;
            }

            featuresStart = i;
            featuresEnd = lines.Count;
            for (var j = i + 1; j < lines.Count; j++)
            {
                if (Regex.IsMatch(lines[j], @"^\s*\[[^\]]+\]\s*(?:#.*)?$"))
                {
                    featuresEnd = j;
                    break;
                }
            }

            break;
        }

        if (featuresStart < 0)
        {
            if (lines.Count > 0 && !string.IsNullOrWhiteSpace(lines[^1]))
            {
                lines.Add(string.Empty);
            }

            lines.Add("[features]");
            lines.Add($"{key} = {value}");
            return string.Join(newline, lines) + newline;
        }

        var keysToRemove = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (removeKeys is not null)
        {
            foreach (var removeKey in removeKeys)
            {
                keysToRemove.Add(removeKey);
            }
        }

        var insertedOrUpdated = false;
        for (var i = featuresStart + 1; i < featuresEnd; i++)
        {
            var line = lines[i];
            var keyMatch = Regex.Match(line, @"^\s*([A-Za-z0-9_.-]+)\s*=");
            if (!keyMatch.Success)
            {
                continue;
            }

            var currentKey = keyMatch.Groups[1].Value;
            if (keysToRemove.Contains(currentKey))
            {
                lines.RemoveAt(i);
                i--;
                featuresEnd--;
                continue;
            }

            if (string.Equals(currentKey, key, StringComparison.OrdinalIgnoreCase))
            {
                var commentMatch = Regex.Match(line, @"(\s+#.*)$");
                lines[i] = $"{key} = {value}{commentMatch.Value}";
                insertedOrUpdated = true;
            }
        }

        if (!insertedOrUpdated)
        {
            lines.Insert(featuresStart + 1, $"{key} = {value}");
        }

        return string.Join(newline, lines) + newline;
    }

    private static string SetTopLevelArray(string text, string key, IReadOnlyList<string> values)
    {
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = Regex.Split(text, "\r\n|\n").ToList();
        if (lines.Count > 0 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        var replacement = $"{key} = {FormatTomlArray(values)}";
        var firstSection = lines.FindIndex(line => Regex.IsMatch(line, @"^\s*\[[^\]]+\]\s*(?:#.*)?$"));
        if (firstSection < 0)
        {
            firstSection = lines.Count;
        }

        for (var i = 0; i < firstSection; i++)
        {
            if (Regex.IsMatch(lines[i], $@"^\s*{Regex.Escape(key)}\s*=", RegexOptions.IgnoreCase))
            {
                lines[i] = replacement;
                return string.Join(newline, lines) + newline;
            }
        }

        lines.Insert(firstSection, replacement);
        if (firstSection + 1 < lines.Count && !string.IsNullOrWhiteSpace(lines[firstSection + 1]))
        {
            lines.Insert(firstSection + 1, string.Empty);
        }

        return string.Join(newline, lines) + newline;
    }

    private static string SetSectionKey(string text, string sectionName, string key, string value)
    {
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = Regex.Split(text, "\r\n|\n").ToList();
        if (lines.Count > 0 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        var sectionStart = -1;
        var sectionEnd = lines.Count;
        var sectionPattern = $@"^\s*\[{Regex.Escape(sectionName)}\]\s*(?:#.*)?$";
        for (var i = 0; i < lines.Count; i++)
        {
            if (!Regex.IsMatch(lines[i], sectionPattern, RegexOptions.IgnoreCase))
            {
                continue;
            }

            sectionStart = i;
            sectionEnd = lines.Count;
            for (var j = i + 1; j < lines.Count; j++)
            {
                if (Regex.IsMatch(lines[j], @"^\s*\[[^\]]+\]\s*(?:#.*)?$"))
                {
                    sectionEnd = j;
                    break;
                }
            }

            break;
        }

        if (sectionStart < 0)
        {
            if (lines.Count > 0 && !string.IsNullOrWhiteSpace(lines[^1]))
            {
                lines.Add(string.Empty);
            }

            lines.Add($"[{sectionName}]");
            lines.Add($"{key} = {value}");
            return string.Join(newline, lines) + newline;
        }

        for (var i = sectionStart + 1; i < sectionEnd; i++)
        {
            if (Regex.IsMatch(lines[i], $@"^\s*{Regex.Escape(key)}\s*=", RegexOptions.IgnoreCase))
            {
                lines[i] = $"{key} = {value}";
                return string.Join(newline, lines) + newline;
            }
        }

        lines.Insert(sectionStart + 1, $"{key} = {value}");
        return string.Join(newline, lines) + newline;
    }

    private static string FormatTomlArray(IReadOnlyList<string> values)
    {
        return "[" + string.Join(", ", values.Select(value => $"\"{EscapeTomlString(value)}\"")) + "]";
    }

    private static string EscapeTomlString(string value)
    {
        return value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);
    }

}

internal readonly record struct AgentHookInstallResult(
    bool Success,
    string Message);

internal readonly record struct AgentTrackingInstallStatus(
    bool Codex,
    bool Claude);
