namespace LiveShelf;

internal sealed class MediaTimelineEstimator
{
    private static readonly TimeSpan MinimumTrustedMovement = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan PlayingConfirmationDelay = TimeSpan.FromMilliseconds(850);
    private static readonly TimeSpan TimelineAnchorFutureTolerance = TimeSpan.FromSeconds(2);

    private readonly Dictionary<string, MediaTimelineState> _states = [];

    public void Clear()
    {
        _states.Clear();
    }

    public void Remove(string sessionId)
    {
        _states.Remove(sessionId);
    }

    public TimeSpan Resolve(
        string sessionId,
        TimeSpan reportedElapsed,
        TimeSpan duration,
        bool isPlaying,
        DateTimeOffset timelineUpdatedAt,
        bool canControlPlayback)
    {
        var now = DateTimeOffset.UtcNow;
        if (!_states.TryGetValue(sessionId, out var state))
        {
            var anchorTime = isPlaying
                ? GetUsableAnchorTime(timelineUpdatedAt, now)
                : now;

            _states[sessionId] = new MediaTimelineState(
                reportedElapsed,
                reportedElapsed,
                anchorTime,
                timelineUpdatedAt,
                isPlaying,
                duration,
                canEstimate: isPlaying);

            return isPlaying
                ? ClampElapsed(reportedElapsed + GetPositiveElapsed(now, anchorTime), duration)
                : reportedElapsed;
        }

        var statusChanged = state.WasPlaying != isPlaying;
        var movedForward = reportedElapsed - state.LastReportedElapsed > MinimumTrustedMovement;
        var jumpedBackward = state.LastReportedElapsed - reportedElapsed > MinimumTrustedMovement;
        var timelineAdvanced = timelineUpdatedAt > state.LastTimelineUpdatedAt;
        var durationChanged = duration > TimeSpan.Zero &&
                              state.Duration > TimeSpan.Zero &&
                              (duration - state.Duration).Duration() > TimeSpan.FromSeconds(1);

        if (!isPlaying)
        {
            state.CanEstimate = false;
            state.PendingPlayingSinceUtc = null;
            state.AnchorElapsed = reportedElapsed;
            state.AnchorTimeUtc = now;
        }
        else
        {
            if (statusChanged || jumpedBackward || durationChanged)
            {
                state.CanEstimate = false;
                state.PendingPlayingSinceUtc = now;
                state.AnchorElapsed = reportedElapsed;
                state.AnchorTimeUtc = now;
            }

            if (movedForward)
            {
                state.CanEstimate = true;
                state.PendingPlayingSinceUtc = null;
                state.AnchorElapsed = reportedElapsed;
                state.AnchorTimeUtc = GetUsableAnchorTime(timelineUpdatedAt, now);
            }
            else if (!state.CanEstimate && canControlPlayback)
            {
                state.PendingPlayingSinceUtc ??= now;
                if (now - state.PendingPlayingSinceUtc.Value >= PlayingConfirmationDelay)
                {
                    state.CanEstimate = true;
                    state.PendingPlayingSinceUtc = null;
                    state.AnchorElapsed = reportedElapsed;
                    state.AnchorTimeUtc = now;
                }
            }
        }

        var displayElapsed = reportedElapsed;
        if (isPlaying && state.CanEstimate)
        {
            displayElapsed = state.AnchorElapsed + GetPositiveElapsed(now, state.AnchorTimeUtc);
        }

        state.LastReportedElapsed = reportedElapsed;
        state.WasPlaying = isPlaying;
        state.Duration = duration;
        if (timelineAdvanced)
        {
            state.LastTimelineUpdatedAt = timelineUpdatedAt;
        }

        return ClampElapsed(displayElapsed, duration);
    }

    public static TimeSpan ClampElapsed(TimeSpan elapsed, TimeSpan duration)
    {
        if (elapsed < TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        return duration > TimeSpan.Zero && elapsed > duration
            ? duration
            : elapsed;
    }

    private static TimeSpan GetPositiveElapsed(DateTimeOffset now, DateTimeOffset anchorTime)
    {
        var elapsed = now - anchorTime;
        return elapsed > TimeSpan.Zero ? elapsed : TimeSpan.Zero;
    }

    private static DateTimeOffset GetUsableAnchorTime(DateTimeOffset timelineUpdatedAt, DateTimeOffset now)
    {
        if (timelineUpdatedAt == default ||
            timelineUpdatedAt > now + TimelineAnchorFutureTolerance)
        {
            return now;
        }

        return timelineUpdatedAt;
    }
}

internal sealed class MediaTimelineState(
    TimeSpan lastReportedElapsed,
    TimeSpan anchorElapsed,
    DateTimeOffset anchorTimeUtc,
    DateTimeOffset lastTimelineUpdatedAt,
    bool wasPlaying,
    TimeSpan duration,
    bool canEstimate)
{
    public TimeSpan LastReportedElapsed { get; set; } = lastReportedElapsed;

    public TimeSpan AnchorElapsed { get; set; } = anchorElapsed;

    public DateTimeOffset AnchorTimeUtc { get; set; } = anchorTimeUtc;

    public DateTimeOffset LastTimelineUpdatedAt { get; set; } = lastTimelineUpdatedAt;

    public bool WasPlaying { get; set; } = wasPlaying;

    public TimeSpan Duration { get; set; } = duration;

    public DateTimeOffset? PendingPlayingSinceUtc { get; set; }

    public bool CanEstimate { get; set; } = canEstimate;
}
