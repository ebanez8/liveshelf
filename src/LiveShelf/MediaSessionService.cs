using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using Windows.Media.Control;

namespace LiveShelf;

internal sealed class MediaSessionService : IDisposable
{
    private static readonly TimeSpan TimelineUpdateInterval = TimeSpan.FromSeconds(1);

    private readonly Dictionary<string, GlobalSystemMediaTransportControlsSession> _sessions = [];
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _timelineTimer;
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private bool _isDisposed;
    private bool _isRefreshing;
    private bool _refreshQueued;

    public MediaSessionService()
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        _timelineTimer = new DispatcherTimer
        {
            Interval = TimelineUpdateInterval
        };
        _timelineTimer.Tick += TimelineTimer_Tick;
    }

    public event EventHandler<IReadOnlyList<MediaSessionSnapshot>>? SessionsChanged;

    public void Start()
    {
        _ = InitializeAsync();
    }

    public void RefreshSoon()
    {
        QueueRefresh();
    }

    public async Task TogglePlayPauseAsync(string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
        {
            return;
        }

        try
        {
            var playbackInfo = session.GetPlaybackInfo();
            var controls = playbackInfo.Controls;

            if (controls.IsPlayPauseToggleEnabled)
            {
                await session.TryTogglePlayPauseAsync();
            }
            else if (playbackInfo.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing &&
                     controls.IsPauseEnabled)
            {
                await session.TryPauseAsync();
            }
            else if (controls.IsPlayEnabled)
            {
                await session.TryPlayAsync();
            }

            QueueRefresh();
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException)
        {
        }
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _timelineTimer.Stop();

        if (_manager is not null)
        {
            _manager.SessionsChanged -= Manager_SessionsChanged;
            _manager.CurrentSessionChanged -= Manager_CurrentSessionChanged;
        }

        foreach (var session in _sessions.Values)
        {
            UnsubscribeSession(session);
        }

        _sessions.Clear();
    }

    private async Task InitializeAsync()
    {
        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _manager.SessionsChanged += Manager_SessionsChanged;
            _manager.CurrentSessionChanged += Manager_CurrentSessionChanged;
            _timelineTimer.Start();
            await RefreshSessionsAsync();
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException)
        {
        }
    }

    private void TimelineTimer_Tick(object? sender, EventArgs e)
    {
        QueueRefresh();
    }

    private void Manager_SessionsChanged(
        GlobalSystemMediaTransportControlsSessionManager sender,
        SessionsChangedEventArgs args)
    {
        QueueRefresh();
    }

    private void Manager_CurrentSessionChanged(
        GlobalSystemMediaTransportControlsSessionManager sender,
        CurrentSessionChangedEventArgs args)
    {
        QueueRefresh();
    }

    private void Session_PlaybackInfoChanged(
        GlobalSystemMediaTransportControlsSession sender,
        PlaybackInfoChangedEventArgs args)
    {
        QueueRefresh();
    }

    private void Session_TimelinePropertiesChanged(
        GlobalSystemMediaTransportControlsSession sender,
        TimelinePropertiesChangedEventArgs args)
    {
        QueueRefresh();
    }

    private void Session_MediaPropertiesChanged(
        GlobalSystemMediaTransportControlsSession sender,
        MediaPropertiesChangedEventArgs args)
    {
        QueueRefresh();
    }

    private void QueueRefresh()
    {
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.InvokeAsync(QueueRefreshOnDispatcher);
            return;
        }

        QueueRefreshOnDispatcher();
    }

    private void QueueRefreshOnDispatcher()
    {
        if (_isDisposed)
        {
            return;
        }

        if (_isRefreshing)
        {
            _refreshQueued = true;
            return;
        }

        _ = RefreshSessionsAsync();
    }

    private async Task RefreshSessionsAsync()
    {
        if (_manager is null || _isDisposed || _isRefreshing)
        {
            return;
        }

        _isRefreshing = true;

        try
        {
            IReadOnlyList<GlobalSystemMediaTransportControlsSession> currentSessions;
            try
            {
                currentSessions = _manager.GetSessions();
            }
            catch (InvalidOperationException)
            {
                return;
            }

            var currentSession = _manager.GetCurrentSession();
            var activeIds = new HashSet<string>(StringComparer.Ordinal);
            var snapshots = new List<MediaSessionSnapshot>();

            foreach (var session in currentSessions)
            {
                var sessionId = GetSessionId(session);
                activeIds.Add(sessionId);

                if (!_sessions.ContainsKey(sessionId))
                {
                    _sessions[sessionId] = session;
                    SubscribeSession(session);
                }

                var snapshot = await TryCreateSnapshotAsync(
                    session,
                    sessionId,
                    ReferenceEquals(session, currentSession));

                if (snapshot is not null)
                {
                    snapshots.Add(snapshot);
                }
            }

            foreach (var stale in _sessions.Where(pair => !activeIds.Contains(pair.Key)).ToArray())
            {
                UnsubscribeSession(stale.Value);
                _sessions.Remove(stale.Key);
            }

            SessionsChanged?.Invoke(this, snapshots);
        }
        finally
        {
            _isRefreshing = false;
            if (_refreshQueued)
            {
                _refreshQueued = false;
                QueueRefresh();
            }
        }
    }

    private async Task<MediaSessionSnapshot?> TryCreateSnapshotAsync(
        GlobalSystemMediaTransportControlsSession session,
        string sessionId,
        bool isCurrentSession)
    {
        try
        {
            var playbackInfo = session.GetPlaybackInfo();
            var timeline = session.GetTimelineProperties();
            var properties = await session.TryGetMediaPropertiesAsync();
            var controls = playbackInfo.Controls;

            var playbackStatus = playbackInfo.PlaybackStatus;
            var title = NormalizeWhitespace(properties.Title);
            var artist = NormalizeWhitespace(properties.Artist);
            var album = NormalizeWhitespace(properties.AlbumTitle);
            var sourceAppUserModelId = session.SourceAppUserModelId ?? string.Empty;
            var sourceDisplayName = FormatSourceAppName(sourceAppUserModelId);
            var start = timeline.StartTime;
            var end = timeline.EndTime;
            var position = EstimatePosition(timeline, playbackStatus);
            var duration = end > start ? end - start : TimeSpan.Zero;
            var elapsed = duration > TimeSpan.Zero ? position - start : position;

            if (elapsed < TimeSpan.Zero)
            {
                elapsed = TimeSpan.Zero;
            }

            if (duration > TimeSpan.Zero && elapsed > duration)
            {
                elapsed = duration;
            }

            var progress = duration > TimeSpan.FromSeconds(1)
                ? Math.Clamp(elapsed.TotalMilliseconds / duration.TotalMilliseconds, 0, 1)
                : 0;

            return new MediaSessionSnapshot(
                sessionId,
                sourceAppUserModelId,
                sourceDisplayName,
                title,
                artist,
                album,
                playbackStatus,
                playbackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                playbackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused,
                playbackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Stopped,
                elapsed,
                duration,
                progress,
                timeline.LastUpdatedTime,
                isCurrentSession,
                controls.IsPlayPauseToggleEnabled || controls.IsPlayEnabled || controls.IsPauseEnabled,
                controls.IsPreviousEnabled,
                controls.IsNextEnabled);
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static TimeSpan EstimatePosition(
        GlobalSystemMediaTransportControlsSessionTimelineProperties timeline,
        GlobalSystemMediaTransportControlsSessionPlaybackStatus playbackStatus)
    {
        var position = timeline.Position;
        if (playbackStatus != GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
        {
            return position;
        }

        var elapsedSinceUpdate = DateTimeOffset.UtcNow - timeline.LastUpdatedTime;
        if (elapsedSinceUpdate <= TimeSpan.Zero || elapsedSinceUpdate > TimeSpan.FromMinutes(10))
        {
            return position;
        }

        return position + elapsedSinceUpdate;
    }

    private static string GetSessionId(GlobalSystemMediaTransportControlsSession session)
    {
        return RuntimeHelpers.GetHashCode(session).ToString(CultureInfo.InvariantCulture);
    }

    private static string FormatSourceAppName(string sourceAppUserModelId)
    {
        if (string.IsNullOrWhiteSpace(sourceAppUserModelId))
        {
            return "Media";
        }

        var source = sourceAppUserModelId;
        var bangIndex = source.LastIndexOf('!');
        if (bangIndex >= 0 && bangIndex + 1 < source.Length)
        {
            source = source[(bangIndex + 1)..];
        }

        var dotIndex = source.LastIndexOf('.');
        if (dotIndex >= 0 && dotIndex + 1 < source.Length)
        {
            source = source[(dotIndex + 1)..];
        }

        source = source
            .Replace("Microsoft", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("MSEdge", "Edge", StringComparison.OrdinalIgnoreCase)
            .Replace("Chrome", "Chrome", StringComparison.OrdinalIgnoreCase)
            .Trim('-', '_', '.', ' ');

        return string.IsNullOrWhiteSpace(source) ? "Media" : source;
    }

    private static string NormalizeWhitespace(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return string.Join(' ', value.Split(default(string[]), StringSplitOptions.RemoveEmptyEntries));
    }

    private void SubscribeSession(GlobalSystemMediaTransportControlsSession session)
    {
        session.PlaybackInfoChanged += Session_PlaybackInfoChanged;
        session.TimelinePropertiesChanged += Session_TimelinePropertiesChanged;
        session.MediaPropertiesChanged += Session_MediaPropertiesChanged;
    }

    private void UnsubscribeSession(GlobalSystemMediaTransportControlsSession session)
    {
        session.PlaybackInfoChanged -= Session_PlaybackInfoChanged;
        session.TimelinePropertiesChanged -= Session_TimelinePropertiesChanged;
        session.MediaPropertiesChanged -= Session_MediaPropertiesChanged;
    }
}

internal sealed record MediaSessionSnapshot(
    string SessionId,
    string SourceAppUserModelId,
    string SourceDisplayName,
    string Title,
    string Artist,
    string Album,
    GlobalSystemMediaTransportControlsSessionPlaybackStatus PlaybackStatus,
    bool IsPlaying,
    bool IsPaused,
    bool IsStopped,
    TimeSpan Position,
    TimeSpan Duration,
    double Progress,
    DateTimeOffset LastUpdatedTime,
    bool IsCurrentSession,
    bool CanTogglePlayPause,
    bool CanPrevious,
    bool CanNext);
