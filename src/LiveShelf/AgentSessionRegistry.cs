using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LiveShelf;

internal sealed class AgentSessionRegistry
{
    private static readonly TimeSpan UnlinkedSessionTtl = TimeSpan.FromHours(6);

    private const int AutoLinkScore = 160;
    private const int LinkedKeyScore = 1000;
    private const int ForegroundProcessScore = 200;
    private const int TerminalSessionScore = 150;
    private const int ProcessMatchScore = 80;
    private const int CwdMatchScore = 35;
    private const int TitleCwdMatchScore = 20;
    private const int SourceHintScore = 10;
    private const int ClearWinnerMargin = 50;

    private readonly Func<IReadOnlyList<ShelvedWindow>> _getCards;
    private readonly Dictionary<string, AgentSession> _sessionsByKey = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, AgentSession> _sessionsByToken = new(StringComparer.OrdinalIgnoreCase);
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
        if (!HasReliableSessionId(agentEvent, sessionId))
        {
            if (TryApplySessionlessLifecycle(source, agentEvent))
            {
                return;
            }

            Trace.WriteLine(
                $"LiveShelf agent event ignored source={source} sessionId={sessionId} reason=unreliable-session-id cwd={agentEvent.Cwd}");
            return;
        }

        var session = GetOrCreateSession(source, sessionId, agentEvent.LiveShelfAgentToken);

        UpdateSessionState(session, agentEvent);

        if (!string.IsNullOrWhiteSpace(session.LiveShelfAgentToken))
        {
            ApplyTokenRoutedSession(session);
            return;
        }

        if (!string.IsNullOrWhiteSpace(session.LinkedCardId))
        {
            var linkedCard = _getCards().FirstOrDefault(card =>
                card.Id == session.LinkedCardId &&
                string.Equals(card.LinkedAgentKey, session.Key, StringComparison.OrdinalIgnoreCase));
            if (linkedCard is not null)
            {
                ApplyLinkedCard(session, linkedCard);
                return;
            }

            session.LinkedCardId = string.Empty;
        }

        var exactCard = _getCards().FirstOrDefault(card =>
            string.Equals(card.LinkedAgentKey, session.Key, StringComparison.OrdinalIgnoreCase));
        if (exactCard is not null)
        {
            LinkSessionToCard(session, exactCard);
            return;
        }

        TryAttachModeAutoLinkSession(session);
    }

    internal int SessionCount => _sessionsByKey.Count;

    internal int TokenSessionCount => _sessionsByToken.Count;

    internal void PruneOldUnlinkedSessions(DateTime nowUtc)
    {
        var liveCardIds = _getCards()
            .Where(card => !string.IsNullOrWhiteSpace(card.LinkedAgentKey))
            .Select(card => card.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var session in _sessionsByKey.Values.ToArray())
        {
            if (!string.IsNullOrWhiteSpace(session.LinkedCardId) && liveCardIds.Contains(session.LinkedCardId))
            {
                continue;
            }

            if (nowUtc - session.LastEventAtUtc < UnlinkedSessionTtl)
            {
                continue;
            }

            _sessionsByKey.Remove(session.Key);
            if (!string.IsNullOrWhiteSpace(session.LiveShelfAgentToken))
            {
                _sessionsByToken.Remove(session.LiveShelfAgentToken);
            }

            _ambiguousPromptedKeys.Remove(session.Key);
        }
    }

    private bool TryApplySessionlessLifecycle(string source, AgentEvent agentEvent)
    {
        var eventName = NormalizeEventName(agentEvent.EffectiveEventName);
        var nextStatus = eventName switch
        {
            "agentturncomplete" or "turncomplete" or "taskcompleted" or "stop" => AgentSessionStatus.Done,
            "approvalrequest" or "agentapprovalrequest" => AgentSessionStatus.Waiting,
            "stopfailure" => AgentSessionStatus.Failed,
            _ => (AgentSessionStatus?)null
        };

        if (nextStatus is null)
        {
            return false;
        }

        var session = _sessionsByKey.Values
            .Where(candidate => string.Equals(candidate.Source, source, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(candidate => candidate.LastEventAtUtc)
            .FirstOrDefault();

        if (session is null)
        {
            return false;
        }

        var now = agentEvent.Timestamp > 0 ? agentEvent.TimestampUtc : DateTime.UtcNow;
        session.Status = nextStatus.Value;
        session.LastEventAtUtc = now;
        if (nextStatus.Value is AgentSessionStatus.Done or AgentSessionStatus.Failed)
        {
            session.LastStopAtUtc = now;
        }

        if (string.IsNullOrWhiteSpace(session.LinkedCardId))
        {
            return true;
        }

        var linkedCard = _getCards().FirstOrDefault(card =>
            card.Id == session.LinkedCardId &&
            string.Equals(card.LinkedAgentKey, session.Key, StringComparison.OrdinalIgnoreCase));
        if (linkedCard is not null)
        {
            ApplyLinkedCard(session, linkedCard);
        }
        else
        {
            session.LinkedCardId = string.Empty;
        }

        return true;
    }

    public void TryAutoLinkCard(ShelvedWindow card)
    {
        if (card.HasLinkedAgentSession)
        {
            return;
        }

        var tokenSession = FindTokenSessionForCard(card);
        if (tokenSession is not null)
        {
            LinkSessionToCard(tokenSession, card);
            return;
        }

        var candidates = _sessionsByKey.Values
            .Where(session => string.IsNullOrWhiteSpace(session.LinkedCardId))
            .Where(session => string.IsNullOrWhiteSpace(session.LiveShelfAgentToken))
            .Where(session => DateTime.UtcNow - session.LastEventAtUtc < TimeSpan.FromHours(2))
            .Select(session => new AgentSessionCandidate(session, ScoreStrongAgentCardMatch(card, session)))
            .Where(candidate => candidate.Score > 0)
            .OrderByDescending(candidate => candidate.Score)
            .ToList();

        if (TryChooseAutoLink(candidates, out var sessionToLink))
        {
            LinkSessionToCard(sessionToLink, card);
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
        card.LiveShelfAgentToken = session.LiveShelfAgentToken;
        card.TerminalSessionId = session.TerminalSessionId;
        card.LaunchCwd = session.LaunchCwd;
        card.IsAgentLikeSession = true;
        card.SuspectedAgent = session.Source;
        _ambiguousPromptedKeys.Remove(session.Key);
        if (string.IsNullOrWhiteSpace(card.PossibleCwd))
        {
            card.PossibleCwd = session.Cwd;
        }

        ApplyLinkedCard(session, card);
    }

    private AgentSession GetOrCreateSession(string source, string sessionId, string token)
    {
        if (!string.IsNullOrWhiteSpace(token) &&
            _sessionsByToken.TryGetValue(token, out var tokenSession))
        {
            if (!IsPendingLiveShelfSessionId(sessionId) &&
                !string.Equals(tokenSession.SessionId, sessionId, StringComparison.OrdinalIgnoreCase))
            {
                _sessionsByKey.Remove(tokenSession.Key);
                tokenSession.SessionId = sessionId;
            }

            if (!_sessionsByKey.ContainsKey(tokenSession.Key))
            {
                _sessionsByKey[tokenSession.Key] = tokenSession;
            }

            return tokenSession;
        }

        var key = AgentSession.BuildKey(source, sessionId);
        if (!_sessionsByKey.TryGetValue(key, out var session))
        {
            session = new AgentSession(source, sessionId);
            _sessionsByKey[key] = session;
        }

        if (!string.IsNullOrWhiteSpace(token))
        {
            session.LiveShelfAgentToken = token;
            _sessionsByToken[token] = session;
        }

        return session;
    }

    private void ApplyTokenRoutedSession(AgentSession session)
    {
        var card = _getCards().FirstOrDefault(candidate =>
            string.Equals(candidate.LiveShelfAgentToken, session.LiveShelfAgentToken, StringComparison.OrdinalIgnoreCase));
        if (card is not null)
        {
            LinkSessionToCard(session, card);
            TraceRoute(session, "token", card, "updated");
            return;
        }

        card = FindCardForPendingTokenSession(session);
        if (card is not null)
        {
            LinkSessionToCard(session, card);
            TraceRoute(session, "token-claim", card, "updated");
            return;
        }

        TraceRoute(session, "token", null, "pending");
    }

    private AgentSession? FindTokenSessionForCard(ShelvedWindow card) =>
        _sessionsByToken.Values
            .Where(session => string.IsNullOrWhiteSpace(session.LinkedCardId))
            .Where(session => IsPendingTokenSessionCandidate(card, session))
            .OrderByDescending(session => ScoreTokenSessionCandidate(card, session))
            .FirstOrDefault();

    private ShelvedWindow? FindCardForPendingTokenSession(AgentSession session) =>
        _getCards()
            .Where(card => !card.HasLinkedAgentSession || string.Equals(card.LiveShelfAgentToken, session.LiveShelfAgentToken, StringComparison.OrdinalIgnoreCase))
            .Where(card => IsPendingTokenSessionCandidate(card, session))
            .OrderByDescending(card => ScoreTokenSessionCandidate(card, session))
            .FirstOrDefault();

    private static bool IsPendingTokenSessionCandidate(ShelvedWindow card, AgentSession session) =>
        !string.IsNullOrWhiteSpace(session.LiveShelfAgentToken) &&
        ScoreTokenSessionCandidate(card, session) >= ProcessMatchScore;

    private static int ScoreTokenSessionCandidate(ShelvedWindow card, AgentSession session)
    {
        var score = 0;
        if (!string.IsNullOrWhiteSpace(card.LiveShelfAgentToken) &&
            string.Equals(card.LiveShelfAgentToken, session.LiveShelfAgentToken, StringComparison.OrdinalIgnoreCase))
        {
            score += LinkedKeyScore;
        }

        if (!string.IsNullOrWhiteSpace(card.TerminalSessionId) &&
            !string.IsNullOrWhiteSpace(session.TerminalSessionId) &&
            string.Equals(card.TerminalSessionId, session.TerminalSessionId, StringComparison.OrdinalIgnoreCase))
        {
            score += TerminalSessionScore;
        }

        if (card.SourceProcessId > 0 && card.SourceProcessId == session.ForegroundProcessId)
        {
            score += ForegroundProcessScore;
        }

        if (CardMatchesSessionProcess(card, session))
        {
            score += ProcessMatchScore;
        }

        if (!string.IsNullOrWhiteSpace(session.LaunchCwd) &&
            !string.IsNullOrWhiteSpace(card.PossibleCwd) &&
            SamePath(session.LaunchCwd, card.PossibleCwd))
        {
            score += CwdMatchScore;
        }

        return score;
    }

    private void TryAttachModeAutoLinkSession(AgentSession session)
    {
        var strongCandidates = _getCards()
            .Where(card => !card.HasLinkedAgentSession)
            .Select(card => new AgentCardCandidate(card, ScoreStrongAgentCardMatch(card, session)))
            .Where(candidate => candidate.Score > 0)
            .OrderByDescending(candidate => candidate.Score)
            .ToList();

        if (TryChooseAutoLink(strongCandidates, out var cardToLink))
        {
            LinkSessionToCard(session, cardToLink);
            TraceRoute(session, "attach scoring", cardToLink, "updated");
            return;
        }

        var hintCandidates = _getCards()
            .Where(card => !card.HasLinkedAgentSession)
            .Select(card => new AgentCardCandidate(card, ScoreAgentCardHint(card, session)))
            .Where(candidate => candidate.Score > 0)
            .OrderByDescending(candidate => candidate.Score)
            .ToList();

        var promptCandidates = strongCandidates.Count > 0 ? strongCandidates : hintCandidates;
        if (promptCandidates.Count > 0)
        {
            Trace.WriteLine(
                $"LiveShelf route=attach-prompt key={session.Key} strongCandidates={strongCandidates.Count} hintCandidates={hintCandidates.Count} cwd={session.Cwd} processIds={string.Join(',', session.RelatedProcessIds)} scores={FormatScores(promptCandidates)}");
            if (_ambiguousPromptedKeys.Add(session.Key))
            {
                AmbiguousLinkDetected?.Invoke(
                    this,
                    new AgentLinkAmbiguousEventArgs(
                        session,
                        promptCandidates.Take(2).ToArray()));
            }

            return;
        }

        Trace.WriteLine(
            $"LiveShelf route=pending key={session.Key} token={session.LiveShelfAgentToken} cwd={session.Cwd} processIds={string.Join(',', session.RelatedProcessIds)}");
    }

    private void ApplyLinkedCard(AgentSession session, ShelvedWindow card)
    {
        if (!string.Equals(card.LinkedAgentKey, session.Key, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

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
        session.LiveShelfAgentToken = FirstNonEmpty(agentEvent.LiveShelfAgentToken, session.LiveShelfAgentToken);
        session.TerminalSessionId = FirstNonEmpty(agentEvent.TerminalSessionId, session.TerminalSessionId);
        session.LaunchCwd = FirstNonEmpty(agentEvent.LaunchCwd, session.LaunchCwd);
        session.TermProgram = FirstNonEmpty(agentEvent.TermProgram, session.TermProgram);
        if (agentEvent.ForegroundProcessId > 0)
        {
            session.ForegroundProcessId = agentEvent.ForegroundProcessId;
        }

        session.TranscriptPath = FirstNonEmpty(agentEvent.TranscriptPath, session.TranscriptPath);
        AddRelatedProcessIds(session, agentEvent);

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
            case "agentturncomplete":
            case "turncomplete":
                session.Status = AgentSessionStatus.Done;
                session.LastStopAtUtc = session.LastEventAtUtc;
                break;

            case "approvalrequest":
            case "agentapprovalrequest":
                session.Status = AgentSessionStatus.Waiting;
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

    private static void AddRelatedProcessIds(AgentSession session, AgentEvent agentEvent)
    {
        AddRelatedProcessId(session, agentEvent.ProcessId);
        AddRelatedProcessId(session, agentEvent.LiveShelfParentProcessId);
        foreach (var processId in agentEvent.ParentProcessIds)
        {
            AddRelatedProcessId(session, processId);
        }

        AddRelatedProcessId(session, agentEvent.ForegroundProcessId);
    }

    private static void AddRelatedProcessId(AgentSession session, int processId)
    {
        if (processId > 0)
        {
            session.RelatedProcessIds.Add(processId);
        }
    }

    private static bool CardMatchesSessionProcess(ShelvedWindow card, AgentSession session) =>
        card.SourceProcessId > 0 && session.RelatedProcessIds.Contains(card.SourceProcessId);

    private static int ScoreStrongAgentCardMatch(ShelvedWindow card, AgentSession session)
    {
        var score = 0;
        if (string.Equals(card.LinkedAgentKey, session.Key, StringComparison.OrdinalIgnoreCase))
        {
            score += LinkedKeyScore;
        }

        if (!string.IsNullOrWhiteSpace(card.TerminalSessionId) &&
            !string.IsNullOrWhiteSpace(session.TerminalSessionId) &&
            string.Equals(card.TerminalSessionId, session.TerminalSessionId, StringComparison.OrdinalIgnoreCase))
        {
            score += TerminalSessionScore;
        }

        if (card.SourceProcessId > 0 && card.SourceProcessId == session.ForegroundProcessId)
        {
            score += ForegroundProcessScore;
        }

        if (CardMatchesSessionProcess(card, session))
        {
            score += ProcessMatchScore;
        }

        if (!string.IsNullOrWhiteSpace(card.PossibleCwd) &&
            !string.IsNullOrWhiteSpace(session.Cwd) &&
            SamePath(card.PossibleCwd, session.Cwd))
        {
            score += CwdMatchScore;
        }
        else if (!string.IsNullOrWhiteSpace(session.Cwd) &&
                 TitleContainsCwdFolder(card.Title, session.Cwd) &&
                 CardMatchesSessionSource(card, session.Source))
        {
            score += TitleCwdMatchScore;
        }

        return score;
    }

    private static int ScoreAgentCardHint(ShelvedWindow card, AgentSession session) =>
        CardMatchesSessionSource(card, session.Source) ? SourceHintScore : 0;

    private static bool CardMatchesSessionSource(ShelvedWindow card, string source)
    {
        var cardSource = FirstNonEmpty(card.SuspectedAgent, InferSourceFromCard(card));
        return !string.IsNullOrWhiteSpace(cardSource) &&
               string.Equals(cardSource, source, StringComparison.OrdinalIgnoreCase);
    }

    private static string InferSourceFromCard(ShelvedWindow card)
    {
        var text = $"{card.ProcessName} {card.ExePath} {card.Title}";
        if (text.Contains("codex", StringComparison.OrdinalIgnoreCase))
        {
            return "codex";
        }

        return text.Contains("claude", StringComparison.OrdinalIgnoreCase) ? "claude" : string.Empty;
    }

    private static bool TryChooseAutoLink(
        IReadOnlyList<AgentCardCandidate> candidates,
        out ShelvedWindow card)
    {
        card = null!;
        var best = candidates.FirstOrDefault();
        if (best is null || best.Score < AutoLinkScore)
        {
            return false;
        }

        var second = candidates.Skip(1).FirstOrDefault();
        if (second is not null && best.Score - second.Score < ClearWinnerMargin)
        {
            return false;
        }

        card = best.Card;
        return true;
    }

    private static bool TryChooseAutoLink(
        IReadOnlyList<AgentSessionCandidate> candidates,
        out AgentSession session)
    {
        session = null!;
        var best = candidates.FirstOrDefault();
        if (best is null || best.Score < AutoLinkScore)
        {
            return false;
        }

        var second = candidates.Skip(1).FirstOrDefault();
        if (second is not null && best.Score - second.Score < ClearWinnerMargin)
        {
            return false;
        }

        session = best.Session;
        return true;
    }

    private static string FormatScores(IEnumerable<AgentCardCandidate> candidates) =>
        string.Join(
            "; ",
            candidates.Select(candidate =>
                $"{candidate.Card.Id}:{candidate.Score}:title={candidate.Card.Title}:cwd={candidate.Card.PossibleCwd}:token={candidate.Card.LiveShelfAgentToken}"));

    private static void TraceRoute(AgentSession session, string route, ShelvedWindow? card, string result)
    {
        Trace.WriteLine(
            $"LiveShelf route={route} result={result} key={session.Key} token={session.LiveShelfAgentToken} sessionId={session.SessionId} terminalSession={session.TerminalSessionId} launchCwd={session.LaunchCwd} cwd={session.Cwd} cardId={card?.Id ?? ""} linkedCardId={session.LinkedCardId}");
    }

    private static bool HasReliableSessionId(AgentEvent agentEvent, string sessionId)
    {
        if (!string.IsNullOrWhiteSpace(agentEvent.LiveShelfAgentToken) &&
            IsPendingLiveShelfSessionId(sessionId))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(sessionId) ||
            sessionId.Equals("unknown", StringComparison.OrdinalIgnoreCase) ||
            sessionId.EndsWith(":unknown", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(agentEvent.Cwd) &&
            SamePath(sessionId, agentEvent.Cwd))
        {
            return false;
        }

        return true;
    }

    private static bool IsPendingLiveShelfSessionId(string sessionId) =>
        sessionId.StartsWith("liveshelf-pending-", StringComparison.OrdinalIgnoreCase);

    private static bool TitleContainsCwdFolder(string title, string cwd)
    {
        var folderName = Path.GetFileName(cwd.TrimEnd('\\', '/'));
        return !string.IsNullOrWhiteSpace(folderName) &&
               title.Contains(folderName, StringComparison.OrdinalIgnoreCase);
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
