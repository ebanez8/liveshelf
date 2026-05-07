using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LiveShelf;

internal sealed class AgentSessionRegistry
{
    private const int AutoLinkThreshold = 85;
    private const int AmbiguousThreshold = 60;
    private const int ClearWinnerMargin = 20;

    private readonly Func<IReadOnlyList<ShelvedWindow>> _getCards;
    private readonly Dictionary<string, AgentSession> _sessionsByKey = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _ambiguousPromptedKeys = new(StringComparer.OrdinalIgnoreCase);

    public AgentSessionRegistry(Func<IReadOnlyList<ShelvedWindow>> getCards)
    {
        _getCards = getCards;
    }

    public event EventHandler<AgentCardUpdate>? CardUpdateRequested;

    public event EventHandler<AgentLinkAmbiguousEventArgs>? AmbiguousLinkDetected;

    public void ApplyEvent(AgentEvent agentEvent)
    {
        var source = NormalizeSource(agentEvent.Source);
        if (string.IsNullOrWhiteSpace(source))
        {
            return;
        }

        var sessionId = string.IsNullOrWhiteSpace(agentEvent.SessionId)
            ? agentEvent.EffectiveSessionId
            : agentEvent.SessionId;
        var key = AgentSession.BuildKey(source, sessionId);
        if (!_sessionsByKey.TryGetValue(key, out var session))
        {
            session = new AgentSession(source, sessionId);
            _sessionsByKey[key] = session;
        }

        UpdateSessionState(session, agentEvent);

        if (!string.IsNullOrWhiteSpace(session.LinkedCardId))
        {
            var linkedCard = _getCards().FirstOrDefault(card =>
                card.Id == session.LinkedCardId || card.LinkedAgentKey == session.Key);
            if (linkedCard is not null)
            {
                ApplyLinkedCard(session, linkedCard);
                return;
            }

            session.LinkedCardId = string.Empty;
        }

        TryAutoLinkSession(session);
    }

    public void TryAutoLinkCard(ShelvedWindow card)
    {
        if (card.HasLinkedAgentSession)
        {
            return;
        }

        var candidates = _sessionsByKey.Values
            .Where(session => string.IsNullOrWhiteSpace(session.LinkedCardId))
            .Where(session => DateTime.UtcNow - session.LastEventAtUtc < TimeSpan.FromHours(2))
            .Select(session => new AgentSessionCandidate(session, ScoreAgentCardMatch(card, session)))
            .Where(candidate => candidate.Score > 0)
            .OrderByDescending(candidate => candidate.Score)
            .ToList();

        var best = candidates.FirstOrDefault();
        if (best is null || best.Score < AutoLinkThreshold)
        {
            return;
        }

        var second = candidates.Skip(1).FirstOrDefault();
        if (second is null || best.Score - second.Score >= ClearWinnerMargin)
        {
            LinkSessionToCard(best.Session, card);
        }
    }

    public void UnlinkCard(ShelvedWindow card)
    {
        if (string.IsNullOrWhiteSpace(card.LinkedAgentKey))
        {
            return;
        }

        if (_sessionsByKey.TryGetValue(card.LinkedAgentKey, out var session))
        {
            session.LinkedCardId = string.Empty;
            session.LastAppliedBadgeKey = string.Empty;
        }

        card.LinkedAgentKey = string.Empty;
    }

    public void LinkSessionToCard(AgentSession session, ShelvedWindow card)
    {
        session.LinkedCardId = card.Id;
        card.LinkedAgentKey = session.Key;
        card.IsAgentLikeSession = true;
        card.SuspectedAgent = session.Source;
        _ambiguousPromptedKeys.Remove(session.Key);
        if (string.IsNullOrWhiteSpace(card.PossibleCwd))
        {
            card.PossibleCwd = session.Cwd;
        }

        ApplyLinkedCard(session, card);
    }

    private void TryAutoLinkSession(AgentSession session)
    {
        var candidates = _getCards()
            .Where(card => !card.HasLinkedAgentSession)
            .Select(card => new AgentCardCandidate(card, ScoreAgentCardMatch(card, session)))
            .Where(candidate => candidate.Score > 0)
            .OrderByDescending(candidate => candidate.Score)
            .ToList();

        var best = candidates.FirstOrDefault();
        if (best is null)
        {
            return;
        }

        var second = candidates.Skip(1).FirstOrDefault();
        if (best.Score >= AutoLinkThreshold &&
            (second is null || best.Score - second.Score >= ClearWinnerMargin))
        {
            LinkSessionToCard(session, best.Card);
            return;
        }

        if (best.Score >= AmbiguousThreshold)
        {
            if (_ambiguousPromptedKeys.Add(session.Key))
            {
                AmbiguousLinkDetected?.Invoke(
                    this,
                    new AgentLinkAmbiguousEventArgs(session, candidates.Take(2).ToArray()));
            }
        }
    }

    private void ApplyLinkedCard(AgentSession session, ShelvedWindow card)
    {
        var badge = ComputeAgentBadge(session);
        var badgeKey = $"{session.Status}:{badge.Label}:{badge.Detail}:{CountChangedFiles(session)}";
        if (string.Equals(session.LastAppliedBadgeKey, badgeKey, StringComparison.Ordinal))
        {
            badge = badge with { Notify = false };
        }
        else
        {
            session.LastAppliedBadgeKey = badgeKey;
        }

        CardUpdateRequested?.Invoke(this, new AgentCardUpdate(card, session, badge));
    }

    private static AgentBadge ComputeAgentBadge(AgentSession session)
    {
        return session.Status switch
        {
            AgentSessionStatus.Failed => new AgentBadge(ShelfBadgeKind.Failed, "Failed", BuildFailureDetail(session), true),
            AgentSessionStatus.Waiting => new AgentBadge(ShelfBadgeKind.WaitingForApproval, "Waiting for approval", string.Empty, true),
            AgentSessionStatus.Editing => new AgentBadge(ShelfBadgeKind.EditingFiles, "Editing files", string.Empty, false),
            AgentSessionStatus.RunningCommand => new AgentBadge(ShelfBadgeKind.RunningCommand, "Running command", string.Empty, false),
            AgentSessionStatus.Done => BuildDoneBadge(session),
            AgentSessionStatus.Idle => new AgentBadge(ShelfBadgeKind.Running, "Idle", string.Empty, false),
            _ => new AgentBadge(ShelfBadgeKind.Running, "Working", string.Empty, false)
        };
    }

    private static AgentBadge BuildDoneBadge(AgentSession session)
    {
        var changedCount = CountChangedFiles(session);
        return changedCount > 0
            ? new AgentBadge(ShelfBadgeKind.DoneNeedsReview, $"Done - {changedCount} files changed", string.Empty, true)
            : new AgentBadge(ShelfBadgeKind.Done, "Done", string.Empty, true);
    }

    private static string BuildFailureDetail(AgentSession session)
    {
        return string.IsNullOrWhiteSpace(session.CurrentTurnId)
            ? string.Empty
            : $"Turn {session.CurrentTurnId}";
    }

    private static int CountChangedFiles(AgentSession session)
    {
        return Math.Max(session.ChangedFilesSinceTurnStart.Count, session.ChangedFileCountEstimate);
    }

    private static void UpdateSessionState(AgentSession session, AgentEvent agentEvent)
    {
        session.LastEventAtUtc = agentEvent.Timestamp > 0 ? agentEvent.TimestampUtc : DateTime.UtcNow;
        session.Cwd = FirstNonEmpty(agentEvent.Cwd, session.Cwd);
        session.TranscriptPath = FirstNonEmpty(agentEvent.TranscriptPath, session.TranscriptPath);

        var eventName = NormalizeEventName(agentEvent.EffectiveEventName);
        switch (eventName)
        {
            case "userpromptsubmit":
            case "sessionstart":
                session.Status = AgentSessionStatus.Working;
                session.CurrentTurnId = FirstNonEmpty(agentEvent.TurnId, session.CurrentTurnId);
                session.LastPromptAtUtc = session.LastEventAtUtc;
                session.ActiveToolUseIds.Clear();
                session.ChangedFilesSinceTurnStart.Clear();
                session.ChangedFileCountEstimate = 0;
                break;

            case "pretooluse":
                session.Status = GetToolStatus(agentEvent.ToolName);
                if (!string.IsNullOrWhiteSpace(agentEvent.ToolUseId))
                {
                    session.ActiveToolUseIds.Add(agentEvent.ToolUseId);
                }

                AddChangedFilesFromEvent(session, agentEvent);
                break;

            case "posttooluse":
                if (!string.IsNullOrWhiteSpace(agentEvent.ToolUseId))
                {
                    session.ActiveToolUseIds.Remove(agentEvent.ToolUseId);
                }

                AddChangedFilesFromEvent(session, agentEvent);
                if (session.ActiveToolUseIds.Count == 0)
                {
                    session.Status = AgentSessionStatus.Working;
                }

                break;

            case "permissionrequest":
                session.Status = AgentSessionStatus.Waiting;
                break;

            case "notification":
                if (LooksLikePermission(agentEvent))
                {
                    session.Status = AgentSessionStatus.Waiting;
                }

                break;

            case "filechanged":
                AddChangedFilesFromEvent(session, agentEvent);
                break;

            case "stop":
            case "taskcompleted":
                session.Status = AgentSessionStatus.Done;
                session.LastStopAtUtc = session.LastEventAtUtc;
                break;

            case "stopfailure":
                session.Status = AgentSessionStatus.Failed;
                session.LastStopAtUtc = session.LastEventAtUtc;
                break;
        }

        if (agentEvent.FilesChanged is { } filesChanged)
        {
            session.ChangedFileCountEstimate = Math.Max(session.ChangedFileCountEstimate, filesChanged);
        }
    }

    private static AgentSessionStatus GetToolStatus(string toolName)
    {
        if (IsEditTool(toolName))
        {
            return AgentSessionStatus.Editing;
        }

        return IsShellTool(toolName)
            ? AgentSessionStatus.RunningCommand
            : AgentSessionStatus.Working;
    }

    private static void AddChangedFilesFromEvent(AgentSession session, AgentEvent agentEvent)
    {
        if (!string.IsNullOrWhiteSpace(agentEvent.FilePath))
        {
            session.ChangedFilesSinceTurnStart.Add(NormalizePathLabel(agentEvent.FilePath));
        }

        AddPathsFromJson(session, agentEvent.ToolInput);
        AddPathsFromJson(session, agentEvent.ToolResponse);

        if (IsEditTool(agentEvent.ToolName) &&
            session.ChangedFilesSinceTurnStart.Count == 0 &&
            !string.IsNullOrWhiteSpace(agentEvent.ToolUseId))
        {
            session.ChangedFileCountEstimate = Math.Max(session.ChangedFileCountEstimate, 1);
        }
    }

    private static void AddPathsFromJson(AgentSession session, JsonElement? json)
    {
        if (json is not { } element)
        {
            return;
        }

        AddPathsFromJsonElement(session, element);
    }

    private static void AddPathsFromJsonElement(AgentSession session, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    var name = NormalizeEventName(property.Name);
                    if (IsPathProperty(name))
                    {
                        AddPathValue(session, property.Value);
                    }
                    else if (name is "patch" or "input" or "command" or "cmd")
                    {
                        AddPatchPaths(session, property.Value);
                    }
                    else
                    {
                        AddPathsFromJsonElement(session, property.Value);
                    }
                }

                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    AddPathsFromJsonElement(session, item);
                }

                break;

            case JsonValueKind.String:
                AddPatchPaths(session, element);
                break;
        }
    }

    private static void AddPathValue(AgentSession session, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            var path = value.GetString();
            if (!string.IsNullOrWhiteSpace(path))
            {
                session.ChangedFilesSinceTurnStart.Add(NormalizePathLabel(path));
            }

            return;
        }

        AddPathsFromJsonElement(session, value);
    }

    private static void AddPatchPaths(AgentSession session, JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String)
        {
            return;
        }

        var text = value.GetString();
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        foreach (Match match in Regex.Matches(
                     text,
                     @"^\*\*\* (?:Add|Update|Delete) File:\s*(.+)$",
                     RegexOptions.Multiline))
        {
            session.ChangedFilesSinceTurnStart.Add(NormalizePathLabel(match.Groups[1].Value.Trim()));
        }
    }

    private static int ScoreAgentCardMatch(ShelvedWindow card, AgentSession session)
    {
        var score = 0;

        if (string.Equals(card.LinkedAgentKey, session.Key, StringComparison.OrdinalIgnoreCase))
        {
            score += 100;
        }

        if (!string.IsNullOrWhiteSpace(card.PossibleCwd) &&
            !string.IsNullOrWhiteSpace(session.Cwd) &&
            SamePath(card.PossibleCwd, session.Cwd))
        {
            score += 60;
        }

        if (!string.IsNullOrWhiteSpace(session.Cwd) &&
            TitleContainsFolderName(card.Title, session.Cwd))
        {
            score += 25;
        }

        if (ProcessInfoSuggestsAgent(card, session.Source))
        {
            score += 45;
        }

        if (Math.Abs((card.ShelvedAtUtc - session.LastEventAtUtc).TotalMilliseconds) < 15000)
        {
            score += 15;
        }

        if (IsTerminalProcess(card.ProcessName))
        {
            score += 15;
        }

        if (string.Equals(card.SuspectedAgent, session.Source, StringComparison.OrdinalIgnoreCase))
        {
            score += 30;
        }

        return score;
    }

    private static bool IsPathProperty(string normalizedName)
    {
        return normalizedName is "filepath" or "path" or "filename" or "file" or "files";
    }

    private static bool LooksLikePermission(AgentEvent agentEvent)
    {
        var text = $"{agentEvent.Error} {agentEvent.LastAssistantMessage} {agentEvent.ToolName}";
        return text.Contains("approval", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("permission", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("allow", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("confirm", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsEditTool(string toolName)
    {
        var normalized = NormalizeEventName(toolName);
        return normalized.Contains("applypatch", StringComparison.Ordinal) ||
               normalized.Contains("edit", StringComparison.Ordinal) ||
               normalized.Contains("write", StringComparison.Ordinal) ||
               normalized.Contains("multiedit", StringComparison.Ordinal);
    }

    private static bool IsShellTool(string toolName)
    {
        var normalized = NormalizeEventName(toolName);
        return normalized.Contains("bash", StringComparison.Ordinal) ||
               normalized.Contains("shell", StringComparison.Ordinal) ||
               normalized.Contains("command", StringComparison.Ordinal) ||
               normalized.Contains("powershell", StringComparison.Ordinal) ||
               normalized.Contains("cmd", StringComparison.Ordinal);
    }

    private static bool SamePath(string first, string second)
    {
        try
        {
            first = Path.GetFullPath(first).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            second = Path.GetFullPath(second).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            first = first.TrimEnd('\\', '/');
            second = second.TrimEnd('\\', '/');
        }

        return string.Equals(first, second, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TitleContainsFolderName(string title, string cwd)
    {
        var folderName = Path.GetFileName(cwd.TrimEnd('\\', '/'));
        return !string.IsNullOrWhiteSpace(folderName) &&
               title.Contains(folderName, StringComparison.OrdinalIgnoreCase);
    }

    private static bool ProcessInfoSuggestsAgent(ShelvedWindow card, string source)
    {
        var text = $"{card.ProcessName} {card.ExePath} {card.Title} {card.SuspectedAgent}";
        return source.Contains("codex", StringComparison.OrdinalIgnoreCase)
            ? text.Contains("codex", StringComparison.OrdinalIgnoreCase)
            : text.Contains("claude", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTerminalProcess(string processName)
    {
        return processName.Contains("windowsterminal", StringComparison.OrdinalIgnoreCase) ||
               processName.Equals("wt", StringComparison.OrdinalIgnoreCase) ||
               processName.Contains("conhost", StringComparison.OrdinalIgnoreCase) ||
               processName.Contains("cmd", StringComparison.OrdinalIgnoreCase) ||
               processName.Contains("powershell", StringComparison.OrdinalIgnoreCase) ||
               processName.Contains("pwsh", StringComparison.OrdinalIgnoreCase) ||
               processName.Contains("wezterm", StringComparison.OrdinalIgnoreCase) ||
               processName.Contains("alacritty", StringComparison.OrdinalIgnoreCase) ||
               processName.Contains("tabby", StringComparison.OrdinalIgnoreCase) ||
               processName.Contains("hyper", StringComparison.OrdinalIgnoreCase) ||
               processName.Contains("ghostty", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeSource(string source)
    {
        source = source.Trim().ToLowerInvariant();
        if (source.Contains("codex", StringComparison.Ordinal))
        {
            return "codex";
        }

        return source.Contains("claude", StringComparison.Ordinal) ? "claude" : source;
    }

    private static string NormalizeEventName(string value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : new string(value.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
    }

    private static string NormalizePathLabel(string path)
    {
        return path.Trim().Trim('"');
    }

    private static string FirstNonEmpty(string first, string second)
    {
        return string.IsNullOrWhiteSpace(first) ? second : first;
    }
}

internal sealed record AgentCardUpdate(
    ShelvedWindow Card,
    AgentSession Session,
    AgentBadge Badge);

internal sealed record AgentCardCandidate(
    ShelvedWindow Card,
    int Score);

internal sealed record AgentSessionCandidate(
    AgentSession Session,
    int Score);

internal sealed class AgentLinkAmbiguousEventArgs : EventArgs
{
    public AgentLinkAmbiguousEventArgs(AgentSession session, IReadOnlyList<AgentCardCandidate> candidates)
    {
        Session = session;
        Candidates = candidates;
    }

    public AgentSession Session { get; }

    public IReadOnlyList<AgentCardCandidate> Candidates { get; }
}
