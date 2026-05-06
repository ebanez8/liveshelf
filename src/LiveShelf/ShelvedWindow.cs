using System.ComponentModel;
using System.Windows;
using System.Windows.Media;

namespace LiveShelf;

public sealed class ShelvedWindow : INotifyPropertyChanged
{
    private static readonly Brush LiveBrush = new SolidColorBrush(Color.FromRgb(95, 220, 139));
    private static readonly Brush ClosedBrush = new SolidColorBrush(Color.FromRgb(238, 105, 117));

    private string _title;
    private bool _isExpanded;
    private bool _isSourceAlive = true;

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
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    internal IntPtr SourceHwnd { get; }

    internal IntPtr ThumbnailHandle { get; set; }

    internal NativeMethods.WINDOWPLACEMENT OriginalPlacement { get; }

    public string ProcessName { get; }

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

    private void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
