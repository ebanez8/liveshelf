using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;

namespace LiveShelf;

public sealed class ShelvedWindow : INotifyPropertyChanged
{
    private static readonly Brush LiveBrush = new SolidColorBrush(Color.FromRgb(95, 220, 139));
    private static readonly Brush ClosedBrush = new SolidColorBrush(Color.FromRgb(238, 105, 117));
    private static readonly Brush ChangedBrush = new SolidColorBrush(Color.FromRgb(117, 196, 255));
    private static readonly Brush UpdatedBrush = new SolidColorBrush(Color.FromRgb(93, 232, 222));
    private static readonly Brush RunningBrush = new SolidColorBrush(Color.FromRgb(117, 196, 255));
    private static readonly Brush DoneBrush = new SolidColorBrush(Color.FromRgb(95, 220, 139));
    private static readonly Brush NeedsReviewBrush = new SolidColorBrush(Color.FromRgb(142, 232, 145));
    private static readonly Brush NeedsAttentionBrush = new SolidColorBrush(Color.FromRgb(255, 196, 87));
    private static readonly Brush ErrorBrush = new SolidColorBrush(Color.FromRgb(238, 105, 117));
    private static readonly Brush EmptyBadgeBrush = Brushes.Transparent;

    private string _title;
    private string _badgeText = string.Empty;
    private string _statusText = string.Empty;
    private string _detailText = string.Empty;
    private string _mediaSourceTitle = string.Empty;
    private string _mediaSessionId = string.Empty;
    private string _mediaProgressBarText = string.Empty;
    private string _acknowledgedAgentSignalKey = string.Empty;
    private string _currentAgentSignalKey = string.Empty;
    private string _linkedAgentKey = string.Empty;
    private string _liveShelfAgentToken = string.Empty;
    private string _terminalSessionId = string.Empty;
    private string _launchCwd = string.Empty;
    private string _possibleCwd = string.Empty;
    private string _suspectedAgent = string.Empty;
    private string _agentDisplayTitle = string.Empty;
    private string _exePath = string.Empty;
    private string _originalMonitorKey = string.Empty;
    private string _currentShelfMonitorKey = string.Empty;
    private ImageSource? _appIcon;
    private string _mediaPlayPauseText = "Play";
    private string _previewStatusReason = string.Empty;
    private double _mediaProgressPercent;
    private Brush _badgeBrush = EmptyBadgeBrush;
    private ShelfBadgeKind _badgeKind = ShelfBadgeKind.None;
    private bool _isExpanded;
    private bool _isZoomed;
    private bool _isSourceAlive = true;
    private bool _isMediaCard;
    private bool _hasMediaProgress;
    private bool _canToggleMediaPlayback;
    private bool _isPreviewStatusOnly;
    private bool _isThumbnailVisible = true;

    internal ShelvedWindow(
        IntPtr sourceHwnd,
        IntPtr thumbnailHandle,
        NativeMethods.WINDOWPLACEMENT originalPlacement,
        string title,
        string processName,
        int sourceProcessId,
        NativeMethods.RECT originalSourceRect,
        SourceWindowPolicy sourceWindowPolicy)
    {
        SourceHwnd = sourceHwnd;
        ThumbnailHandle = thumbnailHandle;
        OriginalPlacement = originalPlacement;
        OriginalSourceRect = originalSourceRect;
        SourceWindowPolicy = sourceWindowPolicy;
        _title = string.IsNullOrWhiteSpace(title) ? processName : title;
        ProcessName = processName;
        SourceProcessId = sourceProcessId;
        LastObservedChangeUtc = DateTime.UtcNow;
        ShelvedAtUtc = LastObservedChangeUtc;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    internal IntPtr SourceHwnd { get; }

    internal IntPtr ThumbnailHandle { get; set; }

    internal IntPtr ThumbnailShelfHwnd { get; set; }

    internal IntPtr MonitorHandle { get; set; }

    internal NativeMethods.RECT MonitorBounds { get; set; }

    internal NativeMethods.RECT LastCardBounds { get; set; }

    internal NativeMethods.RECT LastThumbnailDestination { get; set; }

    internal int LastDwmRegisterResult { get; set; }

    internal int LastDwmQueryResult { get; set; }

    internal int LastDwmUpdateResult { get; set; }

    internal bool IsShelvingTransactionPending { get; set; }

    internal string OriginalMonitorKey
    {
        get => _originalMonitorKey;
        set => _originalMonitorKey = value;
    }

    internal string CurrentShelfMonitorKey
    {
        get => _currentShelfMonitorKey;
        set => _currentShelfMonitorKey = value;
    }

    internal NativeMethods.WINDOWPLACEMENT OriginalPlacement { get; }

    internal NativeMethods.RECT OriginalSourceRect { get; }

    internal SourceWindowPolicy SourceWindowPolicy { get; private set; }

    internal NativeMethods.RECT ParkedBounds { get; set; }

    internal int LivePreviewFailureCount { get; set; }

    internal int LivePreviewRecoveryAttempts { get; set; }

    internal NativeMethods.SIZE LastThumbnailSourceSize { get; set; }

    internal DateTime LastPreviewRecoveryUtc { get; set; }

    internal bool IsThumbnailVisible
    {
        get => _isThumbnailVisible;
        set => _isThumbnailVisible = value;
    }

    internal string MediaSessionId => _mediaSessionId;

    public string ProcessName { get; }

    public int SourceProcessId { get; }

    public string Id { get; } = Guid.NewGuid().ToString("N");

    internal DateTime ShelvedAtUtc { get; }

    internal DateTime LastObservedChangeUtc { get; set; }

    internal DateTime LastStateProbeUtc { get; set; }

    internal int ConsecutiveUnchangedContentProbeCount { get; set; }

    internal bool HasDetectedChange { get; set; }

    internal bool HasReportedStable { get; set; }

    internal bool HasContentSnapshot { get; set; }

    internal bool HasObservedBusySignal { get; set; }

    internal bool IsAgentLikeSession { get; set; }

    internal string LinkedAgentKey
    {
        get => _linkedAgentKey;
        set
        {
            if (_linkedAgentKey == value)
            {
                return;
            }

            _linkedAgentKey = value;
            OnPropertyChanged(nameof(HasLinkedAgentSession));
            OnPropertyChanged(nameof(CardTitle));
        }
    }

    public bool HasLinkedAgentSession => !string.IsNullOrWhiteSpace(LinkedAgentKey);

    internal string LiveShelfAgentToken
    {
        get => _liveShelfAgentToken;
        set
        {
            if (_liveShelfAgentToken == value)
            {
                return;
            }

            _liveShelfAgentToken = value;
            OnPropertyChanged(nameof(LiveShelfAgentToken));
        }
    }

    internal string TerminalSessionId
    {
        get => _terminalSessionId;
        set => _terminalSessionId = value;
    }

    internal string LaunchCwd
    {
        get => _launchCwd;
        set => _launchCwd = value;
    }

    internal string PossibleCwd
    {
        get => _possibleCwd;
        set => _possibleCwd = value;
    }

    internal string SuspectedAgent
    {
        get => _suspectedAgent;
        set => _suspectedAgent = value;
    }

    internal string AgentDisplayTitle
    {
        get => _agentDisplayTitle;
        set
        {
            if (_agentDisplayTitle == value)
            {
                return;
            }

            _agentDisplayTitle = value;
            OnPropertyChanged(nameof(CardTitle));
        }
    }

    internal string ExePath
    {
        get => _exePath;
        set
        {
            _exePath = value;
            _appIcon = null;
            OnPropertyChanged(nameof(AppIcon));
        }
    }

    public ImageSource? AppIcon
    {
        get
        {
            _appIcon ??= AppIconExtractor.Extract(_exePath);
            return _appIcon;
        }
    }

    public Brush BadgeDotBrush => BadgeKind switch
    {
        ShelfBadgeKind.None => IsSourceAlive ? EmptyBadgeBrush : ClosedBrush,
        _ => BadgeBrush
    };

    public Visibility BadgeDotVisibility => BadgeKind is not ShelfBadgeKind.None || !IsSourceAlive
        ? Visibility.Visible
        : Visibility.Collapsed;

    internal int LastContentHash { get; set; }

    public ShelfBadgeKind BadgeKind
    {
        get => _badgeKind;
        private set
        {
            if (_badgeKind == value)
            {
                return;
            }

            _badgeKind = value;
            OnPropertyChanged(nameof(BadgeKind));
            OnPropertyChanged(nameof(BadgeDotBrush));
            OnPropertyChanged(nameof(BadgeDotVisibility));
        }
    }

    public string Title
    {
        get => _title;
        set
        {
            if (_title == value)
            {
                return;
            }

            _title = value;
            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(CardTitle));
        }
    }

    public string CardTitle => IsMediaCard && !string.IsNullOrWhiteSpace(MediaSourceTitle)
        ? MediaSourceTitle
        : HasLinkedAgentSession && !string.IsNullOrWhiteSpace(AgentDisplayTitle)
            ? AgentDisplayTitle
            : Title;

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value)
            {
                return;
            }

            _isExpanded = value;
            OnPropertyChanged(nameof(IsExpanded));
            OnPropertyChanged(nameof(PreviewHeight));
            OnPropertyChanged(nameof(ZoomText));
        }
    }

    internal bool IsZoomed
    {
        get => _isZoomed;
        set
        {
            if (_isZoomed == value)
            {
                return;
            }

            _isZoomed = value;
            OnPropertyChanged(nameof(IsZoomed));
            OnPropertyChanged(nameof(ZoomText));
        }
    }

    public bool IsSourceAlive
    {
        get => _isSourceAlive;
        set
        {
            if (_isSourceAlive == value)
            {
                return;
            }

            _isSourceAlive = value;
            OnPropertyChanged(nameof(IsSourceAlive));
            OnPropertyChanged(nameof(StatusBrush));
            OnPropertyChanged(nameof(ClosedOverlayVisibility));
            OnPropertyChanged(nameof(BadgeDotBrush));
            OnPropertyChanged(nameof(BadgeDotVisibility));
        }
    }

    public double PreviewHeight => IsExpanded ? 188 : 92;

    public Brush StatusBrush => IsSourceAlive ? LiveBrush : ClosedBrush;

    public Visibility ClosedOverlayVisibility => IsSourceAlive ? Visibility.Collapsed : Visibility.Visible;

    public Visibility HeaderVisibility => IsMediaCard ? Visibility.Collapsed : Visibility.Visible;

    public Visibility PreviewVisibility => IsMediaCard ? Visibility.Collapsed : Visibility.Visible;

    public string BadgeText
    {
        get => _badgeText;
        private set
        {
            if (_badgeText == value)
            {
                return;
            }

            _badgeText = value;
            OnPropertyChanged(nameof(BadgeText));
            OnPropertyChanged(nameof(BadgeVisibility));
        }
    }

    public Brush BadgeBrush
    {
        get => _badgeBrush;
        private set
        {
            if (_badgeBrush == value)
            {
                return;
            }

            _badgeBrush = value;
            OnPropertyChanged(nameof(BadgeBrush));
            OnPropertyChanged(nameof(BadgeDotBrush));
            OnPropertyChanged(nameof(BadgeDotVisibility));
        }
    }

    public Visibility BadgeVisibility => IsMediaCard || string.IsNullOrWhiteSpace(BadgeText)
        ? Visibility.Collapsed
        : Visibility.Visible;

    public string StatusText
    {
        get => _statusText;
        private set
        {
            if (_statusText == value)
            {
                return;
            }

            _statusText = value;
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(StatusTextVisibility));
            OnPropertyChanged(nameof(HeaderStatusTextVisibility));
        }
    }

    public Visibility StatusTextVisibility => string.IsNullOrWhiteSpace(StatusText)
        ? Visibility.Collapsed
        : Visibility.Visible;

    public Visibility HeaderStatusTextVisibility => !IsMediaCard && !string.IsNullOrWhiteSpace(StatusText)
        ? Visibility.Visible
        : Visibility.Collapsed;

    public string DetailText
    {
        get => _detailText;
        private set
        {
            if (_detailText == value)
            {
                return;
            }

            _detailText = value;
            OnPropertyChanged(nameof(DetailText));
            OnPropertyChanged(nameof(DetailTextVisibility));
            OnPropertyChanged(nameof(HeaderDetailTextVisibility));
        }
    }

    public Visibility DetailTextVisibility => string.IsNullOrWhiteSpace(DetailText)
        ? Visibility.Collapsed
        : Visibility.Visible;

    public Visibility HeaderDetailTextVisibility => !IsMediaCard && !string.IsNullOrWhiteSpace(DetailText)
        ? Visibility.Visible
        : Visibility.Collapsed;

    public bool IsMediaCard
    {
        get => _isMediaCard;
        private set
        {
            if (_isMediaCard == value)
            {
                return;
            }

            _isMediaCard = value;
            OnPropertyChanged(nameof(IsMediaCard));
            OnPropertyChanged(nameof(CardTitle));
            OnPropertyChanged(nameof(BadgeVisibility));
            OnPropertyChanged(nameof(HeaderVisibility));
            OnPropertyChanged(nameof(PreviewVisibility));
            OnPropertyChanged(nameof(HeaderStatusTextVisibility));
            OnPropertyChanged(nameof(HeaderDetailTextVisibility));
            OnPropertyChanged(nameof(MediaInfoVisibility));
            OnPropertyChanged(nameof(MediaProgressVisibility));
            OnPropertyChanged(nameof(MediaControlsVisibility));
        }
    }

    public string MediaSourceTitle
    {
        get => _mediaSourceTitle;
        private set
        {
            if (_mediaSourceTitle == value)
            {
                return;
            }

            _mediaSourceTitle = value;
            OnPropertyChanged(nameof(MediaSourceTitle));
            OnPropertyChanged(nameof(CardTitle));
        }
    }

    public double MediaProgressPercent
    {
        get => _mediaProgressPercent;
        private set
        {
            if (Math.Abs(_mediaProgressPercent - value) < 0.1)
            {
                return;
            }

            _mediaProgressPercent = value;
            OnPropertyChanged(nameof(MediaProgressPercent));
        }
    }

    public bool HasMediaProgress
    {
        get => _hasMediaProgress;
        private set
        {
            if (_hasMediaProgress == value)
            {
                return;
            }

            _hasMediaProgress = value;
            OnPropertyChanged(nameof(HasMediaProgress));
            OnPropertyChanged(nameof(HeaderMediaProgressVisibility));
            OnPropertyChanged(nameof(MediaProgressVisibility));
        }
    }

    public Visibility HeaderMediaProgressVisibility => !IsMediaCard && HasMediaProgress
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility MediaProgressVisibility => IsMediaCard && HasMediaProgress
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility MediaInfoVisibility => IsMediaCard
        ? Visibility.Visible
        : Visibility.Collapsed;

    public string MediaProgressBarText
    {
        get => _mediaProgressBarText;
        private set
        {
            if (_mediaProgressBarText == value)
            {
                return;
            }

            _mediaProgressBarText = value;
            OnPropertyChanged(nameof(MediaProgressBarText));
        }
    }

    public bool CanToggleMediaPlayback
    {
        get => _canToggleMediaPlayback;
        private set
        {
            if (_canToggleMediaPlayback == value)
            {
                return;
            }

            _canToggleMediaPlayback = value;
            OnPropertyChanged(nameof(CanToggleMediaPlayback));
            OnPropertyChanged(nameof(MediaControlsVisibility));
        }
    }

    public Visibility MediaControlsVisibility => Visibility.Collapsed;

    public string MediaPlayPauseText
    {
        get => _mediaPlayPauseText;
        private set
        {
            if (_mediaPlayPauseText == value)
            {
                return;
            }

            _mediaPlayPauseText = value;
            OnPropertyChanged(nameof(MediaPlayPauseText));
        }
    }

    public string ZoomText => IsZoomed ? "Max" : "Zoom";

    public bool IsPreviewStatusOnly
    {
        get => _isPreviewStatusOnly;
        private set
        {
            if (_isPreviewStatusOnly == value)
            {
                return;
            }

            _isPreviewStatusOnly = value;
            OnPropertyChanged(nameof(IsPreviewStatusOnly));
            OnPropertyChanged(nameof(StatusOnlyPreviewVisibility));
        }
    }

    public Visibility StatusOnlyPreviewVisibility => IsPreviewStatusOnly
        ? Visibility.Visible
        : Visibility.Collapsed;

    public string PreviewStatusReason
    {
        get => _previewStatusReason;
        private set
        {
            if (_previewStatusReason == value)
            {
                return;
            }

            _previewStatusReason = value;
            OnPropertyChanged(nameof(PreviewStatusReason));
        }
    }

    internal void SetSourceWindowPolicy(SourceWindowPolicy policy)
    {
        SourceWindowPolicy = policy;
    }

    internal void DowngradeToStatusOnlyPreview(string reason)
    {
        PreviewStatusReason = string.IsNullOrWhiteSpace(reason)
            ? "Live preview unavailable"
            : reason;
        IsPreviewStatusOnly = true;
    }

    internal void ClearStatusOnlyPreview()
    {
        PreviewStatusReason = string.Empty;
        IsPreviewStatusOnly = false;
        LivePreviewFailureCount = 0;
        LivePreviewRecoveryAttempts = 0;
    }

    internal void SetBadge(ShelfBadgeKind kind, string detail = "")
    {
        if (IsAgentLikeSession)
        {
            _currentAgentSignalKey = BuildAgentSignalKey(kind, LastContentHash, detail);
        }

        BadgeKind = kind;

        (BadgeText, BadgeBrush) = kind switch
        {
            ShelfBadgeKind.Changed => ("Changed", ChangedBrush),
            ShelfBadgeKind.Updated => ("Updated", UpdatedBrush),
            ShelfBadgeKind.Loading => ("Loading", RunningBrush),
            ShelfBadgeKind.Running => ("Running", RunningBrush),
            ShelfBadgeKind.EditingFiles => ("Editing files", RunningBrush),
            ShelfBadgeKind.RunningCommand => ("Running command", RunningBrush),
            ShelfBadgeKind.WaitingForApproval => ("Waiting for approval", NeedsAttentionBrush),
            ShelfBadgeKind.Done => ("Done", DoneBrush),
            ShelfBadgeKind.DoneNeedsReview => ("Done • Needs review", NeedsReviewBrush),
            ShelfBadgeKind.NeedsReview => ("Needs review", NeedsReviewBrush),
            ShelfBadgeKind.ResponseFinished => ("Response finished", DoneBrush),
            ShelfBadgeKind.Closed => ("Closed", ClosedBrush),
            ShelfBadgeKind.NeedsAttention => ("Needs attention", NeedsAttentionBrush),
            ShelfBadgeKind.NeedsInput => ("Needs input", NeedsAttentionBrush),
            ShelfBadgeKind.Playing => ("Playing", DoneBrush),
            ShelfBadgeKind.Paused => ("Paused", UpdatedBrush),
            ShelfBadgeKind.UploadComplete => ("Upload complete", DoneBrush),
            ShelfBadgeKind.Failed => ("Failed", ErrorBrush),
            ShelfBadgeKind.Error => ("Error", ErrorBrush),
            _ => (string.Empty, EmptyBadgeBrush)
        };

        (StatusText, DetailText) = BuildStatusLines(kind, BadgeText, detail);
    }

    internal void SetHookedAgentStatus(ShelfBadgeKind kind, string label, string detail = "")
    {
        IsAgentLikeSession = true;
        _currentAgentSignalKey = BuildAgentSignalKey(kind, LastContentHash, detail);
        BadgeKind = kind;

        BadgeText = label;
        BadgeBrush = kind switch
        {
            ShelfBadgeKind.WaitingForApproval => NeedsAttentionBrush,
            ShelfBadgeKind.Done or ShelfBadgeKind.DoneNeedsReview => DoneBrush,
            ShelfBadgeKind.Failed => ErrorBrush,
            ShelfBadgeKind.EditingFiles or ShelfBadgeKind.RunningCommand or ShelfBadgeKind.Running => RunningBrush,
            _ => RunningBrush
        };

        (StatusText, DetailText) = BuildStatusLines(kind, label, detail);
    }

    internal void MarkAttentionSeen()
    {
        if (IsAgentLikeSession && IsAcknowledgableAgentSignal(BadgeKind))
        {
            _acknowledgedAgentSignalKey = _currentAgentSignalKey;
            SetBadge(ShelfBadgeKind.None);
            return;
        }

        if (BadgeKind is ShelfBadgeKind.Changed or ShelfBadgeKind.Updated or ShelfBadgeKind.NeedsAttention)
        {
            SetBadge(ShelfBadgeKind.None);
        }
    }

    internal bool IsAcknowledgedAgentSignal(ShelfBadgeKind kind, string detail)
    {
        return IsAgentLikeSession &&
               IsAcknowledgableAgentSignal(kind) &&
               !string.IsNullOrWhiteSpace(_acknowledgedAgentSignalKey) &&
               string.Equals(
                   _acknowledgedAgentSignalKey,
                   BuildAgentSignalKey(kind, LastContentHash, detail),
                   StringComparison.Ordinal);
    }

    internal void ClearAgentSignalAcknowledgement()
    {
        _acknowledgedAgentSignalKey = string.Empty;
    }

    private void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private static (string Status, string Detail) BuildStatusLines(
        ShelfBadgeKind kind,
        string badgeText,
        string detail)
    {
        if (string.IsNullOrWhiteSpace(badgeText))
        {
            return (string.Empty, string.Empty);
        }

        detail = detail.Trim();
        if (detail.Length > 120)
        {
            detail = detail[..117] + "...";
        }

        var status = kind switch
        {
            ShelfBadgeKind.EditingFiles => "Editing files...",
            ShelfBadgeKind.RunningCommand => "Running command...",
            ShelfBadgeKind.Running => "Running...",
            ShelfBadgeKind.Loading => "Loading...",
            _ => badgeText
        };

        if (detail.Length == 0)
        {
            return (status, string.Empty);
        }

        return kind is ShelfBadgeKind.Playing or ShelfBadgeKind.Paused
            ? ($"{status} • {detail}", string.Empty)
            : (status, detail);
    }

    private static bool IsAcknowledgableAgentSignal(ShelfBadgeKind kind)
    {
        return kind is ShelfBadgeKind.Done or
            ShelfBadgeKind.DoneNeedsReview or
            ShelfBadgeKind.NeedsReview or
            ShelfBadgeKind.Failed or
            ShelfBadgeKind.Error;
    }

    private static string BuildAgentSignalKey(ShelfBadgeKind kind, int contentHash, string detail)
    {
        return $"{kind}:{contentHash}";
    }

    internal void SetMediaStatus(
        string sessionId,
        string sourceTitle,
        ShelfBadgeKind kind,
        string statusDetail,
        string mediaDetail,
        double progress,
        bool hasProgress,
        bool canTogglePlayback)
    {
        _mediaSessionId = sessionId;
        IsMediaCard = true;
        MediaSourceTitle = sourceTitle;
        MediaProgressPercent = Math.Clamp(progress * 100, 0, 100);
        HasMediaProgress = hasProgress;
        MediaProgressBarText = BuildMediaProgressBar(progress, hasProgress);
        CanToggleMediaPlayback = canTogglePlayback;
        MediaPlayPauseText = kind == ShelfBadgeKind.Playing ? "Pause" : "Play";

        BadgeKind = kind;
        (BadgeText, BadgeBrush) = kind switch
        {
            ShelfBadgeKind.Playing => ("Playing", DoneBrush),
            ShelfBadgeKind.Paused => ("Paused", UpdatedBrush),
            ShelfBadgeKind.Done => ("Done", DoneBrush),
            _ => ("Media", UpdatedBrush)
        };

        StatusText = string.IsNullOrWhiteSpace(statusDetail)
            ? BadgeText
            : $"{BadgeText} • {statusDetail}";
        DetailText = mediaDetail;
    }

    internal void ClearMediaStatus()
    {
        if (!IsMediaCard)
        {
            return;
        }

        _mediaSessionId = string.Empty;
        IsMediaCard = false;
        MediaSourceTitle = string.Empty;
        HasMediaProgress = false;
        MediaProgressPercent = 0;
        MediaProgressBarText = string.Empty;
        CanToggleMediaPlayback = false;

        if (BadgeKind is ShelfBadgeKind.Playing or ShelfBadgeKind.Paused)
        {
            SetBadge(ShelfBadgeKind.None);
        }
    }

    private static string BuildMediaProgressBar(double progress, bool hasProgress)
    {
        if (!hasProgress)
        {
            return string.Empty;
        }

        const int segments = 10;
        var completed = Math.Clamp((int)Math.Round(progress * segments), 0, segments);
        return new string('━', completed) + new string('─', segments - completed);
    }
}

public enum ShelfBadgeKind
{
    None,
    Changed,
    Updated,
    Loading,
    Running,
    EditingFiles,
    RunningCommand,
    WaitingForApproval,
    Done,
    DoneNeedsReview,
    NeedsReview,
    ResponseFinished,
    Closed,
    NeedsAttention,
    NeedsInput,
    Playing,
    Paused,
    UploadComplete,
    Failed,
    Error
}
