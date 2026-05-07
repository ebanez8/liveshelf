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
    private readonly Dispatcher _dispatcher;
    private readonly ShelfEventBridge _eventBridge;
    private readonly MediaSessionService _mediaSessionService;
    private IReadOnlyList<MediaSessionSnapshot> _latestMediaSessions = [];
    private IntPtr _lastForegroundWindow;
    private bool _thumbnailsVisible = true;
    private bool _isRestoringAll;

    public WindowShelver(IntPtr shelfHwnd, ObservableCollection<ShelvedWindow> items)
    {
        _shelfHwnd = shelfHwnd;
        _items = items;
        _dispatcher = Dispatcher.CurrentDispatcher;

        _monitorTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _monitorTimer.Tick += MonitorTimer_Tick;
        _monitorTimer.Start();

        _eventBridge = new ShelfEventBridge();
        _eventBridge.EventReceived += EventBridge_EventReceived;

        _mediaSessionService = new MediaSessionService();
        _mediaSessionService.SessionsChanged += MediaSessionService_SessionsChanged;
        _mediaSessionService.Start();
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
        var processId = NativeMethods.GetProcessId(sourceHwnd);

        var registerResult = NativeMethods.DwmRegisterThumbnail(_shelfHwnd, sourceHwnd, out var thumbnailHandle);
        NativeMethods.ThrowForHResult("DwmRegisterThumbnail", registerResult);

        var item = new ShelvedWindow(sourceHwnd, thumbnailHandle, placement, title, processName, processId);
        item.SuppressFocusAlertsUntilUtc = DateTime.UtcNow.Add(InitialFocusSuppression);
        item.IsAgentLikeSession = LooksLikeAgentSession(processName, title);

        try
        {
            ShelvedWindowRegistry.AddOrUpdate(item);
            ParkSourceWindow(sourceHwnd, currentRect);
            _items.Add(item);
            _mediaSessionService.RefreshSoon();
            StatusChanged?.Invoke(this, $"Shelved {item.ProcessName}");
            ThumbnailRefreshRequested?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
            ShelvedWindowRegistry.Remove(item);
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

        RestoreSourceWindow(item, activate: true);
        ShelvedWindowRegistry.Remove(item);

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
            RestoreSourceWindow(item, activate: false);
        }

        ShelvedWindowRegistry.Remove(item);

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

        ShelvedWindowRegistry.Remove(item);

        _items.Remove(item);
        StatusChanged?.Invoke(this, "Ready");
    }

    public void ToggleMediaPlayback(ShelvedWindow item)
    {
        if (!_items.Contains(item) || string.IsNullOrWhiteSpace(item.MediaSessionId))
        {
            return;
        }

        _ = _mediaSessionService.TogglePlayPauseAsync(item.MediaSessionId);
    }

    public void RestoreAll()
    {
        if (_isRestoringAll)
        {
            return;
        }

        _isRestoringAll = true;
        _monitorTimer.Stop();
        _eventBridge.Dispose();
        _mediaSessionService.Dispose();

        foreach (var item in _items.ToArray())
        {
            RestoreForShutdown(item);
        }

        StatusChanged?.Invoke(this, "Ready");
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

    private void RestoreForShutdown(ShelvedWindow item)
    {
        try
        {
            EndInteractiveZoom(item);
            UnregisterThumbnail(item);
            RestoreSourceWindow(item, activate: false);
            ShelvedWindowRegistry.Remove(item);
        }
        finally
        {
            _items.Remove(item);
        }
    }

    private static void RestoreSourceWindow(ShelvedWindow item, bool activate)
    {
        if (!NativeMethods.IsWindow(item.SourceHwnd))
        {
            return;
        }

        var placement = item.OriginalPlacement;
        placement.Length = NativeMethods.WINDOWPLACEMENT.Create().Length;

        var placementApplied = NativeMethods.SetWindowPlacement(item.SourceHwnd, ref placement);
        NativeMethods.ShowWindow(
            item.SourceHwnd,
            placementApplied ? GetRestoreShowCommand(placement.ShowCmd) : NativeMethods.SW_RESTORE);

        if (activate)
        {
            NativeMethods.SetForegroundWindow(item.SourceHwnd);
        }
    }

    private static int GetRestoreShowCommand(int originalShowCommand)
    {
        return originalShowCommand switch
        {
            NativeMethods.SW_SHOWMAXIMIZED => NativeMethods.SW_SHOWMAXIMIZED,
            NativeMethods.SW_SHOWMINIMIZED or NativeMethods.SW_MINIMIZE or NativeMethods.SW_SHOWMINNOACTIVE => NativeMethods.SW_RESTORE,
            NativeMethods.SW_SHOWNORMAL or NativeMethods.SW_SHOW or NativeMethods.SW_SHOWNA or NativeMethods.SW_SHOWNOACTIVATE => originalShowCommand,
            _ => NativeMethods.SW_RESTORE
        };
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
                if (!item.IsMediaCard)
                {
                    SetBadgeAndAlert(item, GetTitleChangeBadge(item));
                }

                ApplyMediaSessions(_latestMediaSessions);
            }

            ObserveWindowContent(item, now);
        }

        _lastForegroundWindow = foregroundWindow;
    }

    private void EventBridge_EventReceived(object? sender, ShelfBridgeEvent bridgeEvent)
    {
        _dispatcher.InvokeAsync(() => ApplyBridgeEvent(bridgeEvent));
    }

    private void MediaSessionService_SessionsChanged(
        object? sender,
        IReadOnlyList<MediaSessionSnapshot> sessions)
    {
        _dispatcher.InvokeAsync(() => ApplyMediaSessions(sessions));
    }

    private void ApplyBridgeEvent(ShelfBridgeEvent bridgeEvent)
    {
        var item = FindBridgeTarget(bridgeEvent);
        if (item is null)
        {
            return;
        }

        var badge = GetBridgeBadge(bridgeEvent);
        if (badge is ShelfBadgeKind.None)
        {
            return;
        }

        var now = DateTime.UtcNow;
        item.LastObservedChangeUtc = now;
        item.HasDetectedChange = true;

        if (IsRunningBadge(badge))
        {
            item.HasObservedBusySignal = true;
            item.HasReportedStable = false;
        }

        if (IsFinalBadge(badge))
        {
            item.HasReportedStable = true;
        }

        SetBadgeAndAlert(item, badge, force: true, BuildBridgeDetail(bridgeEvent));
    }

    private ShelvedWindow? FindBridgeTarget(ShelfBridgeEvent bridgeEvent)
    {
        if (bridgeEvent.Hwnd is > 0)
        {
            var hwnd = new IntPtr(bridgeEvent.Hwnd.Value);
            var byHwnd = _items.FirstOrDefault(item => item.SourceHwnd == hwnd);
            if (byHwnd is not null)
            {
                return byHwnd;
            }
        }

        if (bridgeEvent.EffectiveProcessId > 0)
        {
            var byProcessId = _items.FirstOrDefault(item => item.SourceProcessId == bridgeEvent.EffectiveProcessId);
            if (byProcessId is not null)
            {
                return byProcessId;
            }
        }

        if (!string.IsNullOrWhiteSpace(bridgeEvent.ProcessName))
        {
            var byProcessName = _items
                .Where(item => item.ProcessName.Contains(bridgeEvent.ProcessName, StringComparison.OrdinalIgnoreCase) ||
                               bridgeEvent.ProcessName.Contains(item.ProcessName, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(item => item.LastObservedChangeUtc)
                .FirstOrDefault();

            if (byProcessName is not null)
            {
                return byProcessName;
            }
        }

        if (!string.IsNullOrWhiteSpace(bridgeEvent.EffectiveTitle))
        {
            var byTitle = _items
                .Where(item => item.Title.Contains(bridgeEvent.EffectiveTitle, StringComparison.OrdinalIgnoreCase) ||
                               bridgeEvent.EffectiveTitle.Contains(item.Title, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(item => item.LastObservedChangeUtc)
                .FirstOrDefault();

            if (byTitle is not null)
            {
                return byTitle;
            }
        }

        if (IsAgentBridgeEvent(bridgeEvent))
        {
            return _items
                .Where(item => item.IsAgentLikeSession || LooksLikeAgentSession(item.ProcessName, item.Title))
                .OrderByDescending(item => item.LastObservedChangeUtc)
                .FirstOrDefault();
        }

        return null;
    }

    private void ApplyMediaSessions(IReadOnlyList<MediaSessionSnapshot> sessions)
    {
        _latestMediaSessions = sessions;

        if (_items.Count == 0)
        {
            return;
        }

        var assignedSessionIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in _items)
        {
            var match = FindBestMediaSessionMatch(item, sessions, assignedSessionIds);
            if (match is null)
            {
                item.ClearMediaStatus();
                continue;
            }

            assignedSessionIds.Add(match.SessionId);
            ApplyMediaStatus(item, match);
        }
    }

    private MediaSessionSnapshot? FindBestMediaSessionMatch(
        ShelvedWindow item,
        IReadOnlyList<MediaSessionSnapshot> sessions,
        HashSet<string> assignedSessionIds)
    {
        MediaSessionSnapshot? bestSession = null;
        var bestScore = 0;

        foreach (var session in sessions)
        {
            if (assignedSessionIds.Contains(session.SessionId) || !IsUsableMediaSession(session))
            {
                continue;
            }

            var score = ScoreMediaSessionMatch(item, session);
            if (score <= bestScore)
            {
                continue;
            }

            bestSession = session;
            bestScore = score;
        }

        if (bestSession is null)
        {
            return null;
        }

        var isBrowser = IsBrowserProcess(item.ProcessName);
        var threshold = isBrowser ? 48 : 32;
        if (isBrowser &&
            bestScore >= 34 &&
            CountShelvedBrowserWindowsForSession(bestSession) == 1 &&
            CountBrowserSessionsForSource(bestSession) == 1)
        {
            threshold = 34;
        }

        return bestScore >= threshold ? bestSession : null;
    }

    private int CountShelvedBrowserWindowsForSession(MediaSessionSnapshot session)
    {
        return _items.Count(item =>
            IsBrowserProcess(item.ProcessName) &&
            DoesSourceLookLikeProcess(session, item.ProcessName));
    }

    private int CountBrowserSessionsForSource(MediaSessionSnapshot session)
    {
        return _latestMediaSessions.Count(candidate =>
            IsSameMediaSource(candidate, session) &&
            (DoesSourceLookLikeProcess(candidate, "chrome") ||
             DoesSourceLookLikeProcess(candidate, "msedge") ||
             DoesSourceLookLikeProcess(candidate, "firefox") ||
             DoesSourceLookLikeProcess(candidate, "brave") ||
             DoesSourceLookLikeProcess(candidate, "opera")));
    }

    private static bool IsUsableMediaSession(MediaSessionSnapshot session)
    {
        return !session.IsStopped &&
               (!string.IsNullOrWhiteSpace(session.Title) ||
                session.IsPlaying ||
                session.IsPaused ||
                session.Duration > TimeSpan.FromSeconds(1));
    }

    private static int ScoreMediaSessionMatch(ShelvedWindow item, MediaSessionSnapshot session)
    {
        var score = 0;
        var processToken = NormalizeToken(item.ProcessName);
        var titleToken = NormalizeToken(item.Title);
        var sourceToken = NormalizeToken($"{session.SourceAppUserModelId} {session.SourceDisplayName}");
        var mediaTitleToken = NormalizeToken(session.Title);
        var artistToken = NormalizeToken(session.Artist);

        if (!string.IsNullOrWhiteSpace(processToken) &&
            (sourceToken.Contains(processToken, StringComparison.Ordinal) ||
             processToken.Contains(sourceToken, StringComparison.Ordinal) ||
             DoesSourceLookLikeProcess(session, item.ProcessName)))
        {
            score += 34;
        }

        if (!string.IsNullOrWhiteSpace(mediaTitleToken) &&
            titleToken.Contains(mediaTitleToken, StringComparison.Ordinal))
        {
            score += IsBrowserProcess(item.ProcessName) ? 52 : 36;
        }
        else if (!string.IsNullOrWhiteSpace(mediaTitleToken) &&
                 mediaTitleToken.Contains(titleToken, StringComparison.Ordinal) &&
                 titleToken.Length >= 12)
        {
            score += 28;
        }

        if (!string.IsNullOrWhiteSpace(artistToken) &&
            titleToken.Contains(artistToken, StringComparison.Ordinal))
        {
            score += 12;
        }

        if (item.IsMediaCard && item.MediaSessionId == session.SessionId)
        {
            score += 16;
        }

        if (session.IsCurrentSession)
        {
            score += 8;
        }

        if (IsBrowserProcess(item.ProcessName) &&
            ContainsKnownMediaSite(item.Title, session.Title))
        {
            score += 14;
        }

        return score;
    }

    private static bool DoesSourceLookLikeProcess(MediaSessionSnapshot session, string processName)
    {
        var source = NormalizeToken($"{session.SourceAppUserModelId} {session.SourceDisplayName}");
        var process = NormalizeToken(processName);
        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(process))
        {
            return false;
        }

        return source.Contains(process, StringComparison.Ordinal) ||
               process.Contains(source, StringComparison.Ordinal) ||
               (process == "msedge" && source.Contains("edge", StringComparison.Ordinal)) ||
               (process == "chrome" && source.Contains("chrome", StringComparison.Ordinal)) ||
               (process == "brave" && source.Contains("brave", StringComparison.Ordinal)) ||
               (process == "spotify" && source.Contains("spotify", StringComparison.Ordinal)) ||
               (process == "vlc" && source.Contains("vlc", StringComparison.Ordinal));
    }

    private static bool IsSameMediaSource(MediaSessionSnapshot first, MediaSessionSnapshot second)
    {
        var firstSource = NormalizeToken($"{first.SourceAppUserModelId} {first.SourceDisplayName}");
        var secondSource = NormalizeToken($"{second.SourceAppUserModelId} {second.SourceDisplayName}");
        return firstSource == secondSource;
    }

    private static bool ContainsKnownMediaSite(string windowTitle, string mediaTitle)
    {
        var text = $"{windowTitle} {mediaTitle}";
        return text.Contains("YouTube", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("Netflix", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("Twitch", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("Spotify", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("Vimeo", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("SoundCloud", StringComparison.OrdinalIgnoreCase);
    }

    private static void ApplyMediaStatus(ShelvedWindow item, MediaSessionSnapshot session)
    {
        var kind = session.IsPlaying
            ? ShelfBadgeKind.Playing
            : session.IsPaused
                ? ShelfBadgeKind.Paused
                : ShelfBadgeKind.Changed;
        var progressText = FormatProgressText(session);
        var mediaDetail = FormatMediaDetail(session);
        var sourceTitle = ResolveMediaSourceTitle(item, session);

        item.SetMediaStatus(
            session.SessionId,
            sourceTitle,
            kind,
            progressText,
            mediaDetail,
            session.Progress,
            session.Duration > TimeSpan.FromSeconds(1),
            session.CanTogglePlayPause);
    }

    private static string ResolveMediaSourceTitle(ShelvedWindow item, MediaSessionSnapshot session)
    {
        var title = item.Title;
        if (title.Contains("YouTube", StringComparison.OrdinalIgnoreCase))
        {
            return "YouTube";
        }

        if (title.Contains("Netflix", StringComparison.OrdinalIgnoreCase))
        {
            return "Netflix";
        }

        if (title.Contains("Twitch", StringComparison.OrdinalIgnoreCase))
        {
            return "Twitch";
        }

        if (title.Contains("Spotify", StringComparison.OrdinalIgnoreCase))
        {
            return "Spotify";
        }

        return IsBrowserProcess(item.ProcessName)
            ? session.SourceDisplayName
            : FormatProcessDisplayName(item.ProcessName, session.SourceDisplayName);
    }

    private static string FormatProcessDisplayName(string processName, string fallback)
    {
        if (processName.Contains("spotify", StringComparison.OrdinalIgnoreCase))
        {
            return "Spotify";
        }

        if (processName.Contains("vlc", StringComparison.OrdinalIgnoreCase))
        {
            return "VLC";
        }

        if (processName.Contains("msedge", StringComparison.OrdinalIgnoreCase))
        {
            return "Edge";
        }

        if (processName.Contains("chrome", StringComparison.OrdinalIgnoreCase))
        {
            return "Chrome";
        }

        return string.IsNullOrWhiteSpace(fallback) ? processName : fallback;
    }

    private static string FormatMediaDetail(MediaSessionSnapshot session)
    {
        if (string.IsNullOrWhiteSpace(session.Title))
        {
            return string.Empty;
        }

        return string.IsNullOrWhiteSpace(session.Artist)
            ? session.Title
            : $"{session.Artist} - {session.Title}";
    }

    private static string FormatProgressText(MediaSessionSnapshot session)
    {
        if (session.Duration <= TimeSpan.FromSeconds(1))
        {
            return string.Empty;
        }

        return $"{FormatDuration(session.Position)} / {FormatDuration(session.Duration)}";
    }

    private static string FormatDuration(TimeSpan value)
    {
        if (value < TimeSpan.Zero)
        {
            value = TimeSpan.Zero;
        }

        return value.TotalHours >= 1
            ? $"{(int)value.TotalHours}:{value.Minutes:00}:{value.Seconds:00}"
            : $"{(int)value.TotalMinutes}:{value.Seconds:00}";
    }

    private void ObserveWindowContent(ShelvedWindow item, DateTime now)
    {
        if (item.IsMediaCard)
        {
            return;
        }

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
            now - item.LastObservedChangeUtc < doneDelay)
        {
            return;
        }

        var stableBadge = GetStableContentBadge(item, text);
        if (stableBadge is ShelfBadgeKind.None)
        {
            return;
        }

        item.HasReportedStable = true;
        SetBadgeAndAlert(item, stableBadge);
    }

    private void SetBadgeAndAlert(
        ShelvedWindow item,
        ShelfBadgeKind badgeKind,
        bool force = false,
        string detail = "")
    {
        if (item.BadgeKind == badgeKind && string.IsNullOrWhiteSpace(detail))
        {
            return;
        }

        if (item.BadgeKind != badgeKind && !force && !ShouldReplaceBadge(item.BadgeKind, badgeKind))
        {
            return;
        }

        item.SetBadge(badgeKind, detail);
        AttentionRequested?.Invoke(this, item);
    }

    private static ShelfBadgeKind GetTitleChangeBadge(ShelvedWindow item)
    {
        if (item.IsAgentLikeSession || LooksLikeAgentSession(item.ProcessName, item.Title))
        {
            return GetAgentTextBadge(item.Title);
        }

        return GetSmartTextBadge(item.ProcessName, item.Title);
    }

    private static ShelfBadgeKind GetContentChangeBadge(ShelvedWindow item, string text)
    {
        var recentText = Tail(text, 1800);

        if (item.IsAgentLikeSession || LooksLikeAgentSession(item.ProcessName, item.Title, text))
        {
            return GetAgentTextBadge(recentText);
        }

        return GetSmartTextBadge(item.ProcessName, recentText);
    }

    private static ShelfBadgeKind GetStableContentBadge(ShelvedWindow item, string text)
    {
        var recentText = Tail(text, 1800);
        var isAgent = item.IsAgentLikeSession || LooksLikeAgentSession(item.ProcessName, item.Title, text);

        if (isAgent)
        {
            if (LooksLikeApproval(recentText))
            {
                return ShelfBadgeKind.WaitingForApproval;
            }

            if (LooksFailure(recentText))
            {
                return ShelfBadgeKind.Failed;
            }

            return !LooksBusy(text) && (LooksDone(text) || (item.HasObservedBusySignal && LooksPromptReady(text)))
                ? ShelfBadgeKind.DoneNeedsReview
                : ShelfBadgeKind.None;
        }

        if (LooksError(recentText))
        {
            return ShelfBadgeKind.Error;
        }

        if (LooksUploadComplete(recentText))
        {
            return ShelfBadgeKind.UploadComplete;
        }

        if (LooksResponseFinished(recentText))
        {
            return ShelfBadgeKind.ResponseFinished;
        }

        return LooksDone(recentText) ? ShelfBadgeKind.Done : ShelfBadgeKind.None;
    }

    private static ShelfBadgeKind GetBridgeBadge(ShelfBridgeEvent bridgeEvent)
    {
        var isAgentEvent = IsAgentBridgeEvent(bridgeEvent);
        var explicitStatus = MapStatusToken(bridgeEvent.Status);
        if (explicitStatus is not ShelfBadgeKind.None)
        {
            return isAgentEvent && explicitStatus is ShelfBadgeKind.Done
                ? ShelfBadgeKind.DoneNeedsReview
                : explicitStatus;
        }

        var eventName = NormalizeToken(bridgeEvent.EventName);
        var tool = NormalizeToken(bridgeEvent.Tool);
        var text = $"{bridgeEvent.EventName} {bridgeEvent.Tool} {bridgeEvent.Message} {bridgeEvent.Details}";

        if (isAgentEvent)
        {
            if (eventName.Contains("permissionrequest", StringComparison.Ordinal) || LooksLikeApproval(text))
            {
                return ShelfBadgeKind.WaitingForApproval;
            }

            if (eventName.Contains("stopfailure", StringComparison.Ordinal) ||
                bridgeEvent.Success == false ||
                LooksFailure(text))
            {
                return ShelfBadgeKind.Failed;
            }

            if (eventName == "stop")
            {
                return ShelfBadgeKind.DoneNeedsReview;
            }

            if (eventName.Contains("pretooluse", StringComparison.Ordinal))
            {
                if (IsFileEditingTool(tool, text))
                {
                    return ShelfBadgeKind.EditingFiles;
                }

                if (IsCommandTool(tool, text))
                {
                    return ShelfBadgeKind.RunningCommand;
                }

                return ShelfBadgeKind.Running;
            }

            if (eventName.Contains("userpromptsubmit", StringComparison.Ordinal) ||
                eventName.Contains("posttooluse", StringComparison.Ordinal))
            {
                return ShelfBadgeKind.Running;
            }

            if (eventName.Contains("notification", StringComparison.Ordinal))
            {
                return LooksLikeApproval(text) ? ShelfBadgeKind.WaitingForApproval : ShelfBadgeKind.NeedsAttention;
            }
        }

        var smartEventStatus = MapStatusToken(bridgeEvent.EventName);
        if (smartEventStatus is not ShelfBadgeKind.None)
        {
            return smartEventStatus;
        }

        if (LooksError(text))
        {
            return ShelfBadgeKind.Error;
        }

        if (LooksLikeNeedsInput(text))
        {
            return ShelfBadgeKind.NeedsInput;
        }

        if (LooksUploadComplete(text))
        {
            return ShelfBadgeKind.UploadComplete;
        }

        if (LooksPlaying(text))
        {
            return ShelfBadgeKind.Playing;
        }

        if (LooksPaused(text))
        {
            return ShelfBadgeKind.Paused;
        }

        if (LooksLoading(text))
        {
            return ShelfBadgeKind.Loading;
        }

        return LooksResponseFinished(text)
            ? ShelfBadgeKind.ResponseFinished
            : LooksDone(text)
                ? ShelfBadgeKind.Done
                : ShelfBadgeKind.Changed;
    }

    private static ShelfBadgeKind GetAgentTextBadge(string text)
    {
        if (LooksLikeApproval(text))
        {
            return ShelfBadgeKind.WaitingForApproval;
        }

        if (LooksFailure(text))
        {
            return ShelfBadgeKind.Failed;
        }

        if (LooksAgentEditing(text))
        {
            return ShelfBadgeKind.EditingFiles;
        }

        if (LooksAgentRunningCommand(text))
        {
            return ShelfBadgeKind.RunningCommand;
        }

        if (LooksBusy(text))
        {
            return ShelfBadgeKind.Running;
        }

        return LooksDone(text) ? ShelfBadgeKind.DoneNeedsReview : ShelfBadgeKind.Changed;
    }

    private static ShelfBadgeKind GetSmartTextBadge(string processName, string text)
    {
        if (LooksError(text))
        {
            return ShelfBadgeKind.Error;
        }

        if (LooksLikeNeedsInput(text))
        {
            return ShelfBadgeKind.NeedsInput;
        }

        if (LooksUploadComplete(text))
        {
            return ShelfBadgeKind.UploadComplete;
        }

        if (LooksPlaying(text))
        {
            return ShelfBadgeKind.Playing;
        }

        if (LooksPaused(text))
        {
            return ShelfBadgeKind.Paused;
        }

        if (LooksLoading(text))
        {
            return ShelfBadgeKind.Loading;
        }

        if (LooksResponseFinished(text))
        {
            return ShelfBadgeKind.ResponseFinished;
        }

        if (LooksDone(text))
        {
            return ShelfBadgeKind.Done;
        }

        return IsBrowserProcess(processName)
            ? ShelfBadgeKind.Updated
            : ShelfBadgeKind.Changed;
    }

    private static ShelfBadgeKind MapStatusToken(string status)
    {
        return NormalizeToken(status) switch
        {
            "loading" or "pageloading" => ShelfBadgeKind.Loading,
            "updated" or "pageloaded" or "loaded" or "complete" => ShelfBadgeKind.Updated,
            "changed" => ShelfBadgeKind.Changed,
            "done" or "finished" => ShelfBadgeKind.Done,
            "responsefinished" => ShelfBadgeKind.ResponseFinished,
            "doneneedsreview" or "readyforreview" => ShelfBadgeKind.DoneNeedsReview,
            "needsreview" => ShelfBadgeKind.NeedsReview,
            "running" or "working" => ShelfBadgeKind.Running,
            "editingfiles" or "editing" => ShelfBadgeKind.EditingFiles,
            "runningcommand" or "commandrunning" => ShelfBadgeKind.RunningCommand,
            "waitingforapproval" or "permissionrequest" => ShelfBadgeKind.WaitingForApproval,
            "needsinput" or "inputrequired" => ShelfBadgeKind.NeedsInput,
            "playing" => ShelfBadgeKind.Playing,
            "paused" => ShelfBadgeKind.Paused,
            "uploadcomplete" => ShelfBadgeKind.UploadComplete,
            "failed" or "failure" => ShelfBadgeKind.Failed,
            "error" => ShelfBadgeKind.Error,
            _ => ShelfBadgeKind.None
        };
    }

    private static bool ShouldReplaceBadge(ShelfBadgeKind current, ShelfBadgeKind next)
    {
        if (current == ShelfBadgeKind.Closed)
        {
            return false;
        }

        if (next == ShelfBadgeKind.Closed)
        {
            return true;
        }

        if (IsRunningBadge(next) && IsFinalBadge(current))
        {
            return true;
        }

        return GetBadgePriority(next) >= GetBadgePriority(current);
    }

    private static int GetBadgePriority(ShelfBadgeKind badgeKind)
    {
        return badgeKind switch
        {
            ShelfBadgeKind.Closed => 100,
            ShelfBadgeKind.Failed or ShelfBadgeKind.Error => 90,
            ShelfBadgeKind.WaitingForApproval or ShelfBadgeKind.NeedsInput or ShelfBadgeKind.NeedsAttention => 80,
            ShelfBadgeKind.DoneNeedsReview or ShelfBadgeKind.NeedsReview => 70,
            ShelfBadgeKind.Done or ShelfBadgeKind.ResponseFinished or ShelfBadgeKind.UploadComplete => 65,
            ShelfBadgeKind.Running or ShelfBadgeKind.EditingFiles or ShelfBadgeKind.RunningCommand => 55,
            ShelfBadgeKind.Loading or ShelfBadgeKind.Playing or ShelfBadgeKind.Paused => 45,
            ShelfBadgeKind.Changed or ShelfBadgeKind.Updated => 30,
            _ => 0
        };
    }

    private static bool IsRunningBadge(ShelfBadgeKind badgeKind)
    {
        return badgeKind is ShelfBadgeKind.Running or
            ShelfBadgeKind.EditingFiles or
            ShelfBadgeKind.RunningCommand or
            ShelfBadgeKind.Loading;
    }

    private static bool IsFinalBadge(ShelfBadgeKind badgeKind)
    {
        return badgeKind is ShelfBadgeKind.Done or
            ShelfBadgeKind.DoneNeedsReview or
            ShelfBadgeKind.NeedsReview or
            ShelfBadgeKind.ResponseFinished or
            ShelfBadgeKind.UploadComplete or
            ShelfBadgeKind.Failed or
            ShelfBadgeKind.Error;
    }

    private static string BuildBridgeDetail(ShelfBridgeEvent bridgeEvent)
    {
        if (!string.IsNullOrWhiteSpace(bridgeEvent.Details))
        {
            return bridgeEvent.Details;
        }

        if (bridgeEvent.FilesChanged is { } filesChanged)
        {
            return filesChanged == 1 ? "1 file changed" : $"{filesChanged} files changed";
        }

        return bridgeEvent.Message;
    }

    private static bool IsAgentBridgeEvent(ShelfBridgeEvent bridgeEvent)
    {
        var token = NormalizeToken(
            $"{bridgeEvent.Source} {bridgeEvent.EventName} {bridgeEvent.Tool} {bridgeEvent.Message}");

        return token.Contains("codex", StringComparison.Ordinal) ||
               token.Contains("claude", StringComparison.Ordinal) ||
               token.Contains("cursor", StringComparison.Ordinal) ||
               token.Contains("agent", StringComparison.Ordinal) ||
               token.Contains("pretooluse", StringComparison.Ordinal) ||
               token.Contains("posttooluse", StringComparison.Ordinal) ||
               token.Contains("permissionrequest", StringComparison.Ordinal) ||
               token.Contains("userpromptsubmit", StringComparison.Ordinal) ||
               token.Contains("stopfailure", StringComparison.Ordinal);
    }

    private static bool IsFileEditingTool(string normalizedTool, string text)
    {
        return normalizedTool.Contains("applypatch", StringComparison.Ordinal) ||
               normalizedTool.Contains("edit", StringComparison.Ordinal) ||
               normalizedTool.Contains("write", StringComparison.Ordinal) ||
               LooksAgentEditing(text);
    }

    private static bool IsCommandTool(string normalizedTool, string text)
    {
        return normalizedTool.Contains("shell", StringComparison.Ordinal) ||
               normalizedTool.Contains("command", StringComparison.Ordinal) ||
               normalizedTool.Contains("exec", StringComparison.Ordinal) ||
               LooksAgentRunningCommand(text);
    }

    private static string NormalizeToken(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return new string(value.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
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

    private static bool LooksResponseFinished(string text)
    {
        return text.Contains("response finished", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("finished responding", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("generation complete", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksFailure(string text)
    {
        return text.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("failure", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("error", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("exception", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("build failed", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("tests failed", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("stopfailure", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksError(string text)
    {
        return text.Contains("error", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("failure", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("cannot continue", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("something went wrong", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeNeedsInput(string text)
    {
        return text.Contains("needs input", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("input required", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("sign in", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("password", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("choose an option", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("click next", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("waiting for input", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeApproval(string text)
    {
        return text.Contains("approval", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("permission", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("wants permission", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("allow this", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("confirm", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("proceed?", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksUploadComplete(string text)
    {
        return text.Contains("upload complete", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("uploaded", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("sync complete", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksPlaying(string text)
    {
        return text.Contains("playing", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("watching", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksPaused(string text)
    {
        return text.Contains("paused", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLoading(string text)
    {
        return text.Contains("loading", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("please wait", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("uploading", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("installing", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("downloading", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("processing", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksAgentEditing(string text)
    {
        return text.Contains("editing files", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("applying patch", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("updated file", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("writing file", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksAgentRunningCommand(string text)
    {
        return text.Contains("running command", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("shell command", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("npm ", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("dotnet ", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("cargo ", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("pytest", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("powershell", StringComparison.OrdinalIgnoreCase);
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
