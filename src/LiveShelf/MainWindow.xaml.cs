using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Media;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Win32;

namespace LiveShelf;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private const int ShelfHotkeyId = 0x5153;
    private const int ToggleShelfHotkeyId = 0x4848;
    private const int HotkeyModifiers = NativeMethods.MOD_ALT | NativeMethods.MOD_CONTROL | NativeMethods.MOD_NOREPEAT;
    private const int ShelfHotkeyVirtualKey = 0x53; // S
    private const int ToggleShelfHotkeyVirtualKey = 0x48; // H

    private const double ShelfWidth = 264;
    private const double PeekShelfWidth = 420;
    private const double HiddenOffset = 10;
    private const double CollapsedPreviewHeight = 108;
    private const double PeekPreviewHeight = 236;
    private const int FastAnimationMs = 150;
    private const int ShelfAnimationMs = 230;
    private const int PeekAnimationMs = 210;

    private readonly ObservableCollection<ShelvedWindow> _items = [];
    private readonly Dictionary<ShelvedWindow, FrameworkElement> _cardElements = [];
    private readonly Dictionary<ShelvedWindow, FrameworkElement> _previewElements = [];
    private readonly DispatcherTimer _peekCollapseTimer;
    private HwndSource? _source;
    private IntPtr _windowHandle;
    private WindowShelver? _shelver;
    private ShelvedWindow? _peekedItem;
    private bool _thumbnailRefreshQueued;
    private bool _isThumbnailAnimationRefreshAttached;
    private bool _isShelfHidden;
    private DateTime _thumbnailAnimationRefreshUntilUtc;
    private int _shelfAnimationGeneration;
    private string _statusMessage = "Ready";

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
        Items.CollectionChanged += Items_CollectionChanged;

        _peekCollapseTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(140)
        };
        _peekCollapseTimer.Tick += PeekCollapseTimer_Tick;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<ShelvedWindow> Items => _items;

    public int ItemCount => Items.Count;

    public string StatusMessage
    {
        get => _statusMessage;
        private set
        {
            if (_statusMessage == value)
            {
                return;
            }

            _statusMessage = value;
            OnPropertyChanged(nameof(StatusMessage));
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        _windowHandle = new WindowInteropHelper(this).Handle;
        NativeMethods.MarkAsToolWindow(_windowHandle);

        _source = HwndSource.FromHwnd(_windowHandle);
        _source?.AddHook(WndProc);

        _shelver = new WindowShelver(_windowHandle, _items);
        _shelver.StatusChanged += (_, message) => StatusMessage = message;
        _shelver.ThumbnailRefreshRequested += (_, _) => QueueThumbnailRefresh();

        RegisterHotkeys();
        PositionShelfWindow(animate: false);
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        SystemEvents.DisplaySettingsChanged += SystemEvents_DisplaySettingsChanged;
        PositionShelfWindow(animate: false);
        QueueThumbnailRefresh();
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        SystemEvents.DisplaySettingsChanged -= SystemEvents_DisplaySettingsChanged;
        StopThumbnailAnimationRefresh();
        NativeMethods.UnregisterHotKey(_windowHandle, ShelfHotkeyId);
        NativeMethods.UnregisterHotKey(_windowHandle, ToggleShelfHotkeyId);
        _source?.RemoveHook(WndProc);
        _shelver?.RestoreAll();
    }

    private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        QueueThumbnailRefresh();
    }

    private void Items_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(ItemCount));
        PositionShelfWindow();
        QueueThumbnailRefresh();
    }

    private void SystemEvents_DisplaySettingsChanged(object? sender, EventArgs e)
    {
        PositionShelfWindow(animate: false);
        QueueThumbnailRefresh();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != NativeMethods.WM_HOTKEY)
        {
            return IntPtr.Zero;
        }

        if (wParam.ToInt32() == ShelfHotkeyId)
        {
            handled = true;
            ShelfForegroundWindow();
            return IntPtr.Zero;
        }

        if (wParam.ToInt32() == ToggleShelfHotkeyId)
        {
            handled = true;
            ToggleShelfVisibility();
            return IntPtr.Zero;
        }

        return IntPtr.Zero;
    }

    private void RegisterHotkeys()
    {
        var shelfHotkeyRegistered = NativeMethods.RegisterHotKey(
            _windowHandle,
            ShelfHotkeyId,
            HotkeyModifiers,
            ShelfHotkeyVirtualKey);

        var toggleHotkeyRegistered = NativeMethods.RegisterHotKey(
            _windowHandle,
            ToggleShelfHotkeyId,
            HotkeyModifiers,
            ToggleShelfHotkeyVirtualKey);

        if (shelfHotkeyRegistered && toggleHotkeyRegistered)
        {
            return;
        }

        StatusMessage = shelfHotkeyRegistered
            ? "Hide hotkey unavailable"
            : toggleHotkeyRegistered
                ? "Shelf hotkey unavailable"
                : "Hotkeys unavailable";
        SystemSounds.Exclamation.Play();
    }

    private void ShelfForegroundWindow()
    {
        if (_shelver is null)
        {
            return;
        }

        try
        {
            _shelver.ShelfForegroundWindow();
            if (_isShelfHidden)
            {
                SetShelfHidden(false);
            }
            else
            {
                PositionShelfWindow();
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32InteropException)
        {
            StatusMessage = ex.Message;
            SystemSounds.Exclamation.Play();
        }
    }

    private void Card_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (GetItemFromSender(sender) is { } item)
        {
            ForgetPeek(item);
            _shelver?.Restore(item);
        }
    }

    private void Card_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is FrameworkElement card && GetItemFromSender(sender) is { } item)
        {
            BeginPeek(item, card);
        }
    }

    private void Card_MouseLeave(object sender, MouseEventArgs e)
    {
        if (GetItemFromSender(sender) is { } item && item == _peekedItem)
        {
            _peekCollapseTimer.Stop();
            _peekCollapseTimer.Start();
        }
    }

    private void RestoreMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (GetItemFromContextMenu(sender) is { } item)
        {
            ForgetPeek(item);
            _shelver?.Restore(item);
        }
    }

    private void CloseSourceMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (GetItemFromContextMenu(sender) is { } item)
        {
            ForgetPeek(item);
            _shelver?.CloseSource(item);
        }
    }

    private void RemoveCardMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (GetItemFromContextMenu(sender) is { } item)
        {
            ForgetPeek(item);
            _shelver?.Remove(item, restoreIfAlive: true);
        }
    }

    private void Card_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement card || card.Tag is not ShelvedWindow item)
        {
            return;
        }

        _cardElements[item] = card;
        SetCardInitialTransform(card);
        AnimateDouble(card, UIElement.OpacityProperty, 1, FastAnimationMs);
        AnimateCardTransform(card, scale: 1, offsetX: 0, FastAnimationMs);
    }

    private void Card_Unloaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: ShelvedWindow item })
        {
            _cardElements.Remove(item);
        }
    }

    private void PreviewSurface_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element && element.Tag is ShelvedWindow item)
        {
            _previewElements[item] = element;
            element.Height = item.IsExpanded ? PeekPreviewHeight : CollapsedPreviewHeight;
            QueueThumbnailRefresh();
        }
    }

    private void PreviewSurface_Unloaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element && element.Tag is ShelvedWindow item)
        {
            _previewElements.Remove(item);
        }
    }

    private void PreviewSurface_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        QueueThumbnailRefresh();
    }

    private void PeekCollapseTimer_Tick(object? sender, EventArgs e)
    {
        _peekCollapseTimer.Stop();
        ClearPeek();
    }

    private void ToggleShelfVisibility()
    {
        SetShelfHidden(!_isShelfHidden);
    }

    private void SetShelfHidden(bool hidden)
    {
        if (_isShelfHidden == hidden)
        {
            return;
        }

        if (hidden)
        {
            ClearPeek();
        }

        _isShelfHidden = hidden;
        RootSurface.IsHitTestVisible = !hidden;
        StatusMessage = hidden ? "Shelf hidden" : "Shelf visible";

        if (!hidden)
        {
            _shelver?.SetThumbnailsVisible(true);
        }

        PositionShelfWindow();
    }

    public void ReportRuntimeError(Exception exception)
    {
        StatusMessage = exception.Message;
        SystemSounds.Exclamation.Play();
    }

    private void BeginPeek(ShelvedWindow item, FrameworkElement card)
    {
        if (_isShelfHidden || !item.IsSourceAlive)
        {
            return;
        }

        _peekCollapseTimer.Stop();

        if (_peekedItem is not null && _peekedItem != item)
        {
            CollapsePeekedCard(_peekedItem);
        }

        _peekedItem = item;
        item.IsExpanded = true;
        Panel.SetZIndex(card, 10);
        AnimateCardTransform(card, scale: 1.018, offsetX: -5, PeekAnimationMs);
        AnimatePreviewHeight(item, PeekPreviewHeight, PeekAnimationMs);

        StatusMessage = $"Peeking {item.ProcessName}";
        PositionShelfWindow();
        QueueThumbnailRefresh();
    }

    private void ClearPeek()
    {
        if (_peekedItem is null)
        {
            return;
        }

        var item = _peekedItem;
        _peekedItem = null;
        CollapsePeekedCard(item);
        StatusMessage = "Ready";
        PositionShelfWindow();
        QueueThumbnailRefresh();
    }

    private void ForgetPeek(ShelvedWindow item)
    {
        if (_peekedItem != item)
        {
            return;
        }

        _peekCollapseTimer.Stop();
        _peekedItem = null;
    }

    private void CollapsePeekedCard(ShelvedWindow item)
    {
        item.IsExpanded = false;

        if (_cardElements.TryGetValue(item, out var card))
        {
            Panel.SetZIndex(card, 0);
            AnimateCardTransform(card, scale: 1, offsetX: 0, PeekAnimationMs);
        }

        AnimatePreviewHeight(item, CollapsedPreviewHeight, PeekAnimationMs);
    }

    private void AnimatePreviewHeight(ShelvedWindow item, double height, int durationMs)
    {
        if (!_previewElements.TryGetValue(item, out var preview))
        {
            return;
        }

        AnimateDouble(preview, FrameworkElement.HeightProperty, height, durationMs, QueueThumbnailRefresh);
        RefreshThumbnailsDuring(durationMs);
    }

    private void QueueThumbnailRefresh()
    {
        if (_thumbnailRefreshQueued)
        {
            return;
        }

        _thumbnailRefreshQueued = true;
        Dispatcher.BeginInvoke(() =>
        {
            _thumbnailRefreshQueued = false;
            UpdateThumbnailDestinations();
        }, System.Windows.Threading.DispatcherPriority.Render);
    }

    private void RefreshThumbnailsDuring(int durationMs)
    {
        _thumbnailAnimationRefreshUntilUtc = DateTime.UtcNow.AddMilliseconds(durationMs + 50);

        if (_isThumbnailAnimationRefreshAttached)
        {
            return;
        }

        _isThumbnailAnimationRefreshAttached = true;
        CompositionTarget.Rendering += CompositionTarget_Rendering;
    }

    private void CompositionTarget_Rendering(object? sender, EventArgs e)
    {
        if (DateTime.UtcNow >= _thumbnailAnimationRefreshUntilUtc)
        {
            StopThumbnailAnimationRefresh();
            QueueThumbnailRefresh();
            return;
        }

        UpdateThumbnailDestinations();
    }

    private void StopThumbnailAnimationRefresh()
    {
        if (!_isThumbnailAnimationRefreshAttached)
        {
            return;
        }

        CompositionTarget.Rendering -= CompositionTarget_Rendering;
        _isThumbnailAnimationRefreshAttached = false;
    }

    private void UpdateThumbnailDestinations()
    {
        if (_shelver is null || _windowHandle == IntPtr.Zero)
        {
            return;
        }

        foreach (var item in Items)
        {
            if (!_previewElements.TryGetValue(item, out var element) ||
                element.ActualWidth <= 0 ||
                element.ActualHeight <= 0)
            {
                continue;
            }

            Rect bounds;
            try
            {
                bounds = element.TransformToAncestor(RootSurface)
                    .TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
            }
            catch (InvalidOperationException)
            {
                continue;
            }

            var source = PresentationSource.FromVisual(this);
            var toDevice = source?.CompositionTarget?.TransformToDevice ?? Matrix.Identity;

            var topLeft = toDevice.Transform(bounds.TopLeft);
            var bottomRight = toDevice.Transform(bounds.BottomRight);
            var destination = new NativeMethods.RECT(
                (int)Math.Round(topLeft.X),
                (int)Math.Round(topLeft.Y),
                (int)Math.Round(bottomRight.X),
                (int)Math.Round(bottomRight.Y));

            _shelver.UpdateThumbnailDestination(item, destination);
        }
    }

    private void PositionShelfWindow(bool animate = true)
    {
        if (_windowHandle == IntPtr.Zero)
        {
            return;
        }

        var targetWidth = GetTargetShelfWidth();
        var right = SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth;
        var targetLeft = _isShelfHidden ? right + HiddenOffset : right - targetWidth;
        var targetOpacity = _isShelfHidden ? 0 : 1;
        var generation = ++_shelfAnimationGeneration;

        BeginAnimation(Window.TopProperty, null);
        BeginAnimation(FrameworkElement.HeightProperty, null);
        Top = SystemParameters.VirtualScreenTop;
        Height = SystemParameters.VirtualScreenHeight;

        if (!animate || !IsLoaded)
        {
            BeginAnimation(Window.LeftProperty, null);
            BeginAnimation(FrameworkElement.WidthProperty, null);
            BeginAnimation(UIElement.OpacityProperty, null);
            Left = targetLeft;
            Width = targetWidth;
            Opacity = targetOpacity;

            if (_isShelfHidden)
            {
                _shelver?.SetThumbnailsVisible(false);
            }

            return;
        }

        AnimateDouble(this, Window.LeftProperty, targetLeft, ShelfAnimationMs);
        AnimateDouble(this, FrameworkElement.WidthProperty, targetWidth, ShelfAnimationMs);
        AnimateDouble(this, UIElement.OpacityProperty, targetOpacity, ShelfAnimationMs, () =>
        {
            if (generation == _shelfAnimationGeneration && _isShelfHidden)
            {
                _shelver?.SetThumbnailsVisible(false);
            }
        });
        RefreshThumbnailsDuring(ShelfAnimationMs);
    }

    private double GetTargetShelfWidth()
    {
        if (_peekedItem is not null && !_isShelfHidden)
        {
            return PeekShelfWidth;
        }

        return ShelfWidth;
    }

    private static void SetCardInitialTransform(FrameworkElement card)
    {
        card.Opacity = 0;

        var scale = new ScaleTransform(0.985, 0.985);
        var translate = new TranslateTransform(18, 0);
        card.RenderTransform = new TransformGroup
        {
            Children =
            {
                scale,
                translate
            }
        };
    }

    private static void EnsureMutableCardTransform(FrameworkElement card)
    {
        if (GetTransform<ScaleTransform>(card) is { } scale)
        {
            if (!scale.IsFrozen)
            {
                return;
            }
        }

        card.RenderTransform = new TransformGroup
        {
            Children =
            {
                new ScaleTransform(1, 1),
                new TranslateTransform()
            }
        };
    }

    private static void AnimateCardTransform(FrameworkElement card, double scale, double offsetX, int durationMs)
    {
        EnsureMutableCardTransform(card);

        if (GetTransform<ScaleTransform>(card) is { } scaleTransform)
        {
            AnimateDouble(scaleTransform, ScaleTransform.ScaleXProperty, scale, durationMs);
            AnimateDouble(scaleTransform, ScaleTransform.ScaleYProperty, scale, durationMs);
        }

        if (GetTransform<TranslateTransform>(card) is { } translateTransform)
        {
            AnimateDouble(translateTransform, TranslateTransform.XProperty, offsetX, durationMs);
        }

        if (Window.GetWindow(card) is MainWindow window)
        {
            window.RefreshThumbnailsDuring(durationMs);
        }
    }

    private static T? GetTransform<T>(FrameworkElement element)
        where T : Transform
    {
        if (element.RenderTransform is T direct)
        {
            return direct;
        }

        if (element.RenderTransform is not TransformGroup group)
        {
            return null;
        }

        return group.Children.OfType<T>().FirstOrDefault();
    }

    private static void AnimateDouble(
        DependencyObject target,
        DependencyProperty property,
        double to,
        int durationMs,
        Action? completed = null)
    {
        var animation = new DoubleAnimation
        {
            To = to,
            Duration = TimeSpan.FromMilliseconds(durationMs),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };

        if (completed is not null)
        {
            animation.Completed += (_, _) => completed();
        }

        if (target is UIElement element)
        {
            element.BeginAnimation(property, animation, HandoffBehavior.SnapshotAndReplace);
            return;
        }

        if (target is Animatable animatable)
        {
            animatable.BeginAnimation(property, animation, HandoffBehavior.SnapshotAndReplace);
        }
    }

    private static ShelvedWindow? GetItemFromSender(object sender)
    {
        return sender is FrameworkElement { Tag: ShelvedWindow item } ? item : null;
    }

    private static ShelvedWindow? GetItemFromContextMenu(object sender)
    {
        if (sender is not MenuItem menuItem ||
            menuItem.Parent is not ContextMenu contextMenu ||
            contextMenu.PlacementTarget is not FrameworkElement { Tag: ShelvedWindow item })
        {
            return null;
        }

        return item;
    }

    private void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
