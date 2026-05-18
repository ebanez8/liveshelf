using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

namespace LiveShelf;

internal sealed class HookHealthDiagnostics
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };

    private readonly object _lock = new();
    private readonly Dictionary<string, EventStats> _statsBySource = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> _recentErrors = new();
    private readonly string _filePath;
    private DateTime _lastWriteUtc = DateTime.MinValue;

    public HookHealthDiagnostics()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LiveShelf");
        Directory.CreateDirectory(dir);
        _filePath = Path.Combine(dir, "hook-health.json");
    }

    public void RecordEvent(AgentEvent agentEvent)
    {
        lock (_lock)
        {
            var source = string.IsNullOrWhiteSpace(agentEvent.Source) ? "unknown" : agentEvent.Source.ToLowerInvariant();
            if (!_statsBySource.TryGetValue(source, out var stats))
            {
                stats = new EventStats();
                _statsBySource[source] = stats;
            }

            stats.TotalCount++;
            stats.LastEventAtUtc = DateTime.UtcNow;
            stats.LastEventName = agentEvent.EffectiveEventName ?? string.Empty;
            stats.LastSessionId = agentEvent.SessionId ?? string.Empty;
            stats.LastToolName = agentEvent.ToolName ?? string.Empty;
            stats.LastCwd = agentEvent.Cwd ?? string.Empty;
            stats.LastForegroundProcessId = agentEvent.ForegroundProcessId;
            stats.LastLiveShelfAgentToken = agentEvent.LiveShelfAgentToken ?? string.Empty;
            stats.LastTerminalSessionId = agentEvent.TerminalSessionId ?? string.Empty;

            if (!stats.EventCounts.TryGetValue(stats.LastEventName, out var count))
            {
                count = 0;
            }

            stats.EventCounts[stats.LastEventName] = count + 1;
        }
    }

    public void RecordError(string error)
    {
        lock (_lock)
        {
            _recentErrors.Enqueue($"{DateTimeOffset.UtcNow:O} {error}");
            while (_recentErrors.Count > 20)
            {
                _recentErrors.Dequeue();
            }
        }
    }

    public void WriteSnapshot(
        IReadOnlyCollection<AgentSession> sessions,
        IReadOnlyCollection<ShelvedWindow> cards,
        bool throttle = true)
    {
        if (throttle && DateTime.UtcNow - _lastWriteUtc < TimeSpan.FromSeconds(2))
        {
            return;
        }

        lock (_lock)
        {
            try
            {
                var snapshot = BuildSnapshot(sessions, cards);
                File.WriteAllText(_filePath, snapshot.ToJsonString(JsonOptions));
                _lastWriteUtc = DateTime.UtcNow;
            }
            catch (Exception ex)
            {
                _recentErrors.Enqueue($"{DateTimeOffset.UtcNow:O} write failed: {ex.Message}");
            }
        }
    }

    private JsonObject BuildSnapshot(IReadOnlyCollection<AgentSession> sessions, IReadOnlyCollection<ShelvedWindow> cards)
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var bridgePath = Path.Combine(localAppData, "LiveShelf", "liveshelf-bridge.exe");
        var rawHooksPath = Path.Combine(localAppData, "LiveShelf", "logs", "raw-hooks.jsonl");
        var queuePath = Path.Combine(localAppData, "LiveShelf", "queued-agent-events.jsonl");

        var sources = new JsonObject();
        foreach (var (source, stats) in _statsBySource)
        {
            var counts = new JsonObject();
            foreach (var (name, count) in stats.EventCounts)
            {
                counts[name] = count;
            }

            sources[source] = new JsonObject
            {
                ["totalCount"] = stats.TotalCount,
                ["lastEventAtUtc"] = stats.LastEventAtUtc.ToString("O"),
                ["secondsSinceLastEvent"] = (DateTime.UtcNow - stats.LastEventAtUtc).TotalSeconds,
                ["lastEventName"] = stats.LastEventName,
                ["lastSessionId"] = stats.LastSessionId,
                ["lastToolName"] = stats.LastToolName,
                ["lastCwd"] = stats.LastCwd,
                ["lastForegroundProcessId"] = stats.LastForegroundProcessId,
                ["lastLiveShelfAgentToken"] = stats.LastLiveShelfAgentToken,
                ["lastTerminalSessionId"] = stats.LastTerminalSessionId,
                ["eventCounts"] = counts
            };
        }

        var sessionsJson = new JsonArray();
        foreach (var session in sessions)
        {
            sessionsJson.Add(new JsonObject
            {
                ["key"] = session.Key,
                ["source"] = session.Source,
                ["sessionId"] = session.SessionId,
                ["status"] = session.Status.ToString(),
                ["linkedCardId"] = session.LinkedCardId,
                ["liveShelfAgentToken"] = session.LiveShelfAgentToken,
                ["terminalSessionId"] = session.TerminalSessionId,
                ["foregroundProcessId"] = session.ForegroundProcessId,
                ["cwd"] = session.Cwd,
                ["launchCwd"] = session.LaunchCwd,
                ["secondsSinceLastEvent"] = (DateTime.UtcNow - session.LastEventReceivedAtUtc).TotalSeconds,
                ["lastBadgeKey"] = session.LastAppliedBadgeKey,
                ["activeToolUseCount"] = session.ActiveToolUseIds.Count,
                ["relatedProcessIds"] = ToJsonArray(session.RelatedProcessIds)
            });
        }

        var cardsJson = new JsonArray();
        foreach (var card in cards)
        {
            cardsJson.Add(new JsonObject
            {
                ["id"] = card.Id,
                ["title"] = card.Title,
                ["processName"] = card.ProcessName,
                ["sourceProcessId"] = card.SourceProcessId,
                ["suspectedAgent"] = card.SuspectedAgent,
                ["isAgentLikeSession"] = card.IsAgentLikeSession,
                ["linkedAgentKey"] = card.LinkedAgentKey,
                ["liveShelfAgentToken"] = card.LiveShelfAgentToken,
                ["terminalSessionId"] = card.TerminalSessionId,
                ["launchCwd"] = card.LaunchCwd,
                ["possibleCwd"] = card.PossibleCwd,
                ["badgeKind"] = card.BadgeKind.ToString(),
                ["badgeLabel"] = card.BadgeText ?? string.Empty
            });
        }

        var errors = new JsonArray();
        foreach (var error in _recentErrors)
        {
            errors.Add(error);
        }

        return new JsonObject
        {
            ["generatedAtUtc"] = DateTime.UtcNow.ToString("O"),
            ["liveShelfVersion"] = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown",
            ["liveShelfPid"] = Environment.ProcessId,
            ["paths"] = new JsonObject
            {
                ["bridgeExe"] = bridgePath,
                ["bridgeExists"] = File.Exists(bridgePath),
                ["rawHooksLog"] = rawHooksPath,
                ["rawHooksLogExists"] = File.Exists(rawHooksPath),
                ["queueFile"] = queuePath,
                ["queueFileExists"] = File.Exists(queuePath),
                ["queueFileSize"] = File.Exists(queuePath) ? new FileInfo(queuePath).Length : 0,
                ["claudeSettingsJson"] = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "settings.json"),
                ["codexHooksJson"] = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "hooks.json")
            },
            ["sources"] = sources,
            ["sessionCount"] = sessions.Count,
            ["sessions"] = sessionsJson,
            ["cardCount"] = cards.Count,
            ["cards"] = cardsJson,
            ["recentErrors"] = errors
        };
    }

    private static JsonArray ToJsonArray(IEnumerable<int> values)
    {
        var array = new JsonArray();
        foreach (var value in values)
        {
            array.Add(value);
        }

        return array;
    }

    private sealed class EventStats
    {
        public int TotalCount { get; set; }

        public DateTime LastEventAtUtc { get; set; } = DateTime.UtcNow;

        public string LastEventName { get; set; } = string.Empty;

        public string LastSessionId { get; set; } = string.Empty;

        public string LastToolName { get; set; } = string.Empty;

        public string LastCwd { get; set; } = string.Empty;

        public int LastForegroundProcessId { get; set; }

        public string LastLiveShelfAgentToken { get; set; } = string.Empty;

        public string LastTerminalSessionId { get; set; } = string.Empty;

        public Dictionary<string, int> EventCounts { get; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
