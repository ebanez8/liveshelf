using System.ComponentModel;
using System.Windows;
using System.Windows.Media;

namespace LiveShelf;

public sealed class ShelvedWindow : INotifyPropertyChanged
{
    private static readonly Brush LiveBrush = new SolidColorBrush(Color.FromRgb(95, 220, 139));
    private static readonly Brush ClosedBrush = new SolidColorBrush(Color.FromRgb(238, 105, 117));
    private static readonly Brush ChangedBrush = new SolidColorBrush(Color.FromRgb(117, 196, 255));
    private static readonly Brush UpdatedBrush = new SolidColorBrush(Color.FromRgb(93, 232, 222));
    private static readonly Brush DoneBrush = new SolidColorBrush(Color.FromRgb(95, 220, 139));
    private static readonly Brush NeedsAttentionBrush = new SolidColorBrush(Color.FromRgb(255, 196, 87));
    private static readonly Brush EmptyBadgeBrush = Brushes.Transparent;

    private string _title;
    private string _badgeText = string.Empty;
    private Brush _badgeBrush = EmptyBadgeBrush;
    private ShelfBadgeKind _badgeKind = ShelfBadgeKind.None;
    private bool _isExpanded;
    private bool _isZoomed;
    private bool _isSourceAlive = true;
    private bool _isInteractive;

    internal ShelvedWindow(
        IntPtr sourceHwnd,
        IntPtr thumbnailHandle,
        NativeMethods.WINDOWPLACEMENT originalPlacement,
        string title,
        string processName)
    {
        SourceHwnd = sourceHwnd;
        ThumbnailHandle = thumbnailHandle;
        OriginalPlacement = originalPlacement;
        _title = string.IsNullOrWhiteSpace(title) ? processName : title;
        ProcessName = processName;
        LastObservedChangeUtc = DateTime.UtcNow;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    internal IntPtr SourceHwnd { get; }

    internal IntPtr ThumbnailHandle { get; set; }

    internal NativeMethods.WINDOWPLACEMENT OriginalPlacement { get; }

    internal IntPtr LastInputTargetHwnd { get; set; }

    public string ProcessName { get; }

    internal DateTime LastObservedChangeUtc { get; set; }

    internal DateTime LastStateProbeUtc { get; set; }

    internal DateTime SuppressFocusAlertsUntilUtc { get; set; }

    internal bool HasDetectedChange { get; set; }

    internal bool HasReportedStable { get; set; }

    internal bool HasContentSnapshot { get; set; }

    internal bool HasObservedBusySignal { get; set; }

    internal int LastContentHash { get; set; }

    internal bool IsInteractive
    {
        get => _isInteractive;
        set
        {
            if (_isInteractive == value)
            {
                return;
            }

            _isInteractive = value;
            OnPropertyChanged(nameof(IsInteractive));
            OnPropertyChanged(nameof(InteractionText));
        }
    }

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
        }
    }

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
        }
    }

    public double PreviewHeight => IsExpanded ? 188 : 108;

    public Brush StatusBrush => IsSourceAlive ? LiveBrush : ClosedBrush;

    public Visibility ClosedOverlayVisibility => IsSourceAlive ? Visibility.Collapsed : Visibility.Visible;

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
        }
    }

    public Visibility BadgeVisibility => string.IsNullOrWhiteSpace(BadgeText)
        ? Visibility.Collapsed
        : Visibility.Visible;

    public string InteractionText => IsInteractive ? "Exit" : "Use";

    public string ZoomText => IsZoomed ? "Max" : "Zoom";

    internal void SetBadge(ShelfBadgeKind kind)
    {
        BadgeKind = kind;

        (BadgeText, BadgeBrush) = kind switch
        {
            ShelfBadgeKind.Changed => ("Changed", ChangedBrush),
            ShelfBadgeKind.Updated => ("Updated", UpdatedBrush),
            ShelfBadgeKind.Done => ("Done", DoneBrush),
            ShelfBadgeKind.Closed => ("Closed", ClosedBrush),
            ShelfBadgeKind.NeedsAttention => ("Needs attention", NeedsAttentionBrush),
            _ => (string.Empty, EmptyBadgeBrush)
        };
    }

    internal void MarkAttentionSeen()
    {
        if (BadgeKind is ShelfBadgeKind.Changed or ShelfBadgeKind.Updated or ShelfBadgeKind.NeedsAttention)
        {
            SetBadge(ShelfBadgeKind.None);
        }
    }

    private void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public enum ShelfBadgeKind
{
    None,
    Changed,
    Updated,
    Done,
    Closed,
    NeedsAttention
}
