using System.Collections.ObjectModel;
using System.Windows.Threading;

namespace LiveShelf;

internal sealed class WindowShelver
{
    private static readonly TimeSpan InitialFocusSuppression = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ContentProbeInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan AgentStableDoneDelay = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan StableDoneDelay = TimeSpan.FromSeconds(9);

    private readonly IntPtr _shelfHwnd;
    private readonly ObservableCollection<ShelvedWindow> _items;
    private readonly DispatcherTimer _monitorTimer;
    private IntPtr _lastForegroundWindow;
    private bool _thumbnailsVisible = true;

    public WindowShelver(IntPtr shelfHwnd, ObservableCollection<ShelvedWindow> items)
    {
        _shelfHwnd = shelfHwnd;
        _items = items;

        _monitorTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _monitorTimer.Tick += MonitorTimer_Tick;
        _monitorTimer.Start();
    }

    public event EventHandler<string>? StatusChanged;

    public event EventHandler? ThumbnailRefreshRequested;

    public event EventHandler<ShelvedWindow>? AttentionRequested;

    public void ShelfForegroundWindow()
    {
        var sourceHwnd = NativeMethods.GetForegroundWindow();

        if (_items.Any(item => item.SourceHwnd == sourceHwnd))
        {
            throw new InvalidOperationException("Window is already shelved");
        }

        if (!NativeMethods.IsNormalAppWindow(sourceHwnd, _shelfHwnd, out var reason))
        {
            throw new InvalidOperationException(reason);
        }

        var placement = NativeMethods.WINDOWPLACEMENT.Create();
        if (!NativeMethods.GetWindowPlacement(sourceHwnd, ref placement))
        {
            NativeMethods.ThrowLastWin32Error("GetWindowPlacement");
        }

        if (!NativeMethods.GetWindowRect(sourceHwnd, out var currentRect))
        {
            NativeMethods.ThrowLastWin32Error("GetWindowRect");
        }

        var title = NativeMethods.GetWindowTitle(sourceHwnd);
        var processName = NativeMethods.GetProcessName(sourceHwnd);

        var registerResult = NativeMethods.DwmRegisterThumbnail(_shelfHwnd, sourceHwnd, out var thumbnailHandle);
        NativeMethods.ThrowForHResult("DwmRegisterThumbnail", registerResult);

        var item = new ShelvedWindow(sourceHwnd, thumbnailHandle, placement, title, processName);
        item.SuppressFocusAlertsUntilUtc = DateTime.UtcNow.Add(InitialFocusSuppression);
        item.IsAgentLikeSession = LooksLikeAgentSession(processName, title);

        try
        {
            ParkSourceWindow(sourceHwnd, currentRect);
            _items.Add(item);
            StatusChanged?.Invoke(this, $"Shelved {item.ProcessName}");
            ThumbnailRefreshRequested?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
            NativeMethods.DwmUnregisterThumbnail(thumbnailHandle);
            throw;
        }
    }

    public void Restore(ShelvedWindow item)
    {
        if (!_items.Contains(item))
        {
            return;
        }

        EndInteractiveZoom(item);
        UnregisterThumbnail(item);

        if (NativeMethods.IsWindow(item.SourceHwnd))
        {
            var placement = item.OriginalPlacement;
            placement.Length = NativeMethods.WINDOWPLACEMENT.Create().Length;

            NativeMethods.SetWindowPlacement(item.SourceHwnd, ref placement);
            NativeMethods.ShowWindow(item.SourceHwnd, NativeMethods.SW_RESTORE);
            NativeMethods.SetForegroundWindow(item.SourceHwnd);
        }

        _items.Remove(item);
        StatusChanged?.Invoke(this, "Ready");
    }

    public void Remove(ShelvedWindow item, bool restoreIfAlive)
    {
        if (!_items.Contains(item))
        {
            return;
        }

        EndInteractiveZoom(item);
        UnregisterThumbnail(item);

        if (restoreIfAlive && NativeMethods.IsWindow(item.SourceHwnd))
        {
            var placement = item.OriginalPlacement;
            placement.Length = NativeMethods.WINDOWPLACEMENT.Create().Length;
            NativeMethods.SetWindowPlacement(item.SourceHwnd, ref placement);
            NativeMethods.ShowWindow(item.SourceHwnd, NativeMethods.SW_RESTORE);
        }

        _items.Remove(item);
        StatusChanged?.Invoke(this, "Ready");
    }

    public void CloseSource(ShelvedWindow item)
    {
        if (!_items.Contains(item))
        {
            return;
        }

        EndInteractiveZoom(item);
        UnregisterThumbnail(item);

        if (NativeMethods.IsWindow(item.SourceHwnd))
        {
            NativeMethods.PostMessageW(item.SourceHwnd, NativeMethods.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        }

        _items.Remove(item);
        StatusChanged?.Invoke(this, "Ready");
    }

    public void RestoreAll()
    {
        _monitorTimer.Stop();

        foreach (var item in _items.ToArray())
        {
            Restore(item);
        }
    }

    public void SetThumbnailsVisible(bool visible)
    {
        if (_thumbnailsVisible == visible)
        {
            return;
        }

        _thumbnailsVisible = visible;

        foreach (var item in _items)
        {
            UpdateThumbnailVisibility(item, visible);
        }

        if (visible)
        {
            ThumbnailRefreshRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    public void UpdateThumbnailDestination(ShelvedWindow item, NativeMethods.RECT destination)
    {
        if (item.ThumbnailHandle == IntPtr.Zero || !item.IsSourceAlive)
        {
            return;
        }

        var queryResult = NativeMethods.DwmQueryThumbnailSourceSize(item.ThumbnailHandle, out var sourceSize);
        if (queryResult >= 0)
        {
            destination = NativeMethods.FitInside(destination, sourceSize);
        }

        var properties = new NativeMethods.DWM_THUMBNAIL_PROPERTIES
        {
            dwFlags =
                NativeMethods.DWM_TNP_RECTDESTINATION |
                NativeMethods.DWM_TNP_VISIBLE |
                NativeMethods.DWM_TNP_OPACITY |
                NativeMethods.DWM_TNP_SOURCECLIENTAREAONLY,
            rcDestination = destination,
            opacity = _thumbnailsVisible ? (byte)255 : (byte)0,
            fVisible = _thumbnailsVisible,
            fSourceClientAreaOnly = false
        };

        var updateResult = NativeMethods.DwmUpdateThumbnailProperties(item.ThumbnailHandle, ref properties);
        if (updateResult < 0)
        {
            item.IsSourceAlive = NativeMethods.IsWindow(item.SourceHwnd);
        }
    }

    public void BeginInteractiveZoom(ShelvedWindow item, NativeMethods.RECT screenBounds)
    {
        if (!_items.Contains(item) || !item.IsSourceAlive || !NativeMethods.IsWindow(item.SourceHwnd))
        {
            return;
        }

        item.IsInteractive = true;
    }

    public void UpdateInteractiveZoomBounds(ShelvedWindow item, NativeMethods.RECT screenBounds)
    {
        return;
    }

    public void EndInteractiveZoom(ShelvedWindow item)
    {
        if (!item.IsInteractive)
        {
            return;
        }

        item.IsInteractive = false;
    }

    public void ForwardMouseInput(
        ShelvedWindow item,
        int message,
        int keyState,
        double x,
        double y,
        double previewWidth,
        double previewHeight,
        int wheelDelta = 0,
        double screenX = 0,
        double screenY = 0)
    {
        if (!CanForwardInput(item) || previewWidth <= 0 || previewHeight <= 0)
        {
            return;
        }

        if (!TryMapPreviewPointToSource(item, x, y, previewWidth, previewHeight, out var sourceX, out var sourceY))
        {
            return;
        }

        var target = GetInputTarget(item, sourceX, sourceY, out var targetX, out var targetY);
        item.LastInputTargetHwnd = target;

        var wParam = message == NativeMethods.WM_MOUSEWHEEL
            ? MakeWheelWParam(keyState, wheelDelta)
            : new IntPtr(keyState);
        var lParam = message == NativeMethods.WM_MOUSEWHEEL
            ? MakeLParam((int)Math.Round(screenX), (int)Math.Round(screenY))
            : MakeLParam(targetX, targetY);

        NativeMethods.PostMessageW(target, (uint)message, wParam, lParam);
    }

    public void ForwardKeyInput(ShelvedWindow item, int message, int virtualKey)
    {
        if (!CanForwardInput(item))
        {
            return;
        }

        NativeMethods.PostMessageW(GetKeyboardTarget(item), (uint)message, new IntPtr(virtualKey), IntPtr.Zero);
    }

    public void ForwardCharInput(ShelvedWindow item, char character)
    {
        if (!CanForwardInput(item))
        {
            return;
        }

        NativeMethods.PostMessageW(GetKeyboardTarget(item), NativeMethods.WM_CHAR, new IntPtr(character), IntPtr.Zero);
    }

    private static void UpdateThumbnailVisibility(ShelvedWindow item, bool visible)
    {
        if (item.ThumbnailHandle == IntPtr.Zero || !item.IsSourceAlive)
        {
            return;
        }

        var properties = new NativeMethods.DWM_THUMBNAIL_PROPERTIES
        {
            dwFlags = NativeMethods.DWM_TNP_VISIBLE | NativeMethods.DWM_TNP_OPACITY,
            opacity = visible ? (byte)255 : (byte)0,
            fVisible = visible
        };

        NativeMethods.DwmUpdateThumbnailProperties(item.ThumbnailHandle, ref properties);
    }

    private static void ParkSourceWindow(IntPtr sourceHwnd, NativeMethods.RECT currentRect)
    {
        var virtualScreen = NativeMethods.GetVirtualScreenRect();
        var width = Math.Max(currentRect.Width, 320);
        var height = Math.Max(currentRect.Height, 240);
        var parkedX = virtualScreen.Right + 96;
        var parkedY = virtualScreen.Top + 96;

        NativeMethods.ShowWindow(sourceHwnd, NativeMethods.SW_SHOWNOACTIVATE);

        NativeMethods.SetWindowPos(
            sourceHwnd,
            NativeMethods.HWND_NOTOPMOST,
            0,
            0,
            0,
            0,
            NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE);

        if (!NativeMethods.SetWindowPos(
                sourceHwnd,
                NativeMethods.HWND_BOTTOM,
                parkedX,
                parkedY,
                width,
                height,
                NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOOWNERZORDER | NativeMethods.SWP_SHOWWINDOW))
        {
            NativeMethods.ThrowLastWin32Error("SetWindowPos");
        }
    }

    private void MonitorTimer_Tick(object? sender, EventArgs e)
    {
        var foregroundWindow = NativeMethods.GetForegroundWindow();
        var virtualScreen = NativeMethods.GetVirtualScreenRect();
        var now = DateTime.UtcNow;

        foreach (var item in _items.ToArray())
        {
            if (!NativeMethods.IsWindow(item.SourceHwnd))
            {
                if (item.IsSourceAlive)
                {
                    item.IsSourceAlive = false;
                    SetBadgeAndAlert(item, ShelfBadgeKind.Closed);
                }

                UnregisterThumbnail(item);
                continue;
            }

            item.IsSourceAlive = true;

            if (!item.IsInteractive &&
                foregroundWindow == item.SourceHwnd &&
                _lastForegroundWindow != item.SourceHwnd &&
                now >= item.SuppressFocusAlertsUntilUtc)
            {
                SetBadgeAndAlert(item, ShelfBadgeKind.NeedsAttention);
            }

            if (!item.IsInteractive &&
                NativeMethods.GetWindowRect(item.SourceHwnd, out var sourceRect) &&
                IsMeaningfullyVisible(sourceRect, virtualScreen))
            {
                SetBadgeAndAlert(item, ShelfBadgeKind.NeedsAttention);
            }

            var title = NativeMethods.GetWindowTitle(item.SourceHwnd);
            var nextTitle = string.IsNullOrWhiteSpace(title) ? item.ProcessName : title;
            if (!string.Equals(item.Title, nextTitle, StringComparison.Ordinal))
            {
                item.Title = nextTitle;
                item.LastObservedChangeUtc = now;
                item.HasDetectedChange = true;
                item.HasReportedStable = false;
                item.IsAgentLikeSession |= LooksLikeAgentSession(item.ProcessName, item.Title);
                SetBadgeAndAlert(item, GetTitleChangeBadge(item));
            }

            ObserveWindowContent(item, now);
        }

        _lastForegroundWindow = foregroundWindow;
    }

    private void ObserveWindowContent(ShelvedWindow item, DateTime now)
    {
        if (now - item.LastStateProbeUtc < ContentProbeInterval)
        {
            return;
        }

        item.LastStateProbeUtc = now;

        var snapshot = WindowContentProbe.TryCapture(item.SourceHwnd);
        if (snapshot is null)
        {
            return;
        }

        var text = snapshot.Text;
        item.IsAgentLikeSession |= LooksLikeAgentSession(item.ProcessName, item.Title, text);

        var looksBusy = LooksBusy(text);
        if (looksBusy)
        {
            item.HasObservedBusySignal = true;
            item.HasDetectedChange = true;
            item.HasReportedStable = false;
            item.LastObservedChangeUtc = now;
        }

        if (!item.HasContentSnapshot)
        {
            item.HasContentSnapshot = true;
            item.LastContentHash = snapshot.Hash;
            if (item.IsAgentLikeSession && !looksBusy)
            {
                item.HasDetectedChange = true;
                item.HasReportedStable = false;
                item.LastObservedChangeUtc = now;
            }

            return;
        }

        if (snapshot.Hash != item.LastContentHash)
        {
            item.LastContentHash = snapshot.Hash;
            item.LastObservedChangeUtc = now;
            item.HasDetectedChange = true;
            item.HasReportedStable = false;

            var contentBadge = GetContentChangeBadge(item, text);
            if (contentBadge is not ShelfBadgeKind.None)
            {
                SetBadgeAndAlert(item, contentBadge);
            }

            return;
        }

        var doneDelay = item.IsAgentLikeSession ? AgentStableDoneDelay : StableDoneDelay;
        if (!item.HasDetectedChange ||
            item.HasReportedStable ||
            now - item.LastObservedChangeUtc < doneDelay ||
            !ShouldTreatStableContentAsDone(item, text))
        {
            return;
        }

        item.HasReportedStable = true;
        SetBadgeAndAlert(item, ShelfBadgeKind.Done);
    }

    private void SetBadgeAndAlert(ShelvedWindow item, ShelfBadgeKind badgeKind)
    {
        if (item.BadgeKind == badgeKind || !ShouldReplaceBadge(item.BadgeKind, badgeKind))
        {
            return;
        }

        item.SetBadge(badgeKind);
        AttentionRequested?.Invoke(this, item);
    }

    private static ShelfBadgeKind GetTitleChangeBadge(ShelvedWindow item)
    {
        if (LooksLikeNeedsAttention(item.Title))
        {
            return ShelfBadgeKind.NeedsAttention;
        }

        if (LooksDone(item.Title))
        {
            return ShelfBadgeKind.Done;
        }

        var processName = item.ProcessName;
        return IsBrowserProcess(processName)
            ? ShelfBadgeKind.Updated
            : ShelfBadgeKind.Changed;
    }

    private static ShelfBadgeKind GetContentChangeBadge(ShelvedWindow item, string text)
    {
        var recentText = Tail(text, 1800);
        if (LooksLikeNeedsAttention(recentText))
        {
            return ShelfBadgeKind.NeedsAttention;
        }

        if (LooksDone(recentText))
        {
            return ShelfBadgeKind.Done;
        }

        if (LooksBusy(text))
        {
            return ShelfBadgeKind.None;
        }

        if (item.IsAgentLikeSession && LooksPromptReady(text))
        {
            return ShelfBadgeKind.Done;
        }

        return IsBrowserProcess(item.ProcessName)
            ? ShelfBadgeKind.Updated
            : ShelfBadgeKind.Changed;
    }

    private static bool ShouldTreatStableContentAsDone(ShelvedWindow item, string text)
    {
        var recentText = Tail(text, 1800);
        if (LooksLikeNeedsAttention(recentText) || LooksBusy(text))
        {
            return false;
        }

        var isTerminalOrAgent = item.IsAgentLikeSession || IsTerminalProcess(item.ProcessName);
        return LooksDone(text) ||
               (item.IsAgentLikeSession && item.HasDetectedChange) ||
               (isTerminalOrAgent && LooksPromptReady(text)) ||
               (isTerminalOrAgent && item.HasObservedBusySignal);
    }

    private static bool ShouldReplaceBadge(ShelfBadgeKind current, ShelfBadgeKind next)
    {
        if (current == ShelfBadgeKind.Closed)
        {
            return false;
        }

        if (next == ShelfBadgeKind.Closed || next == ShelfBadgeKind.NeedsAttention)
        {
            return true;
        }

        if (current == ShelfBadgeKind.NeedsAttention && next != ShelfBadgeKind.Closed)
        {
            return false;
        }

        if (next == ShelfBadgeKind.Done)
        {
            return current is ShelfBadgeKind.None or ShelfBadgeKind.Changed or ShelfBadgeKind.Updated or ShelfBadgeKind.Done;
        }

        return true;
    }

    private static bool LooksDone(string title)
    {
        var recent = Tail(title, 2400);
        return recent.Contains("done", StringComparison.OrdinalIgnoreCase) ||
               recent.Contains("complete", StringComparison.OrdinalIgnoreCase) ||
               recent.Contains("completed", StringComparison.OrdinalIgnoreCase) ||
               recent.Contains("finished", StringComparison.OrdinalIgnoreCase) ||
               recent.Contains("success", StringComparison.OrdinalIgnoreCase) ||
               recent.Contains("succeeded", StringComparison.OrdinalIgnoreCase) ||
               recent.Contains("build succeeded", StringComparison.OrdinalIgnoreCase) ||
               recent.Contains("tests passed", StringComparison.OrdinalIgnoreCase) ||
               recent.Contains("all tests pass", StringComparison.OrdinalIgnoreCase) ||
               recent.Contains("changes made", StringComparison.OrdinalIgnoreCase) ||
               recent.Contains("no changes", StringComparison.OrdinalIgnoreCase) ||
               recent.Contains("nothing to do", StringComparison.OrdinalIgnoreCase) ||
               recent.Contains("task complete", StringComparison.OrdinalIgnoreCase) ||
               recent.Contains("100%", StringComparison.OrdinalIgnoreCase) ||
               recent.Contains("0:00", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeNeedsAttention(string title)
    {
        return title.Contains("error", StringComparison.OrdinalIgnoreCase) ||
               title.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
               title.Contains("failure", StringComparison.OrdinalIgnoreCase) ||
               title.Contains("cancel", StringComparison.OrdinalIgnoreCase) ||
               title.Contains("sign in", StringComparison.OrdinalIgnoreCase) ||
               title.Contains("password", StringComparison.OrdinalIgnoreCase) ||
               title.Contains("attention", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksBusy(string text)
    {
        var tail = Tail(text, 1800);
        var recentLines = GetRecentNonEmptyLines(tail, 8);
        var recent = string.Join('\n', recentLines);
        var lastLine = recentLines.LastOrDefault() ?? string.Empty;

        return recent.Contains("esc to interrupt", StringComparison.OrdinalIgnoreCase) ||
               recent.Contains("ctrl+c to interrupt", StringComparison.OrdinalIgnoreCase) ||
               recent.Contains("ctrl-c to interrupt", StringComparison.OrdinalIgnoreCase) ||
               recent.Contains("press esc", StringComparison.OrdinalIgnoreCase) ||
               recent.Contains("stop generating", StringComparison.OrdinalIgnoreCase) ||
               recent.Contains("interrupt", StringComparison.OrdinalIgnoreCase) ||
               recent.Contains("applying patch", StringComparison.OrdinalIgnoreCase) ||
               recent.Contains("calling tool", StringComparison.OrdinalIgnoreCase) ||
               recent.Contains("running command", StringComparison.OrdinalIgnoreCase) ||
               recent.Contains("executing", StringComparison.OrdinalIgnoreCase) ||
               recent.Contains("generating", StringComparison.OrdinalIgnoreCase) ||
               lastLine.Contains("thinking", StringComparison.OrdinalIgnoreCase) ||
               lastLine.Contains("working", StringComparison.OrdinalIgnoreCase) ||
               lastLine.Contains("running", StringComparison.OrdinalIgnoreCase) ||
               lastLine.Contains("editing", StringComparison.OrdinalIgnoreCase) ||
               lastLine.Contains("writing", StringComparison.OrdinalIgnoreCase) ||
               lastLine.Contains("reading", StringComparison.OrdinalIgnoreCase) ||
               lastLine.Contains("searching", StringComparison.OrdinalIgnoreCase) ||
               lastLine.Contains("waiting", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksPromptReady(string text)
    {
        foreach (var line in GetRecentNonEmptyLines(text, 5).Reverse())
        {
            if (LooksPromptReadyLine(line))
            {
                return true;
            }
        }

        return false;
    }

    private static bool LooksPromptReadyLine(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.Length is < 1 or > 180)
        {
            return false;
        }

        return trimmed.StartsWith("PS ", StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith("C:\\", StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith(">", StringComparison.Ordinal) ||
               trimmed.EndsWith("$", StringComparison.Ordinal) ||
               trimmed.EndsWith(">", StringComparison.Ordinal) ||
               ContainsCodePoint(trimmed, 0x203A) ||
               ContainsCodePoint(trimmed, 0x276F) ||
               ContainsCodePoint(trimmed, 0x279C) ||
               ContainsCodePoint(trimmed, 0x03BB) ||
               ContainsCodePoint(trimmed, 0x258C);
    }

    private static bool LooksLikeAgentSession(string processName, string title, string text = "")
    {
        return ContainsAgentName(processName) ||
               ContainsAgentName(title) ||
               ContainsAgentName(Tail(text, 4000));
    }

    private static bool ContainsAgentName(string text)
    {
        return text.Contains("codex", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("claude", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("claude code", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("opencode", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("aider", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("gemini cli", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("qwen code", StringComparison.OrdinalIgnoreCase);
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

    private static bool IsBrowserProcess(string processName)
    {
        return processName.Contains("chrome", StringComparison.OrdinalIgnoreCase) ||
               processName.Contains("msedge", StringComparison.OrdinalIgnoreCase) ||
               processName.Contains("firefox", StringComparison.OrdinalIgnoreCase) ||
               processName.Contains("brave", StringComparison.OrdinalIgnoreCase) ||
               processName.Contains("opera", StringComparison.OrdinalIgnoreCase);
    }

    private static string Tail(string text, int length)
    {
        return text.Length <= length ? text : text[^length..];
    }

    private static string GetLastNonEmptyLine(string text)
    {
        return GetRecentNonEmptyLines(text, 1).LastOrDefault() ?? string.Empty;
    }

    private static IReadOnlyList<string> GetRecentNonEmptyLines(string text, int maxLines)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var result = new List<string>(maxLines);
        for (var i = lines.Length - 1; i >= 0; i--)
        {
            if (!string.IsNullOrWhiteSpace(lines[i]))
            {
                result.Add(lines[i].Trim());
                if (result.Count >= maxLines)
                {
                    break;
                }
            }
        }

        result.Reverse();
        return result;
    }

    private static bool ContainsCodePoint(string text, int codePoint)
    {
        return text.Contains(char.ConvertFromUtf32(codePoint), StringComparison.Ordinal);
    }

    private static bool IsMeaningfullyVisible(NativeMethods.RECT rect, NativeMethods.RECT virtualScreen)
    {
        var left = Math.Max(rect.Left, virtualScreen.Left);
        var top = Math.Max(rect.Top, virtualScreen.Top);
        var right = Math.Min(rect.Right, virtualScreen.Right);
        var bottom = Math.Min(rect.Bottom, virtualScreen.Bottom);
        return right - left >= 96 && bottom - top >= 96;
    }

    private bool CanForwardInput(ShelvedWindow item)
    {
        return _items.Contains(item) &&
               item.IsInteractive &&
               item.IsSourceAlive &&
               NativeMethods.IsWindow(item.SourceHwnd);
    }

    private static IntPtr GetInputTarget(ShelvedWindow item, int sourceX, int sourceY, out int targetX, out int targetY)
    {
        var current = item.SourceHwnd;
        var point = new NativeMethods.POINT
        {
            X = sourceX,
            Y = sourceY
        };

        for (var depth = 0; depth < 6; depth++)
        {
            var child = NativeMethods.ChildWindowFromPointEx(
                current,
                point,
                NativeMethods.CWP_SKIPINVISIBLE | NativeMethods.CWP_SKIPDISABLED);

            if (child == IntPtr.Zero || child == current)
            {
                break;
            }

            NativeMethods.MapWindowPoints(current, child, ref point, 1);
            current = child;
        }

        targetX = point.X;
        targetY = point.Y;
        return current;
    }

    private static IntPtr GetKeyboardTarget(ShelvedWindow item)
    {
        return item.LastInputTargetHwnd != IntPtr.Zero && NativeMethods.IsWindow(item.LastInputTargetHwnd)
            ? item.LastInputTargetHwnd
            : item.SourceHwnd;
    }

    private static bool TryMapPreviewPointToSource(
        ShelvedWindow item,
        double x,
        double y,
        double previewWidth,
        double previewHeight,
        out int sourceX,
        out int sourceY)
    {
        sourceX = 0;
        sourceY = 0;

        var sourceSize = new NativeMethods.SIZE
        {
            Width = (int)Math.Round(previewWidth),
            Height = (int)Math.Round(previewHeight)
        };

        if (item.ThumbnailHandle != IntPtr.Zero)
        {
            NativeMethods.DwmQueryThumbnailSourceSize(item.ThumbnailHandle, out sourceSize);
        }

        if (sourceSize.Width <= 0 || sourceSize.Height <= 0)
        {
            return false;
        }

        var sourceRatio = sourceSize.Width / (double)sourceSize.Height;
        var previewRatio = previewWidth / previewHeight;
        double fittedLeft = 0;
        double fittedTop = 0;
        double fittedWidth = previewWidth;
        double fittedHeight = previewHeight;

        if (sourceRatio > previewRatio)
        {
            fittedHeight = previewWidth / sourceRatio;
            fittedTop = (previewHeight - fittedHeight) / 2;
        }
        else
        {
            fittedWidth = previewHeight * sourceRatio;
            fittedLeft = (previewWidth - fittedWidth) / 2;
        }

        if (x < fittedLeft || x > fittedLeft + fittedWidth || y < fittedTop || y > fittedTop + fittedHeight)
        {
            return false;
        }

        sourceX = Math.Clamp((int)Math.Round(((x - fittedLeft) / fittedWidth) * sourceSize.Width), 0, sourceSize.Width - 1);
        sourceY = Math.Clamp((int)Math.Round(((y - fittedTop) / fittedHeight) * sourceSize.Height), 0, sourceSize.Height - 1);
        return true;
    }

    private static IntPtr MakeLParam(int lowWord, int highWord)
    {
        return new IntPtr((highWord << 16) | (lowWord & 0xFFFF));
    }

    private static IntPtr MakeWheelWParam(int keyState, int wheelDelta)
    {
        return new IntPtr((wheelDelta << 16) | (keyState & 0xFFFF));
    }

    private static void UnregisterThumbnail(ShelvedWindow item)
    {
        if (item.ThumbnailHandle == IntPtr.Zero)
        {
            return;
        }

        NativeMethods.DwmUnregisterThumbnail(item.ThumbnailHandle);
        item.ThumbnailHandle = IntPtr.Zero;
    }
}
