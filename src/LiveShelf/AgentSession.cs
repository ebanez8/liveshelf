namespace LiveShelf;

internal sealed class AgentSession
{
    public AgentSession(string source, string sessionId)
    {
        Source = source;
        SessionId = sessionId;
    }

    public string Source { get; }

    public string SessionId { get; set; }

    public string Key => BuildKey(Source, SessionId);

    public string CurrentTurnId { get; set; } = string.Empty;

    public string LiveShelfAgentToken { get; set; } = string.Empty;

    public string TerminalSessionId { get; set; } = string.Empty;

    public string LaunchCwd { get; set; } = string.Empty;

    public string TermProgram { get; set; } = string.Empty;

    public string Cwd { get; set; } = string.Empty;

    public string TranscriptPath { get; set; } = string.Empty;

    public AgentSessionStatus Status { get; set; } = AgentSessionStatus.Idle;

    public HashSet<string> ActiveToolUseIds { get; } = new(StringComparer.OrdinalIgnoreCase);

    public HashSet<int> RelatedProcessIds { get; } = [];

    public int ForegroundProcessId { get; set; }

    public DateTime LastEventAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime LastPromptAtUtc { get; set; }

    public DateTime LastStopAtUtc { get; set; }

    public HashSet<string> ChangedFilesSinceTurnStart { get; } = new(StringComparer.OrdinalIgnoreCase);

    public int ChangedFileCountEstimate { get; set; }

    public string LinkedCardId { get; set; } = string.Empty;

    public string LastAppliedBadgeKey { get; set; } = string.Empty;

    public static string BuildKey(string source, string sessionId)
    {
        return $"{source}:{sessionId}";
    }
}

internal enum AgentSessionStatus
{
    Idle,
    Working,
    Editing,
    RunningCommand,
    Waiting,
    Done,
    Failed
}
