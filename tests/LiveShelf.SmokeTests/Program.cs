using System.Reflection;
using System.IO;
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
    "normal source windows may still use automatic interactive mode",
    SourceWindowPolicyRules.SupportsAutomaticInteractiveMode(SourceWindowPolicy.Normal));
AssertTrue(
    "browser cards must default to preview mode",
    !SourceWindowPolicyRules.SupportsAutomaticInteractiveMode(SourceWindowPolicy.Browser));
AssertTrue(
    "media cards must default to preview mode",
    !SourceWindowPolicyRules.SupportsAutomaticInteractiveMode(SourceWindowPolicy.Media));
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

var liveInteractiveFlags = SourceWindowPolicyRules.GetLivePreviewInteractiveFlags();
AssertTrue(
    "live preview interactive positioning must preserve source width and height",
    (liveInteractiveFlags & NativeMethods.SWP_NOSIZE) != 0);
AssertTrue(
    "live preview interactive positioning must preserve source location",
    (liveInteractiveFlags & NativeMethods.SWP_NOMOVE) != 0);

var host = new NativeMethods.RECT(0, 0, 200, 200);
var wideSource = new NativeMethods.SIZE { Width = 1920, Height = 1080 };
var contained = DwmThumbnailLayout.ComputeContainDestination(host, wideSource);
AssertEqual("contain-fit should use full host width", 200, contained.Width);
AssertIntBetween("contain-fit should preserve wide aspect ratio", contained.Height, 112, 114);
AssertIntBetween("contain-fit should center vertically", contained.Top, 43, 44);

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
    possibleCwd: @"C:\Users\Evan Z\Desktop\Coding\stack");
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

var cwdMatchCard = CreateShelvedCard(
    "stack - Codex",
    "WindowsTerminal",
    "codex",
    sourceProcessId: 2201,
    possibleCwd: @"C:\Users\Evan Z\Desktop\Coding\stack");
var otherCodexCard = CreateShelvedCard(
    "other - Codex",
    "WindowsTerminal",
    "codex",
    sourceProcessId: 2202,
    possibleCwd: @"C:\Users\Evan Z\Desktop\Coding\other");
var cwdRegistry = new AgentSessionRegistry(() => [cwdMatchCard, otherCodexCard]);
var cwdUpdates = new List<AgentCardUpdate>();
cwdRegistry.CardUpdateRequested += (_, update) => cwdUpdates.Add(update);
cwdRegistry.ApplyEvent(new AgentEvent
{
    Source = "codex",
    SessionId = "session-cwd",
    EventName = "UserPromptSubmit",
    Cwd = @"C:\Users\Evan Z\Desktop\Coding\stack"
});
AssertEqual(
    "unique cwd Codex match should auto-link the matching shelved card",
    cwdMatchCard.Id,
    cwdUpdates.Single().Card.Id);

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
ensureCodexConfig?.Invoke(null, [notifyConfigPath, @"C:\Users\Evan Z\AppData\Local\LiveShelf\liveshelf-bridge.exe"]);
var notifyCodexConfig = File.ReadAllText(notifyConfigPath);
AssertTrue(
    "codex installer should register notify fallback through the bridge",
    notifyCodexConfig.Contains("notify = [\"C:\\\\Users\\\\Evan Z\\\\AppData\\\\Local\\\\LiveShelf\\\\liveshelf-bridge.exe\", \"--source\", \"codex\"]", StringComparison.Ordinal));
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
var shimCommand = buildCodexShimCommand?.Invoke(null, [@"C:\Users\Evan Z\AppData\Local\LiveShelf\liveshelf-codex-hook.cmd"]) as string;
AssertTrue(
    "codex shim command must use cmd call quoting for paths with spaces",
    shimCommand == "cmd.exe /d /c call \"C:\\Users\\Evan Z\\AppData\\Local\\LiveShelf\\liveshelf-codex-hook.cmd\"");

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
