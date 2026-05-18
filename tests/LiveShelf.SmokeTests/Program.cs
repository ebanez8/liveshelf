using System.Reflection;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using LiveShelf;

var failures = new List<string>();
var estimator = new MediaTimelineEstimator();
var duration = TimeSpan.FromMinutes(5);
var timelineAnchor = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(10);

var firstPlaying = estimator.Resolve(
    "video",
    TimeSpan.FromSeconds(20),
    duration,
    isPlaying: true,
    timelineAnchor,
    canControlPlayback: true);
AssertBetween("playing anchor should interpolate from LastUpdatedTime", firstPlaying, 29, 31);

Thread.Sleep(1100);
var stillPlaying = estimator.Resolve(
    "video",
    TimeSpan.FromSeconds(20),
    duration,
    isPlaying: true,
    timelineAnchor,
    canControlPlayback: true);
AssertTrue("playing timeline should keep advancing past the old 4 second cap", stillPlaying > firstPlaying + TimeSpan.FromMilliseconds(800));

var paused = estimator.Resolve(
    "video",
    TimeSpan.FromSeconds(25),
    duration,
    isPlaying: false,
    DateTimeOffset.UtcNow,
    canControlPlayback: true);
AssertBetween("pause should use reported position", paused, 24.9, 25.1);

Thread.Sleep(1100);
var stillPaused = estimator.Resolve(
    "video",
    TimeSpan.FromSeconds(25),
    duration,
    isPlaying: false,
    DateTimeOffset.UtcNow,
    canControlPlayback: true);
AssertBetween("paused timeline must not drift", stillPaused, 24.9, 25.1);

var resumedImmediately = estimator.Resolve(
    "video",
    TimeSpan.FromSeconds(25),
    duration,
    isPlaying: true,
    DateTimeOffset.UtcNow,
    canControlPlayback: true);
AssertBetween("resume should not jump before confirmation", resumedImmediately, 24.9, 25.2);

Thread.Sleep(1000);
var resumedAfterConfirmation = estimator.Resolve(
    "video",
    TimeSpan.FromSeconds(25),
    duration,
    isPlaying: true,
    DateTimeOffset.UtcNow,
    canControlPlayback: true);
AssertBetween("confirmation tick should re-anchor without jumping", resumedAfterConfirmation, 24.9, 25.2);

Thread.Sleep(1100);
var resumedAdvancing = estimator.Resolve(
    "video",
    TimeSpan.FromSeconds(25),
    duration,
    isPlaying: true,
    DateTimeOffset.UtcNow,
    canControlPlayback: true);
AssertTrue("confirmed resumed timeline should advance", resumedAdvancing > TimeSpan.FromSeconds(25.8));

var seekedBackward = estimator.Resolve(
    "video",
    TimeSpan.FromSeconds(3),
    duration,
    isPlaying: true,
    DateTimeOffset.UtcNow,
    canControlPlayback: true);
AssertBetween("backward seek should reset old interpolation anchor", seekedBackward, 2.9, 3.2);

AssertEqual(
    "chrome should use browser source policy",
    SourceWindowPolicy.Browser,
    SourceWindowPolicyRules.Classify("chrome", isMediaCard: false));
AssertEqual(
    "media status should override browser policy",
    SourceWindowPolicy.Media,
    SourceWindowPolicyRules.Classify("chrome", isMediaCard: true));
AssertEqual(
    "terminal should use normal source policy",
    SourceWindowPolicy.Normal,
    SourceWindowPolicyRules.Classify("WindowsTerminal", isMediaCard: false));
AssertEqual(
    "known media player should use media source policy before session matching",
    SourceWindowPolicy.Media,
    SourceWindowPolicyRules.Classify("vlc", isMediaCard: false));
AssertTrue(
    "media cards must park their source windows offscreen",
    !SourceWindowPolicyRules.RequiresSourceSizePreservation(SourceWindowPolicy.Media));

var liveParkFlags = SourceWindowPolicyRules.GetLivePreviewParkFlags();
AssertTrue(
    "live preview parking must preserve source width and height",
    (liveParkFlags & NativeMethods.SWP_NOSIZE) != 0);
AssertTrue(
    "live preview parking must not move browser/media source windows offscreen",
    (liveParkFlags & NativeMethods.SWP_NOMOVE) != 0);

var liveMoveToOriginalFlags = SourceWindowPolicyRules.GetLivePreviewMoveToOriginalFlags();
AssertTrue(
    "live preview policy transition must preserve source width and height",
    (liveMoveToOriginalFlags & NativeMethods.SWP_NOSIZE) != 0);
AssertTrue(
    "live preview policy transition may move back to the original rect",
    (liveMoveToOriginalFlags & NativeMethods.SWP_NOMOVE) == 0);

var host = new NativeMethods.RECT(0, 0, 200, 200);
var wideSource = new NativeMethods.SIZE { Width = 1920, Height = 1080 };
var contained = DwmThumbnailLayout.ComputeContainDestination(host, wideSource);
AssertEqual("contain-fit should use full host width", 200, contained.Width);
AssertIntBetween("contain-fit should preserve wide aspect ratio", contained.Height, 112, 114);
AssertIntBetween("contain-fit should center vertically", contained.Top, 43, 44);

var shortHost = new NativeMethods.RECT(0, 0, 240, 120);
var tallSource = new NativeMethods.SIZE { Width = 900, Height = 1600 };
var tallContained = DwmThumbnailLayout.ComputeContainDestination(shortHost, tallSource);
AssertEqual("contain-fit should use full host height for tall sources", 120, tallContained.Height);
AssertIntBetween("contain-fit should shrink tall source width", tallContained.Width, 67, 68);
AssertIntBetween("contain-fit should center tall source horizontally", tallContained.Left, 86, 87);

var originalBrowserRect = new NativeMethods.RECT(10, 20, 1610, 920);
var tinySource = new NativeMethods.SIZE { Width = 320, Height = 180 };
AssertTrue(
    "tiny top-left source size should be treated as a broken live preview",
    DwmThumbnailLayout.IsSeverelyWrongSourceSize(tinySource, originalBrowserRect));

var sourceOnlyCard = CreateShelvedCard(
    "stack - Codex",
    "WindowsTerminal",
    "codex",
    sourceProcessId: 1101,
    possibleCwd: @"C:\LiveShelfTest\stack");
var sourceOnlyRegistry = new AgentSessionRegistry(() => [sourceOnlyCard]);
var sourceOnlyUpdates = new List<AgentCardUpdate>();
var sourceOnlyPrompts = new List<AgentLinkAmbiguousEventArgs>();
sourceOnlyRegistry.CardUpdateRequested += (_, update) => sourceOnlyUpdates.Add(update);
sourceOnlyRegistry.AmbiguousLinkDetected += (_, prompt) => sourceOnlyPrompts.Add(prompt);
sourceOnlyRegistry.ApplyEvent(new AgentEvent
{
    Source = "codex",
    SessionId = "session-source-only",
    EventName = "UserPromptSubmit"
});
AssertEqual(
    "source-only Codex evidence should not auto-update a card",
    0,
    sourceOnlyUpdates.Count);
AssertEqual(
    "source-only Codex evidence should ask for a one-time link",
    1,
    sourceOnlyPrompts.Count);
sourceOnlyRegistry.LinkSessionToCard(sourceOnlyPrompts[0].Session, sourceOnlyCard);
sourceOnlyRegistry.ApplyEvent(new AgentEvent
{
    Source = "codex",
    SessionId = "session-source-only",
    EventName = "PreToolUse",
    ToolName = "shell_command"
});
AssertEqual(
    "user-selected Codex session link should keep receiving updates without process ancestry",
    2,
    sourceOnlyUpdates.Count);

sourceOnlyRegistry.ApplyEvent(new AgentEvent
{
    Source = "codex",
    SessionId = "different-session",
    EventName = "PreToolUse",
    ToolName = "shell_command"
});
AssertEqual(
    "different Codex session must not update an already-linked card",
    2,
    sourceOnlyUpdates.Count);

var lateCards = new List<ShelvedWindow>();
var lateSourceOnlyRegistry = new AgentSessionRegistry(() => lateCards);
var lateSourceOnlyUpdates = new List<AgentCardUpdate>();
lateSourceOnlyRegistry.CardUpdateRequested += (_, update) => lateSourceOnlyUpdates.Add(update);
lateSourceOnlyRegistry.ApplyEvent(new AgentEvent
{
    Source = "codex",
    SessionId = "session-before-card",
    EventName = "UserPromptSubmit"
});
var lateSourceOnlyCard = CreateShelvedCard(
    "late - Codex",
    "WindowsTerminal",
    "codex",
    sourceProcessId: 1201,
    possibleCwd: @"C:\LiveShelfTest\late");
lateCards.Add(lateSourceOnlyCard);
lateSourceOnlyRegistry.TryAutoLinkCard(lateSourceOnlyCard);
AssertEqual(
    "source-only prior Codex session should not auto-link a later shelved card",
    0,
    lateSourceOnlyUpdates.Count);
AssertTrue(
    "source-only prior Codex session should leave the later card unlinked",
    !lateSourceOnlyCard.HasLinkedAgentSession);

var cwdMatchCard = CreateShelvedCard(
    "stack - Codex",
    "WindowsTerminal",
    "codex",
    sourceProcessId: 2201,
    possibleCwd: @"C:\LiveShelfTest\stack");
var otherCodexCard = CreateShelvedCard(
    "other - Codex",
    "WindowsTerminal",
    "codex",
    sourceProcessId: 2202,
    possibleCwd: @"C:\LiveShelfTest\other");
var cwdRegistry = new AgentSessionRegistry(() => [cwdMatchCard, otherCodexCard]);
var cwdUpdates = new List<AgentCardUpdate>();
cwdRegistry.CardUpdateRequested += (_, update) => cwdUpdates.Add(update);
cwdRegistry.ApplyEvent(new AgentEvent
{
    Source = "codex",
    SessionId = "session-cwd",
    EventName = "UserPromptSubmit",
    Cwd = @"C:\LiveShelfTest\stack"
});
AssertEqual(
    "cwd-only Codex evidence should not auto-link because cwd is only an attach hint",
    0,
    cwdUpdates.Count);
AssertTrue(
    "cwd-only Codex evidence should leave both same-source cards unlinked",
    !cwdMatchCard.HasLinkedAgentSession && !otherCodexCard.HasLinkedAgentSession);

var titleCwdMatchCard = CreateShelvedCard(
    "CCC",
    "WindowsTerminal",
    "codex",
    sourceProcessId: 2251,
    possibleCwd: string.Empty);
var titleCwdRegistry = new AgentSessionRegistry(() => [titleCwdMatchCard]);
var titleCwdUpdates = new List<AgentCardUpdate>();
titleCwdRegistry.CardUpdateRequested += (_, update) => titleCwdUpdates.Add(update);
titleCwdRegistry.ApplyEvent(new AgentEvent
{
    Source = "codex",
    SessionId = "session-title-cwd",
    EventName = "UserPromptSubmit",
    Cwd = @"C:\LiveShelfTest\CCC"
});
AssertEqual(
    "title cwd evidence should not auto-link because terminal titles are only attach hints",
    0,
    titleCwdUpdates.Count);

var processMatchCard = CreateShelvedCard(
    "process - Codex",
    "WindowsTerminal",
    "codex",
    sourceProcessId: 3301,
    possibleCwd: string.Empty);
var processRegistry = new AgentSessionRegistry(() => [processMatchCard]);
var processUpdates = new List<AgentCardUpdate>();
processRegistry.CardUpdateRequested += (_, update) => processUpdates.Add(update);
processRegistry.ApplyEvent(new AgentEvent
{
    Source = "codex",
    SessionId = "session-process",
    EventName = "UserPromptSubmit",
    ParentProcessIds = [3301]
});
AssertEqual(
    "process ancestry alone should not auto-link below the conservative attach threshold",
    0,
    processUpdates.Count);

var foregroundMatchCard = CreateShelvedCard(
    "foreground - Codex",
    "WindowsTerminal",
    "codex",
    sourceProcessId: 4401,
    possibleCwd: string.Empty);
var foregroundRegistry = new AgentSessionRegistry(() => [foregroundMatchCard]);
var foregroundUpdates = new List<AgentCardUpdate>();
foregroundRegistry.CardUpdateRequested += (_, update) => foregroundUpdates.Add(update);
foregroundRegistry.ApplyEvent(new AgentEvent
{
    Source = "codex",
    SessionId = "session-foreground",
    EventName = "UserPromptSubmit",
    ForegroundProcessId = 4401
});
AssertEqual(
    "foreground process evidence should auto-link the matching shelved card",
    foregroundMatchCard.Id,
    foregroundUpdates.Single().Card.Id);

var linkedDoneCard = CreateShelvedCard(
    "linked done - Codex",
    "WindowsTerminal",
    "codex",
    sourceProcessId: 4451,
    possibleCwd: string.Empty);
linkedDoneCard.LinkedAgentKey = "codex:linked-done";
linkedDoneCard.SetHookedAgentStatus(ShelfBadgeKind.Done, "Done");
linkedDoneCard.MarkAttentionSeen();
AssertEqual(
    "zooming or peeking a linked agent completion should clear the done badge",
    ShelfBadgeKind.None,
    linkedDoneCard.BadgeKind);

var tokenCardA = CreateShelvedCard(
    "token A - Codex",
    "WindowsTerminal",
    "codex",
    sourceProcessId: 5101,
    possibleCwd: @"C:\repo");
tokenCardA.LiveShelfAgentToken = "token-A";
var tokenCardB = CreateShelvedCard(
    "token B - Codex",
    "WindowsTerminal",
    "codex",
    sourceProcessId: 5102,
    possibleCwd: @"C:\repo");
tokenCardB.LiveShelfAgentToken = "token-B";
var tokenRegistry = new AgentSessionRegistry(() => [tokenCardA, tokenCardB]);
var tokenUpdates = new List<AgentCardUpdate>();
tokenRegistry.CardUpdateRequested += (_, update) => tokenUpdates.Add(update);
tokenRegistry.ApplyEvent(new AgentEvent
{
    Source = "codex",
    SessionId = "session-token-A",
    LiveShelfAgentToken = "token-A",
    EventName = "UserPromptSubmit",
    Cwd = @"C:\repo"
});
tokenRegistry.ApplyEvent(new AgentEvent
{
    Source = "codex",
    SessionId = "session-token-B",
    LiveShelfAgentToken = "token-B",
    EventName = "UserPromptSubmit",
    Cwd = @"C:\repo"
});
AssertEqual(
    "guaranteed token routing should update exactly one card per token event",
    2,
    tokenUpdates.Count);
AssertEqual("token A event should update only card A", tokenCardA.Id, tokenUpdates[0].Card.Id);
AssertEqual("token B event should update only card B", tokenCardB.Id, tokenUpdates[1].Card.Id);
AssertEqual("token A card should link to the token session key", "codex:session-token-A", tokenCardA.LinkedAgentKey);
AssertEqual("token B card should link to the token session key", "codex:session-token-B", tokenCardB.LinkedAgentKey);

var sameCwdTokenCardA = CreateShelvedCard(
    "same cwd token A",
    "WindowsTerminal",
    "codex",
    sourceProcessId: 5201,
    possibleCwd: @"C:\same");
sameCwdTokenCardA.LiveShelfAgentToken = "same-token-A";
var sameCwdTokenCardB = CreateShelvedCard(
    "same cwd token B",
    "WindowsTerminal",
    "codex",
    sourceProcessId: 5202,
    possibleCwd: @"C:\same");
sameCwdTokenCardB.LiveShelfAgentToken = "same-token-B";
var sameCwdTokenRegistry = new AgentSessionRegistry(() => [sameCwdTokenCardA, sameCwdTokenCardB]);
var sameCwdTokenUpdates = new List<AgentCardUpdate>();
sameCwdTokenRegistry.CardUpdateRequested += (_, update) => sameCwdTokenUpdates.Add(update);
sameCwdTokenRegistry.ApplyEvent(new AgentEvent
{
    Source = "codex",
    SessionId = "same-session-A",
    LiveShelfAgentToken = "same-token-A",
    EventName = "UserPromptSubmit",
    Cwd = @"C:\same"
});
sameCwdTokenRegistry.ApplyEvent(new AgentEvent
{
    Source = "codex",
    SessionId = "same-session-B",
    LiveShelfAgentToken = "same-token-B",
    EventName = "UserPromptSubmit",
    Cwd = @"C:\same"
});
AssertEqual("same cwd token event A should update card A", sameCwdTokenCardA.Id, sameCwdTokenUpdates[0].Card.Id);
AssertEqual("same cwd token event B should update card B", sameCwdTokenCardB.Id, sameCwdTokenUpdates[1].Card.Id);

var sameCwdNoTokenCardA = CreateShelvedCard(
    "same cwd no token A",
    "WindowsTerminal",
    "codex",
    sourceProcessId: 5301,
    possibleCwd: @"C:\ambiguous");
var sameCwdNoTokenCardB = CreateShelvedCard(
    "same cwd no token B",
    "WindowsTerminal",
    "codex",
    sourceProcessId: 5302,
    possibleCwd: @"C:\ambiguous");
var sameCwdNoTokenRegistry = new AgentSessionRegistry(() => [sameCwdNoTokenCardA, sameCwdNoTokenCardB]);
var sameCwdNoTokenUpdates = new List<AgentCardUpdate>();
sameCwdNoTokenRegistry.CardUpdateRequested += (_, update) => sameCwdNoTokenUpdates.Add(update);
sameCwdNoTokenRegistry.ApplyEvent(new AgentEvent
{
    Source = "codex",
    SessionId = "same-cwd-no-token",
    EventName = "UserPromptSubmit",
    Cwd = @"C:\ambiguous"
});
AssertEqual(
    "same cwd without tokens should not update any card because attach mode is ambiguous",
    0,
    sameCwdNoTokenUpdates.Count);

var linkedCardA = CreateShelvedCard(
    "linked A",
    "WindowsTerminal",
    "codex",
    sourceProcessId: 5401,
    possibleCwd: string.Empty);
linkedCardA.LinkedAgentKey = "codex:A";
var linkedCardB = CreateShelvedCard(
    "linked B",
    "WindowsTerminal",
    "codex",
    sourceProcessId: 5402,
    possibleCwd: string.Empty);
linkedCardB.LinkedAgentKey = "codex:B";
var linkedRegistry = new AgentSessionRegistry(() => [linkedCardA, linkedCardB]);
var linkedUpdates = new List<AgentCardUpdate>();
linkedRegistry.CardUpdateRequested += (_, update) => linkedUpdates.Add(update);
linkedRegistry.ApplyEvent(new AgentEvent
{
    Source = "codex",
    SessionId = "A",
    EventName = "PreToolUse",
    ToolName = "shell_command"
});
AssertEqual("existing linked key should route to card A only", 1, linkedUpdates.Count);
AssertEqual("existing linked key should not broadcast to card B", linkedCardA.Id, linkedUpdates.Single().Card.Id);

var oldActiveCard = CreateShelvedCard(
    "old active - Codex",
    "WindowsTerminal",
    "codex",
    sourceProcessId: 5451,
    possibleCwd: string.Empty);
oldActiveCard.LinkedAgentKey = "codex:old-active";
var oldActiveRegistry = new AgentSessionRegistry(() => [oldActiveCard]);
var oldActiveUpdates = new List<AgentCardUpdate>();
oldActiveRegistry.CardUpdateRequested += (_, update) => oldActiveUpdates.Add(update);
oldActiveRegistry.ApplyEvent(new AgentEvent
{
    Source = "codex",
    SessionId = "old-active",
    EventName = "PreToolUse",
    ToolName = "shell_command",
    Timestamp = DateTimeOffset.UtcNow.AddMinutes(-10).ToUnixTimeMilliseconds()
});
AssertEqual(
    "old active linked sessions should not be converted to stalled failures",
    ShelfBadgeKind.RunningCommand,
    oldActiveUpdates.Single().Badge.Kind);

var stalledCard = CreateShelvedCard(
    "stalled - Codex",
    "WindowsTerminal",
    "codex",
    sourceProcessId: 5461,
    possibleCwd: string.Empty);
stalledCard.LinkedAgentKey = "codex:stalled";
var stalledRegistry = new AgentSessionRegistry(() => [stalledCard]);
var stalledUpdates = new List<AgentCardUpdate>();
stalledRegistry.CardUpdateRequested += (_, update) => stalledUpdates.Add(update);
stalledRegistry.ApplyEvent(new AgentEvent
{
    Source = "codex",
    SessionId = "stalled",
    EventName = "PreToolUse",
    ToolName = "shell_command"
});
var stalledSessionField = typeof(AgentSessionRegistry)
    .GetField("_sessionsByKey", BindingFlags.NonPublic | BindingFlags.Instance);
var stalledSessions = (System.Collections.IDictionary)stalledSessionField!.GetValue(stalledRegistry)!;
foreach (AgentSession session in stalledSessions.Values)
{
    session.LastEventReceivedAtUtc = DateTime.UtcNow.AddMinutes(-3);
}
stalledRegistry.SweepStaleSessions();
AssertEqual(
    "linked sessions that go silent for >2 min must flip to Stalled",
    ShelfBadgeKind.Failed,
    stalledUpdates[^1].Badge.Kind);
AssertEqual(
    "stalled badge label should read 'Stalled' so users distinguish kill from explicit failure",
    "Stalled",
    stalledUpdates[^1].Badge.Label);

var pendingTokenCards = new List<ShelvedWindow>();
var pendingTokenRegistry = new AgentSessionRegistry(() => pendingTokenCards);
var pendingTokenUpdates = new List<AgentCardUpdate>();
pendingTokenRegistry.CardUpdateRequested += (_, update) => pendingTokenUpdates.Add(update);
pendingTokenRegistry.ApplyEvent(new AgentEvent
{
    Source = "codex",
    SessionId = "liveshelf-pending-pending-token",
    LiveShelfAgentToken = "pending-token",
    EventName = "LiveShelfTrackedAgentStart",
    LaunchCwd = @"C:\pending",
    LiveShelfParentProcessId = 5501
});
AssertEqual("pending token event without a card should not update unrelated cards", 0, pendingTokenUpdates.Count);
var pendingTokenCard = CreateShelvedCard(
    "pending - Codex",
    "WindowsTerminal",
    "codex",
    sourceProcessId: 5501,
    possibleCwd: @"C:\pending");
pendingTokenCards.Add(pendingTokenCard);
pendingTokenRegistry.TryAutoLinkCard(pendingTokenCard);
AssertEqual("later shelved matching terminal should link to the pending token session", "pending-token", pendingTokenCard.LiveShelfAgentToken);
AssertEqual("pending token claim should update exactly the matching card", 1, pendingTokenUpdates.Count);
AssertEqual("pending token claim should not broadcast", pendingTokenCard.Id, pendingTokenUpdates.Single().Card.Id);

var installerType = typeof(AgentHookInstaller);
var ensureCodexHooksFeature = installerType.GetMethod(
    "EnsureCodexHooksFeature",
    BindingFlags.NonPublic | BindingFlags.Static);
var configPath = Path.Combine(AppContext.BaseDirectory, $"codex-config-{Guid.NewGuid():N}.toml");
File.WriteAllText(
    configPath,
    string.Join(
        Environment.NewLine,
        [
            "[features]",
            "codex_hooks = true",
            "prevent_idle_sleep = true",
            "",
            "[hooks.state]",
            "example = true",
            ""
        ]));
ensureCodexHooksFeature?.Invoke(null, [configPath]);
var migratedCodexConfig = File.ReadAllText(configPath);
AssertTrue(
    "codex hook installer must migrate to the current hooks feature flag",
    migratedCodexConfig.Contains("hooks = true", StringComparison.Ordinal));
AssertTrue(
    "codex hook installer must remove the deprecated codex_hooks feature flag",
    !migratedCodexConfig.Contains("codex_hooks", StringComparison.OrdinalIgnoreCase));
AssertTrue(
    "codex hook installer must preserve unrelated feature flags",
    migratedCodexConfig.Contains("prevent_idle_sleep = true", StringComparison.Ordinal));
AssertTrue(
    "codex hook installer must preserve following toml sections",
    migratedCodexConfig.Contains("[hooks.state]", StringComparison.Ordinal));

var ensureCodexConfig = installerType.GetMethod(
    "EnsureCodexConfig",
    BindingFlags.NonPublic | BindingFlags.Static);
var notifyConfigPath = Path.Combine(AppContext.BaseDirectory, $"codex-notify-config-{Guid.NewGuid():N}.toml");
File.WriteAllText(notifyConfigPath, "[features]" + Environment.NewLine + "prevent_idle_sleep = true" + Environment.NewLine);
ensureCodexConfig?.Invoke(null, [notifyConfigPath, @"C:\Users\Example\AppData\Local\LiveShelf\liveshelf-bridge.exe"]);
var notifyCodexConfig = File.ReadAllText(notifyConfigPath);
AssertTrue(
    "codex installer should register notify fallback through the bridge",
    notifyCodexConfig.Contains("notify = [\"C:\\\\Users\\\\Example\\\\AppData\\\\Local\\\\LiveShelf\\\\liveshelf-bridge.exe\", \"--source\", \"codex\"]", StringComparison.Ordinal));
AssertTrue(
    "codex installer should enable TUI notifications for notify fallback",
    notifyCodexConfig.Contains("notification_condition = \"always\"", StringComparison.Ordinal));

var isCodexTrackingInstalled = installerType.GetMethod(
    "IsCodexTrackingInstalled",
    BindingFlags.NonPublic | BindingFlags.Static);
var installedHooksPath = Path.Combine(AppContext.BaseDirectory, $"codex-hooks-{Guid.NewGuid():N}.json");
File.WriteAllText(
    installedHooksPath,
    """
    {"hooks":{"Stop":[{"hooks":[{"command":"liveshelf-bridge.exe --source codex"}]}]}}
    """);
AssertTrue(
    "codex tracking status should detect an installed notify fallback",
    isCodexTrackingInstalled?.Invoke(null, [notifyConfigPath, installedHooksPath]) is true);

var buildCodexShimCommand = installerType.GetMethod(
    "BuildCodexShimCommand",
    BindingFlags.NonPublic | BindingFlags.Static);
var shimCommand = buildCodexShimCommand?.Invoke(null, [@"C:\Users\Example\AppData\Local\LiveShelf\liveshelf-codex-hook.cmd"]) as string;
AssertTrue(
    "codex shim command must use cmd call quoting for paths with spaces",
    shimCommand == "cmd.exe /d /c call \"C:\\Users\\Example\\AppData\\Local\\LiveShelf\\liveshelf-codex-hook.cmd\"");

var buildCodexHooks = installerType.GetMethod(
    "BuildCodexHooks",
    BindingFlags.NonPublic | BindingFlags.Static);
var codexHooks = buildCodexHooks?.Invoke(null, ["cmd.exe /d /c call \"shim.cmd\""]) as JsonObject;
var hooksRoot = codexHooks?["hooks"] as JsonObject;
AssertTrue(
    "codex hooks should include SessionStart",
    hooksRoot?["SessionStart"] is JsonArray);
AssertTrue(
    "codex hooks should include PostToolUse",
    hooksRoot?["PostToolUse"] is JsonArray);
AssertTrue(
    "codex hooks should include StopFailure",
    hooksRoot?["StopFailure"] is JsonArray);
AssertTrue(
    "codex PostToolUse matcher should cover all Codex tool names",
    hooksRoot?["PostToolUse"]?[0]?["matcher"]?.GetValue<string>() == ".*");
AssertTrue(
    "codex hooks should call the non-failing command shim",
    hooksRoot?["PostToolUse"]?[0]?["hooks"]?[0]?["command"]?.GetValue<string>().Contains("shim.cmd", StringComparison.OrdinalIgnoreCase) == true);

var boundedRegistry = new AgentSessionRegistry(() => []);
for (var i = 0; i < 160; i++)
{
    boundedRegistry.ApplyEvent(new AgentEvent
    {
        Source = "codex",
        SessionId = "bounded",
        EventName = "PreToolUse",
        ToolName = "apply_patch",
        ToolUseId = $"tool-{i}",
        FilePath = $@"C:\LiveShelfTest\file-{i}.cs",
        ProcessId = 7000 + i,
        ParentProcessIds = [8000 + i]
    });
}

var boundedSession = boundedRegistry.Sessions.Single();
AssertTrue(
    "agent changed-file labels should stay bounded",
    boundedSession.ChangedFilesSinceTurnStart.Count <= 128);
AssertEqual(
    "agent changed-file estimate should preserve total count beyond label cap",
    160,
    boundedSession.ChangedFileCountEstimate);
AssertTrue(
    "agent active tool ids should stay bounded",
    boundedSession.ActiveToolUseIds.Count <= 64);
AssertTrue(
    "agent related process ids should stay bounded",
    boundedSession.RelatedProcessIds.Count <= 128);

using (var oversizedService = new AgentEventService())
{
    var receivedOversizedPayload = false;
    oversizedService.EventReceived += (_, _) => receivedOversizedPayload = true;
    var publishPayload = typeof(AgentEventService).GetMethod(
        "PublishPayload",
        BindingFlags.NonPublic | BindingFlags.Instance);
    var oversizedPayload = JsonSerializer.Serialize(new
    {
        source = "codex",
        sessionId = "oversized",
        eventName = "SessionStart",
        lastAssistantMessage = new string('x', AgentEventService.MaxPayloadChars)
    });
    publishPayload?.Invoke(oversizedService, [oversizedPayload]);
    AssertTrue(
        "oversized queued agent payloads should be skipped before deserialization",
        !receivedOversizedPayload);
}

if (failures.Count > 0)
{
    foreach (var failure in failures)
    {
        Console.Error.WriteLine(failure);
    }

    return 1;
}

Console.WriteLine("LiveShelf smoke tests passed.");
return 0;

void AssertTrue(string name, bool condition)
{
    if (!condition)
    {
        failures.Add(name);
    }
}

void AssertEqual<T>(string name, T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        failures.Add($"{name}: expected {expected}, got {actual}");
    }
}

void AssertIntBetween(string name, int actual, int min, int max)
{
    if (actual < min || actual > max)
    {
        failures.Add($"{name}: expected {min}-{max}, got {actual}");
    }
}

void AssertBetween(string name, TimeSpan actual, double minSeconds, double maxSeconds)
{
    var seconds = actual.TotalSeconds;
    if (seconds < minSeconds || seconds > maxSeconds)
    {
        failures.Add($"{name}: expected {minSeconds:0.0}-{maxSeconds:0.0}s, got {seconds:0.000}s");
    }
}

ShelvedWindow CreateShelvedCard(
    string title,
    string processName,
    string suspectedAgent,
    int sourceProcessId,
    string possibleCwd)
{
    var placement = NativeMethods.WINDOWPLACEMENT.Create();
    var rect = new NativeMethods.RECT(0, 0, 900, 600);
    var card = new ShelvedWindow(
        IntPtr.Zero,
        IntPtr.Zero,
        placement,
        title,
        processName,
        sourceProcessId,
        rect,
        SourceWindowPolicy.Normal);
    card.SuspectedAgent = suspectedAgent;
    card.PossibleCwd = possibleCwd;
    card.IsAgentLikeSession = true;
    return card;
}
