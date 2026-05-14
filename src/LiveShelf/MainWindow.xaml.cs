using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Media;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Win32;
using Brush = System.Windows.Media.Brush;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Panel = System.Windows.Controls.Panel;
using Point = System.Windows.Point;
using ScrollBar = System.Windows.Controls.Primitives.ScrollBar;

namespace LiveShelf;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private const int ShelfHotkeyId = 0x5153;
    private const int ToggleShelfHotkeyId = 0x4848;
    private const int EmergencyRestoreHotkeyId = 0x5252;
    private const int HotkeyModifiers = NativeMethods.MOD_ALT | NativeMethods.MOD_CONTROL | NativeMethods.MOD_NOREPEAT;
    private const int EmergencyRestoreHotkeyModifiers = NativeMethods.MOD_ALT | NativeMethods.MOD_CONTROL | NativeMethods.MOD_SHIFT | NativeMethods.MOD_NOREPEAT;
    private const int ShelfHotkeyVirtualKey = 0x53; // S
    private const int ToggleShelfHotkeyVirtualKey = 0x48; // H
    private const int EmergencyRestoreHotkeyVirtualKey = 0x52; // R

    private const double ShelfWidth = 256;
    private const double PeekShelfWidth = 440;
    private const double ZoomShelfWidth = 780;
    private const double RailWidth = 48;
    private const double HiddenOffset = 18;
    private const double CollapsedPreviewHeight = 92;
    private const double PeekPreviewHeight = 248;
    private const double ZoomPreviewHeight = 560;
    private const int CardEntryAnimationMs = 340;
    private const int ShelfAnimationMs = 420;
    private const int ShelfCloseAnimationMs = 700;
    private const int PeekAnimationMs = 380;
    private const int ZoomAnimationMs = 420;
    private const int AttentionAnimationMs = 720;
    private const int ReorderAnimationMs = 210;
    private const int ReorderGapAnimationMs = 130;
    private const int DragShelfPollMs = 80;
    private const int DragShelfDwellMs = 420;
    private const int DragShelfHotZoneSize = 120;
    private const int DragShelfTitleBandHeight = 96;
    private const int RailCollapseDelayMs = 1500;
    private const int RailCollapseAnimationMs = 520;
    private const int RailExpandAnimationMs = 260;
    private const double ThumbnailFrameInsetDip = 2;

    private static readonly Color CardBackgroundColor = Color.FromArgb(110, 43, 48, 56);
    private static readonly Color CardBorderColor = Color.FromArgb(50, 255, 255, 255);
    private static readonly Color AttentionBorderColor = Color.FromRgb(117, 196, 255);
    private static readonly Color AttentionBackgroundColor = Color.FromArgb(210, 38, 49, 61);
    private static readonly Brush AgentIdleBrush = new SolidColorBrush(Color.FromRgb(145, 156, 172));
    private static readonly Brush AgentPendingBrush = new SolidColorBrush(Color.FromRgb(255, 196, 87));
    private static readonly Brush AgentConnectedBrush = new SolidColorBrush(Color.FromRgb(95, 220, 139));
    private static readonly Brush AgentFailedBrush = new SolidColorBrush(Color.FromRgb(238, 105, 117));

    private readonly ObservableCollection<ShelvedWindow> _items = [];
    private readonly Dictionary<ShelvedWindow, FrameworkElement> _cardElements = [];
    private readonly Dictionary<ShelvedWindow, FrameworkElement> _previewHostElements = [];
    private readonly Dictionary<ShelvedWindow, FrameworkElement> _previewElements = [];
    private readonly DispatcherTimer _peekCollapseTimer;
    private readonly DispatcherTimer _dragShelfTimer;
    private readonly DispatcherTimer _railCollapseTimer;
    private HwndSource? _source;
    private IntPtr _windowHandle;
    private WindowShelver? _shelver;
    private ShelvedWindow? _peekedItem;
    private ShelvedWindow? _zoomedItem;
    private bool _thumbnailRefreshQueued;
    private bool _isThumbnailAnimationRefreshAttached;
    private bool _isShelfHidden;
    private bool _hasRestoredShelvedWindowsForShutdown;
    private DateTime _thumbnailAnimationRefreshUntilUtc;
    private DateTime _dragShelfCandidateEnteredUtc;
    private int _shelfAnimationGeneration;
    private IntPtr _dragShelfCandidateHwnd;
    private bool _dragShelfTriggeredWhilePressed;
    private Point _cardDragStartPoint;
    private ShelvedWindow? _cardDragItem;
    private Dictionary<ShelvedWindow, DragCardLayout> _cardDragLayouts = [];
    private bool _isReorderingCards;
    private int _cardDragStartIndex = -1;
    private int _liveReorderDropIndex = -1;
    private bool _isRailMode;
    private DateTime _lastHitTestLogUtc = DateTime.MinValue;
    private string _lastHitTestLogKey = string.Empty;
    private string _statusMessage = "Ready";
    private string _agentConnectionText = "Connect";
    private string _agentConnectionToolTip = "Connect Codex or Claude Code hooks";
    private Brush _agentConnectionBrush = AgentIdleBrush;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
        Items.CollectionChanged += Items_CollectionChanged;

        _peekCollapseTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(260)
        };
        _peekCollapseTimer.Tick += PeekCollapseTimer_Tick;

        _dragShelfTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(DragShelfPollMs)
        };
        _dragShelfTimer.Tick += DragShelfTimer_Tick;

        _railCollapseTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(RailCollapseDelayMs)
        };
        _railCollapseTimer.Tick += RailCollapseTimer_Tick;
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
        Trace.WriteLine(
            $"LiveShelf overlay exstyle=0x{NativeMethods.GetWindowLong(_windowHandle, NativeMethods.GWL_EXSTYLE):X8}");

        _source = HwndSource.FromHwnd(_windowHandle);
        _source?.AddHook(WndProc);

        try
        {
            if (_source?.CompositionTarget is { } compositionTarget)
            {
                compositionTarget.BackgroundColor = Color.FromArgb(0, 0, 0, 0);
            }

            NativeMethods.EnableMicaBackdrop(_windowHandle);
        }
        catch
        {
            // Mica/acrylic not available, shelf stays opaque
        }

        _shelver = new WindowShelver(_windowHandle, _items);
        _shelver.StatusChanged += (_, message) => StatusMessage = message;
        _shelver.ThumbnailRefreshRequested += (_, _) => QueueThumbnailRefresh();
        _shelver.AttentionRequested += (_, item) => Dispatcher.InvokeAsync(() => RunAttentionAlert(item));
        _shelver.AgentCompletionRequested += (_, item) =>
        {
            if (_isShelfHidden)
            {
                SetShelfHidden(false);
                Dispatcher.InvokeAsync(() => RunAttentionAlert(item), DispatcherPriority.Loaded);
            }
        };

        RefreshAgentConnectionStatus();
        RegisterHotkeys();
        PositionShelfWindow(animate: false);
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        SystemEvents.DisplaySettingsChanged += SystemEvents_DisplaySettingsChanged;
        _dragShelfTimer.Start();
        PositionShelfWindow(animate: false);
        QueueThumbnailRefresh();
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        SystemEvents.DisplaySettingsChanged -= SystemEvents_DisplaySettingsChanged;
        StopThumbnailAnimationRefresh();
        NativeMethods.UnregisterHotKey(_windowHandle, ShelfHotkeyId);
        NativeMethods.UnregisterHotKey(_windowHandle, ToggleShelfHotkeyId);
        NativeMethods.UnregisterHotKey(_windowHandle, EmergencyRestoreHotkeyId);
        _dragShelfTimer.Stop();
        _source?.RemoveHook(WndProc);
        RestoreShelvedWindowsForShutdown();
    }

    internal void RestoreShelvedWindowsForShutdown()
    {
        if (_hasRestoredShelvedWindowsForShutdown)
        {
            return;
        }

        _hasRestoredShelvedWindowsForShutdown = true;
        _shelver?.RestoreAll();
    }

    private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        QueueThumbnailRefresh();
    }

    private void Window_MouseLeave(object sender, MouseEventArgs e)
    {
        if (_zoomedItem is { } item)
        {
            DeactivateZoom(item);
            RunAfter(ZoomAnimationMs, () =>
            {
                if (!IsMouseOver && _peekedItem == item && _zoomedItem is null)
                {
                    ClearPeek();
                }
            });
            return;
        }

        if (_peekedItem is not null)
        {
            _peekCollapseTimer.Stop();
            _peekCollapseTimer.Start();
        }

        if (!_isRailMode && !_isShelfHidden && Items.Count > 0)
        {
            _railCollapseTimer.Stop();
            _railCollapseTimer.Start();
        }
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
        if (msg == NativeMethods.WM_NCHITTEST)
        {
            var screenX = GetSignedLoWord(lParam);
            var screenY = GetSignedHiWord(lParam);
            var inside = IsPointInsideShelfHitRegion(screenX, screenY, out var hitArea);
            LogHitTest(screenX, screenY, inside, hitArea);
            handled = true;
            return new IntPtr(inside ? NativeMethods.HTCLIENT : NativeMethods.HTTRANSPARENT);
        }

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

        if (wParam.ToInt32() == EmergencyRestoreHotkeyId)
        {
            handled = true;
            RestoreShelvedWindowsForShutdown();
            StatusMessage = "Restored shelved windows";
            return IntPtr.Zero;
        }

        return IntPtr.Zero;
    }

    private bool IsPointInsideShelfHitRegion(int screenX, int screenY, out string hitArea)
    {
        hitArea = "outside";
        if ((!ShouldShowShelf && !ShouldShowRail) || RootSurface is null)
        {
            hitArea = "hidden";
            return false;
        }

        var local = PointFromScreen(new Point(screenX, screenY));
        if (local.X < 0 ||
            local.Y < 0 ||
            local.X >= ActualWidth ||
            local.Y >= ActualHeight)
        {
            return false;
        }

        DependencyObject? hitObject;
        try
        {
            hitObject = InputHitTest(local) as DependencyObject;
        }
        catch (InvalidOperationException)
        {
            hitObject = null;
        }

        if (hitObject is null)
        {
            hitArea = "empty";
            return false;
        }

        if (FindAncestorWithName(hitObject, "HeaderSurface") is not null)
        {
            hitArea = "header";
            return true;
        }

        if (FindAncestorWithName(hitObject, "StatusSurface") is not null)
        {
            hitArea = "status";
            return true;
        }

        if (FindAncestorWithName(hitObject, "RailSurface") is not null)
        {
            hitArea = "rail";
            return true;
        }

        if (FindTaggedCardAncestor(hitObject) is not null)
        {
            hitArea = "card";
            return true;
        }

        if (FindAncestor<Button>(hitObject) is not null ||
            FindAncestor<MenuItem>(hitObject) is not null ||
            FindAncestor<ScrollBar>(hitObject) is not null)
        {
            hitArea = "control";
            return true;
        }

        hitArea = hitObject.GetType().Name;
        return false;
    }

    private void LogHitTest(int screenX, int screenY, bool inside, string hitArea)
    {
        var key = $"{inside}:{hitArea}";
        var now = DateTime.UtcNow;
        if (key == _lastHitTestLogKey && now - _lastHitTestLogUtc < TimeSpan.FromMilliseconds(600))
        {
            return;
        }

        _lastHitTestLogKey = key;
        _lastHitTestLogUtc = now;
        Trace.WriteLine(
            $"LiveShelf WM_NCHITTEST point=({screenX},{screenY}) result={(inside ? "HTCLIENT" : "HTTRANSPARENT")} area={hitArea}");
    }

    private static int GetSignedLoWord(IntPtr value)
    {
        return unchecked((short)((long)value & 0xFFFF));
    }

    private static int GetSignedHiWord(IntPtr value)
    {
        return unchecked((short)(((long)value >> 16) & 0xFFFF));
    }

    private static FrameworkElement? FindTaggedCardAncestor(DependencyObject? current)
    {
        while (current is not null)
        {
            if (current is FrameworkElement { Tag: ShelvedWindow } element)
            {
                return element;
            }

            current = GetParent(current);
        }

        return null;
    }

    private static FrameworkElement? FindAncestorWithName(DependencyObject? current, string name)
    {
        while (current is not null)
        {
            if (current is FrameworkElement { Name: var currentName } element &&
                string.Equals(currentName, name, StringComparison.Ordinal))
            {
                return element;
            }

            current = GetParent(current);
        }

        return null;
    }

    private static T? FindAncestor<T>(DependencyObject? current)
        where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T typed)
            {
                return typed;
            }

            current = GetParent(current);
        }

        return null;
    }

    private static DependencyObject? GetParent(DependencyObject current)
    {
        try
        {
            if (current is Visual)
            {
                return VisualTreeHelper.GetParent(current);
            }
        }
        catch (InvalidOperationException)
        {
        }

        return current switch
        {
            FrameworkElement element => element.Parent,
            FrameworkContentElement contentElement => contentElement.Parent,
            _ => null
        };
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
        var emergencyRestoreHotkeyRegistered = NativeMethods.RegisterHotKey(
            _windowHandle,
            EmergencyRestoreHotkeyId,
            EmergencyRestoreHotkeyModifiers,
            EmergencyRestoreHotkeyVirtualKey);

        if (shelfHotkeyRegistered && toggleHotkeyRegistered && emergencyRestoreHotkeyRegistered)
        {
            return;
        }

        StatusMessage = shelfHotkeyRegistered
            ? toggleHotkeyRegistered
                ? "Emergency restore hotkey unavailable"
                : "Hide hotkey unavailable"
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

    private void Card_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement card || GetItemFromSender(sender) is not { } item)
        {
            return;
        }

        _cardDragItem = item;
        _cardDragStartPoint = e.GetPosition(this);
        _cardDragStartIndex = Items.IndexOf(item);
        _liveReorderDropIndex = _cardDragStartIndex;
        _cardDragLayouts = CaptureDragCardLayouts();
        _isReorderingCards = false;
        Panel.SetZIndex(card, 50);
        card.CaptureMouse();
    }

    private void Card_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (GetItemFromSender(sender) is { } item)
        {
            if (sender is FrameworkElement card && card.IsMouseCaptured)
            {
                card.ReleaseMouseCapture();
            }

            if (_isReorderingCards)
            {
                var fromIndex = Items.IndexOf(item);
                var toIndex = _liveReorderDropIndex >= 0
                    ? _liveReorderDropIndex
                    : fromIndex;

                ResetLiveReorderGaps(item);
                if (fromIndex >= 0 && toIndex >= 0 && fromIndex != toIndex)
                {
                    Items.Move(fromIndex, toIndex);
                    PositionShelfWindow();
                    QueueThumbnailRefresh();
                }

                AnimateDraggedCardHome(item);
                QueueThumbnailRefresh();
                _cardDragItem = null;
                _cardDragLayouts.Clear();
                _cardDragStartIndex = -1;
                _liveReorderDropIndex = -1;
                _isReorderingCards = false;
                e.Handled = true;
                return;
            }

            _cardDragItem = null;
            _cardDragLayouts.Clear();
            _cardDragStartIndex = -1;
            _liveReorderDropIndex = -1;
            if (sender is FrameworkElement releasedCard)
            {
                Panel.SetZIndex(releasedCard, item.IsExpanded ? 10 : 0);
                releasedCard.Opacity = 1;
            }

            if (item.IsZoomed)
            {
                e.Handled = true;
                return;
            }

            ForgetPeek(item);
            _shelver?.Restore(item);
        }
    }

    private void Card_MouseMove(object sender, MouseEventArgs e)
    {
        if (_cardDragItem is null || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var position = e.GetPosition(this);
        if (!_isReorderingCards &&
            Math.Abs(position.X - _cardDragStartPoint.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(position.Y - _cardDragStartPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        _isReorderingCards = true;
        UpdateDraggedCardVisual(_cardDragItem, position);
        UpdateLiveReorderGaps(_cardDragItem, position);
        e.Handled = true;
    }

    private void Card_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is FrameworkElement card && GetItemFromSender(sender) is { } item)
        {
            if (item.IsMediaCard)
            {
                return;
            }

            BeginPeek(item, card);
        }
    }

    private void Card_MouseLeave(object sender, MouseEventArgs e)
    {
        if (GetItemFromSender(sender) is { } item && item == _peekedItem)
        {
            if (item == _zoomedItem)
            {
                return;
            }

            _peekCollapseTimer.Stop();
            _peekCollapseTimer.Start();
        }
    }

    private void Card_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (GetItemFromSender(sender) is not { } item)
        {
            return;
        }

        if (item.IsMediaCard)
        {
            return;
        }

        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0 || item != _peekedItem)
        {
            return;
        }

        e.Handled = true;
        if (e.Delta > 0)
        {
            ActivateZoom(item);
        }
        else if (e.Delta < 0 && item.IsZoomed)
        {
            DeactivateZoom(item);
        }
    }

    private void Card_ManipulationStarting(object sender, ManipulationStartingEventArgs e)
    {
        e.ManipulationContainer = RootSurface;
        e.Handled = true;
    }

    private void Card_ManipulationDelta(object sender, ManipulationDeltaEventArgs e)
    {
        if (GetItemFromSender(sender) is not { } item || item != _peekedItem)
        {
            return;
        }

        if (item.IsMediaCard)
        {
            return;
        }

        var scaleDelta = (e.DeltaManipulation.Scale.X + e.DeltaManipulation.Scale.Y) / 2;
        if (Math.Abs(scaleDelta - 1) < 0.015)
        {
            return;
        }

        e.Handled = true;
        if (scaleDelta < 1 && item != _zoomedItem)
        {
            ActivateZoom(item);
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

    public string AgentConnectionText
    {
        get => _agentConnectionText;
        private set
        {
            if (_agentConnectionText == value)
            {
                return;
            }

            _agentConnectionText = value;
            OnPropertyChanged(nameof(AgentConnectionText));
        }
    }

    public Brush AgentConnectionBrush
    {
        get => _agentConnectionBrush;
        private set
        {
            if (_agentConnectionBrush == value)
            {
                return;
            }

            _agentConnectionBrush = value;
            OnPropertyChanged(nameof(AgentConnectionBrush));
        }
    }

    public string AgentConnectionToolTip
    {
        get => _agentConnectionToolTip;
        private set
        {
            if (_agentConnectionToolTip == value)
            {
                return;
            }

            _agentConnectionToolTip = value;
            OnPropertyChanged(nameof(AgentConnectionToolTip));
        }
    }

    private void AgentsButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { ContextMenu: { } menu } button)
        {
            return;
        }

        menu.PlacementTarget = button;
        menu.IsOpen = true;
        e.Handled = true;
    }

    private void EnableCodexTrackingMenuItem_Click(object sender, RoutedEventArgs e)
    {
        InstallAgentHooks("Codex", AgentHookInstaller.EnableCodexTracking);
    }

    private void EnableClaudeTrackingMenuItem_Click(object sender, RoutedEventArgs e)
    {
        InstallAgentHooks("Claude", AgentHookInstaller.EnableClaudeTracking);
    }

    private void TestCodexTrackingMenuItem_Click(object sender, RoutedEventArgs e)
    {
        InstallAgentHooks("Codex test", AgentHookInstaller.TestCodexTracking);
    }

    private void TestClaudeTrackingMenuItem_Click(object sender, RoutedEventArgs e)
    {
        InstallAgentHooks("Claude test", AgentHookInstaller.TestClaudeTracking);
    }

    private void EnableAllAgentTrackingMenuItem_Click(object sender, RoutedEventArgs e)
    {
        InstallAgentHooks("Agents", AgentHookInstaller.EnableAllTracking);
    }

    private void InstallAgentHooks(string label, Func<AgentHookInstallResult> install)
    {
        AgentConnectionText = "Installing";
        AgentConnectionBrush = AgentPendingBrush;
        AgentConnectionToolTip = $"Installing {label} hooks...";

        try
        {
            var result = install();
            StatusMessage = result.Message;
            AgentConnectionToolTip = result.Message;
            if (!result.Success)
            {
                AgentConnectionText = "Failed";
                AgentConnectionBrush = AgentFailedBrush;
                SystemSounds.Exclamation.Play();
                return;
            }

            AgentConnectionText = label.EndsWith("test", StringComparison.OrdinalIgnoreCase)
                ? "Hook OK"
                : label == "Agents"
                    ? "Connected"
                    : $"{label} on";
            AgentConnectionBrush = AgentConnectedBrush;
            if (!label.EndsWith("test", StringComparison.OrdinalIgnoreCase))
            {
                RefreshAgentConnectionStatus();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            StatusMessage = ex.Message;
            AgentConnectionText = "Failed";
            AgentConnectionBrush = AgentFailedBrush;
            AgentConnectionToolTip = ex.Message;
            SystemSounds.Exclamation.Play();
        }
    }

    private void RefreshAgentConnectionStatus()
    {
        var status = AgentHookInstaller.GetInstalledTrackingStatus();
        if (status.Codex && status.Claude)
        {
            AgentConnectionText = "Connected";
            AgentConnectionBrush = AgentConnectedBrush;
            AgentConnectionToolTip = "Codex and Claude hooks are connected";
            return;
        }

        if (status.Codex)
        {
            AgentConnectionText = "Codex on";
            AgentConnectionBrush = AgentConnectedBrush;
            AgentConnectionToolTip = "Codex hooks are connected";
            return;
        }

        if (status.Claude)
        {
            AgentConnectionText = "Claude on";
            AgentConnectionBrush = AgentConnectedBrush;
            AgentConnectionToolTip = "Claude hooks are connected";
            return;
        }

        AgentConnectionText = "Connect";
        AgentConnectionBrush = AgentIdleBrush;
        AgentConnectionToolTip = "Connect Codex or Claude Code hooks";
    }

    private void MediaPlayPauseButton_Click(object sender, RoutedEventArgs e)
    {
        if (GetItemFromSender(sender) is { } item)
        {
            _shelver?.ToggleMediaPlayback(item);
            e.Handled = true;
        }
    }

    private void FallbackRestoreButton_Click(object sender, RoutedEventArgs e)
    {
        if (GetItemFromSender(sender) is { } item)
        {
            ForgetPeek(item);
            _shelver?.Restore(item);
            e.Handled = true;
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
        if (card is Border border)
        {
            border.Background = new SolidColorBrush(CardBackgroundColor);
            border.BorderBrush = new SolidColorBrush(CardBorderColor);
        }

        AnimateDouble(card, UIElement.OpacityProperty, 1, CardEntryAnimationMs);
        AnimateCardTransform(card, scale: 1, offsetX: 0, CardEntryAnimationMs);
    }

    private void Card_Unloaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: ShelvedWindow item })
        {
            _cardElements.Remove(item);
        }
    }

    private void PreviewHost_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element && element.Tag is ShelvedWindow item)
        {
            _previewHostElements[item] = element;
            element.Height = item.IsZoomed
                ? GetZoomPreviewHeight()
                : item.IsExpanded
                    ? PeekPreviewHeight
                    : CollapsedPreviewHeight;
            UpdatePreviewFrame(item);
            QueueThumbnailRefresh();
        }
    }

    private void PreviewHost_Unloaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element && element.Tag is ShelvedWindow item)
        {
            _previewHostElements.Remove(item);
        }
    }

    private void PreviewHost_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: ShelvedWindow item })
        {
            UpdatePreviewFrame(item);
        }

        QueueThumbnailRefresh();
    }

    private void PreviewSurface_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element && element.Tag is ShelvedWindow item)
        {
            _previewElements[item] = element;
            UpdatePreviewFrame(item);
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

    private void PreviewSurface_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ShelvedWindow item })
        {
            return;
        }

        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0 && item == _peekedItem)
        {
            e.Handled = true;
            if (e.Delta > 0)
            {
                ActivateZoom(item);
            }
            else if (e.Delta < 0 && item.IsZoomed)
            {
                DeactivateZoom(item);
            }
        }
    }

    private void PeekCollapseTimer_Tick(object? sender, EventArgs e)
    {
        _peekCollapseTimer.Stop();
        ClearPeek();
    }

    private void DragShelfTimer_Tick(object? sender, EventArgs e)
    {
        var leftButtonDown = (NativeMethods.GetAsyncKeyState(NativeMethods.VK_LBUTTON) & unchecked((short)0x8000)) != 0;
        if (!leftButtonDown)
        {
            ResetDragShelfCandidate();
            _dragShelfTriggeredWhilePressed = false;
            return;
        }

        if (_dragShelfTriggeredWhilePressed ||
            !NativeMethods.GetCursorPos(out var point) ||
            !IsPointInDragShelfHotZone(point))
        {
            ResetDragShelfCandidate();
            return;
        }

        var foreground = NativeMethods.GetForegroundWindow();
        if (foreground == IntPtr.Zero ||
            foreground == _windowHandle ||
            !NativeMethods.IsNormalAppWindow(foreground, _windowHandle, out _) ||
            !IsLikelyTitleBandDrag(foreground, point))
        {
            ResetDragShelfCandidate();
            return;
        }

        var now = DateTime.UtcNow;
        if (foreground != _dragShelfCandidateHwnd)
        {
            _dragShelfCandidateHwnd = foreground;
            _dragShelfCandidateEnteredUtc = now;
            return;
        }

        if ((now - _dragShelfCandidateEnteredUtc).TotalMilliseconds < DragShelfDwellMs)
        {
            return;
        }

        _dragShelfTriggeredWhilePressed = true;
        ResetDragShelfCandidate();
        ShelfForegroundWindow();
    }

    private static bool IsPointInDragShelfHotZone(NativeMethods.POINT point)
    {
        var virtualScreen = NativeMethods.GetVirtualScreenRect();
        return point.X >= virtualScreen.Right - DragShelfHotZoneSize &&
               point.X <= virtualScreen.Right &&
               point.Y >= virtualScreen.Bottom - DragShelfHotZoneSize &&
               point.Y <= virtualScreen.Bottom;
    }

    private static bool IsLikelyTitleBandDrag(IntPtr hwnd, NativeMethods.POINT point)
    {
        if (!NativeMethods.GetWindowRect(hwnd, out var rect))
        {
            return false;
        }

        return point.X >= rect.Left &&
               point.X <= rect.Right &&
               point.Y >= rect.Top &&
               point.Y <= rect.Bottom &&
               point.Y - rect.Top <= DragShelfTitleBandHeight;
    }

    private void ResetDragShelfCandidate()
    {
        _dragShelfCandidateHwnd = IntPtr.Zero;
        _dragShelfCandidateEnteredUtc = DateTime.MinValue;
    }

    private int GetCardDropIndex(ShelvedWindow item, Point position)
    {
        var fromIndex = Items.IndexOf(item);
        if (fromIndex < 0 || Items.Count < 2)
        {
            return fromIndex;
        }

        var toIndex = 0;
        for (var index = 0; index < Items.Count; index++)
        {
            if (ReferenceEquals(Items[index], item))
            {
                continue;
            }

            if (!_cardElements.TryGetValue(Items[index], out var card))
            {
                continue;
            }

            Rect bounds;
            try
            {
                bounds = card.TransformToAncestor(this)
                    .TransformBounds(new Rect(0, 0, card.ActualWidth, card.ActualHeight));
            }
            catch (InvalidOperationException)
            {
                continue;
            }

            if (position.Y < bounds.Top + bounds.Height / 2)
            {
                break;
            }

            toIndex++;
        }

        return toIndex;
    }

    private int GetCardDropIndexFromDragStart(ShelvedWindow draggedItem, Point position)
    {
        var fromIndex = _cardDragStartIndex;
        if (fromIndex < 0 || Items.Count < 2 || _cardDragLayouts.Count == 0)
        {
            return fromIndex;
        }

        var toIndex = 0;
        for (var index = 0; index < Items.Count; index++)
        {
            var item = Items[index];
            if (ReferenceEquals(item, draggedItem))
            {
                continue;
            }

            if (!_cardDragLayouts.TryGetValue(item, out var layout))
            {
                continue;
            }

            if (position.Y < layout.Top + layout.Height / 2)
            {
                break;
            }

            toIndex++;
        }

        return toIndex;
    }

    private void UpdateDraggedCardVisual(ShelvedWindow item, Point position)
    {
        if (!_cardElements.TryGetValue(item, out var card))
        {
            return;
        }

        EnsureMutableCardTransform(card);
        if (GetTransform<TranslateTransform>(card) is { } translate)
        {
            translate.BeginAnimation(TranslateTransform.YProperty, null);
            translate.Y = position.Y - _cardDragStartPoint.Y;
        }

        card.Opacity = 0.94;
        Panel.SetZIndex(card, 50);
    }

    private void UpdateLiveReorderGaps(ShelvedWindow draggedItem, Point position)
    {
        if (_cardDragStartIndex < 0 || Items.Count < 2)
        {
            return;
        }

        var dropIndex = GetCardDropIndexFromDragStart(draggedItem, position);
        if (dropIndex < 0 || dropIndex == _liveReorderDropIndex)
        {
            return;
        }

        _liveReorderDropIndex = dropIndex;
        var draggedExtent = GetCardVerticalExtent(draggedItem);

        for (var index = 0; index < Items.Count; index++)
        {
            var item = Items[index];
            if (ReferenceEquals(item, draggedItem) || !_cardElements.TryGetValue(item, out var card))
            {
                continue;
            }

            var offset = 0d;
            if (dropIndex > _cardDragStartIndex && index > _cardDragStartIndex && index <= dropIndex)
            {
                offset = -draggedExtent;
            }
            else if (dropIndex < _cardDragStartIndex && index >= dropIndex && index < _cardDragStartIndex)
            {
                offset = draggedExtent;
            }

            EnsureMutableCardTransform(card);
            if (GetTransform<TranslateTransform>(card) is { } translate)
            {
                AnimateDouble(translate, TranslateTransform.YProperty, offset, ReorderGapAnimationMs);
            }
        }
    }

    private double GetCardVerticalExtent(ShelvedWindow item)
    {
        if (_cardDragLayouts.TryGetValue(item, out var layout))
        {
            return layout.Extent;
        }

        if (!_cardElements.TryGetValue(item, out var card))
        {
            return 0;
        }

        return card.ActualHeight + card.Margin.Top + card.Margin.Bottom;
    }

    private Dictionary<ShelvedWindow, DragCardLayout> CaptureDragCardLayouts()
    {
        var layouts = new Dictionary<ShelvedWindow, DragCardLayout>();
        foreach (var pair in _cardElements)
        {
            if (!TryGetCardTop(pair.Value, out var top))
            {
                continue;
            }

            layouts[pair.Key] = new DragCardLayout(
                top,
                pair.Value.ActualHeight,
                pair.Value.ActualHeight + pair.Value.Margin.Top + pair.Value.Margin.Bottom);
        }

        return layouts;
    }

    private void ResetLiveReorderGaps(ShelvedWindow draggedItem)
    {
        foreach (var pair in _cardElements)
        {
            if (ReferenceEquals(pair.Key, draggedItem))
            {
                continue;
            }

            EnsureMutableCardTransform(pair.Value);
            if (GetTransform<TranslateTransform>(pair.Value) is { } translate)
            {
                translate.BeginAnimation(TranslateTransform.YProperty, null);
                translate.Y = 0;
            }
        }
    }

    private void AnimateDraggedCardHome(ShelvedWindow item)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (!_cardElements.TryGetValue(item, out var card))
            {
                return;
            }

            EnsureMutableCardTransform(card);
            if (GetTransform<TranslateTransform>(card) is { } translate)
            {
                AnimateDouble(translate, TranslateTransform.YProperty, 0, ReorderAnimationMs, () =>
                {
                    Panel.SetZIndex(card, item.IsExpanded ? 10 : 0);
                    card.Opacity = 1;
                    QueueThumbnailRefresh();
                });
            }
            else
            {
                Panel.SetZIndex(card, item.IsExpanded ? 10 : 0);
                card.Opacity = 1;
                QueueThumbnailRefresh();
            }
        }, DispatcherPriority.Loaded);
    }

    private Dictionary<ShelvedWindow, double> CaptureCardTops()
    {
        var tops = new Dictionary<ShelvedWindow, double>();
        foreach (var pair in _cardElements)
        {
            if (TryGetCardTop(pair.Value, out var top))
            {
                tops[pair.Key] = top;
            }
        }

        return tops;
    }

    private void AnimateReorderedCards(Dictionary<ShelvedWindow, double> oldTops, ShelvedWindow? excludedItem = null)
    {
        Dispatcher.BeginInvoke(() =>
        {
            foreach (var pair in _cardElements)
            {
                if (ReferenceEquals(pair.Key, excludedItem))
                {
                    continue;
                }

                if (!oldTops.TryGetValue(pair.Key, out var oldTop) ||
                    !TryGetCardTop(pair.Value, out var newTop))
                {
                    continue;
                }

                var delta = oldTop - newTop;
                if (Math.Abs(delta) < 0.5)
                {
                    continue;
                }

                EnsureMutableCardTransform(pair.Value);
                if (GetTransform<TranslateTransform>(pair.Value) is { } translate)
                {
                    translate.Y = delta;
                    AnimateDouble(translate, TranslateTransform.YProperty, 0, ReorderAnimationMs);
                }
            }
        }, DispatcherPriority.Loaded);
    }

    private bool TryGetCardTop(FrameworkElement card, out double top)
    {
        top = 0;
        try
        {
            var bounds = card.TransformToAncestor(this)
                .TransformBounds(new Rect(0, 0, card.ActualWidth, card.ActualHeight));
            top = bounds.Top;
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
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
            _railCollapseTimer.Stop();
            _shelver?.SetThumbnailsVisible(false);
            StopThumbnailAnimationRefresh();
        }
        else if (_isRailMode)
        {
            _isRailMode = false;
            OnPropertyChanged(nameof(FullShelfVisibility));
            OnPropertyChanged(nameof(RailVisibility));
        }

        _isShelfHidden = hidden;
        StatusMessage = hidden ? "Shelf hidden" : "Shelf visible";

        if (!hidden)
        {
            _shelver?.SetThumbnailsVisible(true);
        }

        PositionShelfWindow();

        if (!hidden)
        {
            RefreshThumbnailsAfterLayout(ShelfAnimationMs);
        }
    }

    public void ReportRuntimeError(Exception exception)
    {
        StatusMessage = exception.Message;
        SystemSounds.Exclamation.Play();
    }

    private void BeginPeek(ShelvedWindow item, FrameworkElement card)
    {
        if (_isShelfHidden || !item.IsSourceAlive || item.IsMediaCard)
        {
            return;
        }

        _peekCollapseTimer.Stop();

        if (_peekedItem is not null && _peekedItem != item)
        {
            CollapsePeekedCard(_peekedItem);
        }

        _peekedItem = item;
        item.MarkAttentionSeen();
        item.IsExpanded = true;
        Panel.SetZIndex(card, 10);
        AnimateCardTransform(card, scale: 1, offsetX: 0, PeekAnimationMs);
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
        if (_zoomedItem == item)
        {
            _zoomedItem = null;
            item.IsZoomed = false;
        }
    }

    private void CollapsePeekedCard(ShelvedWindow item)
    {
        if (_zoomedItem == item)
        {
            _zoomedItem = null;
        }

        item.IsExpanded = false;
        item.IsZoomed = false;

        if (_cardElements.TryGetValue(item, out var card))
        {
            Panel.SetZIndex(card, 0);
            AnimateCardTransform(card, scale: 1, offsetX: 0, PeekAnimationMs);
        }

        AnimatePreviewHeight(item, CollapsedPreviewHeight, PeekAnimationMs);
    }

    private void ActivateZoom(ShelvedWindow item)
    {
        if (_isShelfHidden || item != _peekedItem || !item.IsSourceAlive || item.IsMediaCard)
        {
            return;
        }

        if (_zoomedItem == item)
        {
            return;
        }

        _zoomedItem = item;
        item.IsZoomed = true;
        item.MarkAttentionSeen();
        StatusMessage = $"Previewing {item.ProcessName}";

        AnimatePreviewHeight(item, GetZoomPreviewHeight(), ZoomAnimationMs);
        if (_cardElements.TryGetValue(item, out var card))
        {
            AnimateCardTransform(card, scale: 1, offsetX: 0, ZoomAnimationMs);
        }

        PositionShelfWindow();
        QueueThumbnailRefresh();
    }

    private void DeactivateZoom(ShelvedWindow item)
    {
        if (_zoomedItem != item)
        {
            return;
        }

        _zoomedItem = null;
        item.IsZoomed = false;
        AnimatePreviewHeight(item, PeekPreviewHeight, ZoomAnimationMs);

        if (_cardElements.TryGetValue(item, out var card))
        {
            AnimateCardTransform(card, scale: 1, offsetX: 0, ZoomAnimationMs);
        }

        StatusMessage = $"Peeking {item.ProcessName}";
        PositionShelfWindow();
        QueueThumbnailRefresh();
    }

    private void EnsurePeek(ShelvedWindow item)
    {
        if (_peekedItem == item)
        {
            return;
        }

        if (_peekedItem is not null)
        {
            CollapsePeekedCard(_peekedItem);
        }

        _peekedItem = item;
        item.IsExpanded = true;
        item.MarkAttentionSeen();

        if (_cardElements.TryGetValue(item, out var card))
        {
            Panel.SetZIndex(card, 10);
            AnimateCardTransform(card, scale: 1, offsetX: 0, PeekAnimationMs);
        }

        AnimatePreviewHeight(item, PeekPreviewHeight, PeekAnimationMs);
        PositionShelfWindow();
        QueueThumbnailRefresh();
    }

    private void RunAttentionAlert(ShelvedWindow item)
    {
        if (!_cardElements.TryGetValue(item, out var card) || card is not Border border || !ShouldShowShelf)
        {
            return;
        }

        var borderBrush = EnsureMutableBrush(border.BorderBrush, CardBorderColor);
        var backgroundBrush = EnsureMutableBrush(border.Background, CardBackgroundColor);
        border.BorderBrush = borderBrush;
        border.Background = backgroundBrush;

        AnimateColor(borderBrush, SolidColorBrush.ColorProperty, AttentionBorderColor, AttentionAnimationMs / 2, () =>
        {
            AnimateColor(borderBrush, SolidColorBrush.ColorProperty, CardBorderColor, AttentionAnimationMs);
        });

        AnimateColor(backgroundBrush, SolidColorBrush.ColorProperty, AttentionBackgroundColor, AttentionAnimationMs / 2, () =>
        {
            AnimateColor(backgroundBrush, SolidColorBrush.ColorProperty, CardBackgroundColor, AttentionAnimationMs);
        });

        if (item != _peekedItem)
        {
            AnimateCardTransform(card, scale: 1, offsetX: 0, AttentionAnimationMs / 2);
            RunAfter(AttentionAnimationMs / 2, () =>
            {
                if (item != _peekedItem && _cardElements.TryGetValue(item, out var currentCard))
                {
                    AnimateCardTransform(currentCard, scale: 1, offsetX: 0, AttentionAnimationMs);
                }
            });
        }
    }

    private void RunAfter(int delayMs, Action action)
    {
        var timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(delayMs)
        };

        timer.Tick += (_, _) =>
        {
            timer.Stop();
            action();
        };
        timer.Start();
    }

    private void AnimatePreviewHeight(ShelvedWindow item, double height, int durationMs)
    {
        if (!_previewHostElements.TryGetValue(item, out var previewHost))
        {
            return;
        }

        AnimateDouble(previewHost, FrameworkElement.HeightProperty, height, durationMs, () =>
        {
            UpdatePreviewFrame(item);
            QueueThumbnailRefresh();
        });
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

    private void RefreshThumbnailsAfterLayout(int durationMs)
    {
        Dispatcher.BeginInvoke(() =>
        {
            foreach (var item in Items)
            {
                UpdatePreviewFrame(item);
            }

            QueueThumbnailRefresh();
            if (durationMs > 0)
            {
                RefreshThumbnailsDuring(durationMs);
            }
        }, DispatcherPriority.Loaded);
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
            UpdatePreviewFrame(item);
            if (!TryGetPreviewBounds(item, out var thumbnailDestination))
            {
                continue;
            }

            _shelver.UpdateThumbnailDestination(item, thumbnailDestination);
        }
    }

    private bool UpdatePreviewFrame(ShelvedWindow item)
    {
        if (!_previewElements.TryGetValue(item, out var surface))
        {
            return false;
        }

        var changed = false;
        if (!double.IsNaN(surface.Width))
        {
            surface.Width = double.NaN;
            changed = true;
        }

        if (!double.IsNaN(surface.Height))
        {
            surface.Height = double.NaN;
            changed = true;
        }

        return changed;
    }

    private bool TryGetPreviewBounds(
        ShelvedWindow item,
        out NativeMethods.RECT thumbnailDestination)
    {
        thumbnailDestination = default;

        if (!_previewElements.TryGetValue(item, out var element) ||
            element.ActualWidth <= 0 ||
            element.ActualHeight <= 0)
        {
            return false;
        }

        Rect rootBounds;
        try
        {
            var previewRect = GetPreviewThumbnailRect(element);
            rootBounds = element.TransformToAncestor(RootSurface)
                .TransformBounds(previewRect);
        }
        catch (InvalidOperationException)
        {
            return false;
        }

        var source = PresentationSource.FromVisual(this);
        var toDevice = source?.CompositionTarget?.TransformToDevice ?? Matrix.Identity;

        var rootTopLeft = toDevice.Transform(rootBounds.TopLeft);
        var rootBottomRight = toDevice.Transform(rootBounds.BottomRight);
        thumbnailDestination = new NativeMethods.RECT(
            (int)Math.Round(rootTopLeft.X),
            (int)Math.Round(rootTopLeft.Y),
            (int)Math.Round(rootBottomRight.X),
            (int)Math.Round(rootBottomRight.Y));

        return true;
    }

    private static Rect GetPreviewThumbnailRect(FrameworkElement element)
    {
        var inset = Math.Min(
            ThumbnailFrameInsetDip,
            Math.Max(0, Math.Min(element.ActualWidth, element.ActualHeight) / 4));
        return new Rect(
            inset,
            inset,
            Math.Max(0, element.ActualWidth - (inset * 2)),
            Math.Max(0, element.ActualHeight - (inset * 2)));
    }

    private void PositionShelfWindow(bool animate = true)
    {
        if (_windowHandle == IntPtr.Zero)
        {
            return;
        }

        var shouldShowShelf = ShouldShowShelf || ShouldShowRail;
        RootSurface.IsHitTestVisible = shouldShowShelf;

        if (shouldShowShelf && !_isRailMode)
        {
            _shelver?.SetThumbnailsVisible(true);
        }
        else
        {
            _shelver?.SetThumbnailsVisible(false);
        }

        var targetWidth = GetTargetShelfWidth();
        var right = SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth;
        var targetLeft = shouldShowShelf ? right - targetWidth : right + HiddenOffset;
        var targetOpacity = shouldShowShelf ? 1 : 0;
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

            return;
        }

        var animationMs = shouldShowShelf ? ShelfAnimationMs : ShelfCloseAnimationMs;
        AnimateDouble(this, Window.LeftProperty, targetLeft, animationMs);
        AnimateDouble(this, FrameworkElement.WidthProperty, targetWidth, animationMs);
        AnimateDouble(this, UIElement.OpacityProperty, targetOpacity, animationMs, () =>
        {
            if (generation == _shelfAnimationGeneration && (!ShouldShowShelf || _isRailMode))
            {
                _shelver?.SetThumbnailsVisible(false);
            }
        });
        RefreshThumbnailsDuring(animationMs);
    }

    private bool ShouldShowShelf => Items.Count > 0 && !_isShelfHidden;

    private bool ShouldShowRail => Items.Count > 0 && _isRailMode && !_isShelfHidden;

    private double GetTargetShelfWidth()
    {
        if (_isRailMode)
        {
            return RailWidth;
        }

        if (_zoomedItem is not null && !_isShelfHidden)
        {
            return Math.Min(ZoomShelfWidth, Math.Max(ShelfWidth, SystemParameters.VirtualScreenWidth - 24));
        }

        if (_peekedItem is not null && !_isShelfHidden)
        {
            return PeekShelfWidth;
        }

        return ShelfWidth;
    }

    private static double GetZoomPreviewHeight()
    {
        return Math.Clamp(SystemParameters.VirtualScreenHeight - 170, 360, ZoomPreviewHeight);
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
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
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

    private static void AnimateColor(
        Animatable target,
        DependencyProperty property,
        Color to,
        int durationMs,
        Action? completed = null)
    {
        var animation = new ColorAnimation
        {
            To = to,
            Duration = TimeSpan.FromMilliseconds(durationMs),
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };

        if (completed is not null)
        {
            animation.Completed += (_, _) => completed();
        }

        target.BeginAnimation(property, animation, HandoffBehavior.SnapshotAndReplace);
    }

    private static SolidColorBrush EnsureMutableBrush(Brush brush, Color fallback)
    {
        if (brush is SolidColorBrush solid && !solid.IsFrozen)
        {
            return solid;
        }

        return new SolidColorBrush(fallback);
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

    public Visibility FullShelfVisibility => _isRailMode ? Visibility.Collapsed : Visibility.Visible;

    public Visibility RailVisibility => ShouldShowRail ? Visibility.Visible : Visibility.Collapsed;

    private void RailCollapseTimer_Tick(object? sender, EventArgs e)
    {
        _railCollapseTimer.Stop();
        if (_isShelfHidden || Items.Count == 0 || IsMouseOver)
        {
            return;
        }

        CollapseToRail();
    }

    private void CollapseToRail()
    {
        if (_isRailMode)
        {
            return;
        }

        ClearPeek();
        _isRailMode = true;
        OnPropertyChanged(nameof(FullShelfVisibility));
        OnPropertyChanged(nameof(RailVisibility));
        _shelver?.SetThumbnailsVisible(false);
        AnimateDouble(this, FrameworkElement.WidthProperty, RailWidth, RailCollapseAnimationMs);
        var right = SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth;
        AnimateDouble(this, Window.LeftProperty, right - RailWidth, RailCollapseAnimationMs);
    }

    private void ExpandFromRail()
    {
        if (!_isRailMode)
        {
            return;
        }

        _railCollapseTimer.Stop();
        _isRailMode = false;
        OnPropertyChanged(nameof(FullShelfVisibility));
        OnPropertyChanged(nameof(RailVisibility));
        _shelver?.SetThumbnailsVisible(true);
        var targetWidth = GetTargetShelfWidth();
        var right = SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth;
        AnimateDouble(this, FrameworkElement.WidthProperty, targetWidth, RailExpandAnimationMs);
        AnimateDouble(this, Window.LeftProperty, right - targetWidth, RailExpandAnimationMs);
        RefreshThumbnailsDuring(RailExpandAnimationMs);
    }

    private void RailSurface_MouseEnter(object sender, MouseEventArgs e)
    {
        _railCollapseTimer.Stop();
        ExpandFromRail();
    }

    private void RailIcon_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { Tag: ShelvedWindow item })
        {
            _shelver?.Restore(item);
        }
    }

    private void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

internal readonly record struct DragCardLayout(double Top, double Height, double Extent);
