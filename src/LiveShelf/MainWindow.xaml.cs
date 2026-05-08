using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
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
    private const int EmergencyRestoreHotkeyId = 0x5252;
    private const int HotkeyModifiers = NativeMethods.MOD_ALT | NativeMethods.MOD_CONTROL | NativeMethods.MOD_NOREPEAT;
    private const int EmergencyRestoreHotkeyModifiers = NativeMethods.MOD_ALT | NativeMethods.MOD_CONTROL | NativeMethods.MOD_SHIFT | NativeMethods.MOD_NOREPEAT;
    private const int ShelfHotkeyVirtualKey = 0x53; // S
    private const int ToggleShelfHotkeyVirtualKey = 0x48; // H
    private const int EmergencyRestoreHotkeyVirtualKey = 0x52; // R

    private const double ShelfWidth = 256;
    private const double PeekShelfWidth = 440;
    private const double ZoomShelfWidth = 780;
    private const double HiddenOffset = 18;
    private const double CollapsedPreviewHeight = 108;
    private const double PeekPreviewHeight = 248;
    private const double ZoomPreviewHeight = 560;
    private const int CardEntryAnimationMs = 340;
    private const int ShelfAnimationMs = 560;
    private const int PeekAnimationMs = 500;
    private const int ZoomAnimationMs = 520;
    private const int InteractiveActivationDelayMs = 500;
    private const int AttentionAnimationMs = 720;

    private static readonly Color CardBackgroundColor = Color.FromRgb(24, 29, 35);
    private static readonly Color CardBorderColor = Color.FromRgb(38, 46, 55);
    private static readonly Color AttentionBorderColor = Color.FromRgb(117, 196, 255);
    private static readonly Color AttentionBackgroundColor = Color.FromRgb(38, 49, 61);
    private static readonly Brush AgentIdleBrush = new SolidColorBrush(Color.FromRgb(145, 156, 172));
    private static readonly Brush AgentPendingBrush = new SolidColorBrush(Color.FromRgb(255, 196, 87));
    private static readonly Brush AgentConnectedBrush = new SolidColorBrush(Color.FromRgb(95, 220, 139));
    private static readonly Brush AgentFailedBrush = new SolidColorBrush(Color.FromRgb(238, 105, 117));

    private readonly ObservableCollection<ShelvedWindow> _items = [];
    private readonly Dictionary<ShelvedWindow, FrameworkElement> _cardElements = [];
    private readonly Dictionary<ShelvedWindow, FrameworkElement> _previewElements = [];
    private readonly DispatcherTimer _peekCollapseTimer;
    private readonly DispatcherTimer _interactiveExitTimer;
    private ShelvedWindow? _pendingInteractiveItem;
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
    private DateTime _interactiveExitSuppressedUntilUtc;
    private int _shelfAnimationGeneration;
    private int _interactiveActivationGeneration;
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

        _interactiveExitTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(220)
        };
        _interactiveExitTimer.Tick += InteractiveExitTimer_Tick;
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
        _shelver.AttentionRequested += (_, item) => Dispatcher.InvokeAsync(() => RunAttentionAlert(item));

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
        NativeMethods.UnregisterHotKey(_windowHandle, EmergencyRestoreHotkeyId);
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
            if (item.IsInteractive)
            {
                return;
            }

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

        if (wParam.ToInt32() == EmergencyRestoreHotkeyId)
        {
            handled = true;
            RestoreShelvedWindowsForShutdown();
            StatusMessage = "Restored shelved windows";
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

    private void Card_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (GetItemFromSender(sender) is { } item)
        {
            if (item.IsInteractive)
            {
                return;
            }

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

        if (item.IsInteractive)
        {
            ForwardWheelToSource(item, e);
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

    private void PreviewSurface_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element && element.Tag is ShelvedWindow item)
        {
            _previewElements[item] = element;
            element.Height = item.IsZoomed
                ? GetZoomPreviewHeight()
                : item.IsExpanded
                    ? PeekPreviewHeight
                    : CollapsedPreviewHeight;
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

    private void PreviewSurface_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement preview || preview.Tag is not ShelvedWindow item || !item.IsInteractive)
        {
            return;
        }

        preview.Focus();
        preview.CaptureMouse();
        ForwardMouseToSource(item, preview, NativeMethods.WM_LBUTTONDOWN, NativeMethods.MK_LBUTTON, e.GetPosition(preview));
        e.Handled = true;
    }

    private void PreviewSurface_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement preview || preview.Tag is not ShelvedWindow item || !item.IsInteractive)
        {
            return;
        }

        ForwardMouseToSource(item, preview, NativeMethods.WM_LBUTTONUP, 0, e.GetPosition(preview));
        preview.ReleaseMouseCapture();
        e.Handled = true;
    }

    private void PreviewSurface_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement preview || preview.Tag is not ShelvedWindow item || !item.IsInteractive)
        {
            return;
        }

        preview.Focus();
        ForwardMouseToSource(item, preview, NativeMethods.WM_RBUTTONDOWN, NativeMethods.MK_RBUTTON, e.GetPosition(preview));
        e.Handled = true;
    }

    private void PreviewSurface_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement preview || preview.Tag is not ShelvedWindow item || !item.IsInteractive)
        {
            return;
        }

        ForwardMouseToSource(item, preview, NativeMethods.WM_RBUTTONUP, 0, e.GetPosition(preview));
        e.Handled = true;
    }

    private void PreviewSurface_MouseMove(object sender, MouseEventArgs e)
    {
        if (sender is not FrameworkElement preview || preview.Tag is not ShelvedWindow item || !item.IsInteractive)
        {
            return;
        }

        var keyState = 0;
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            keyState |= NativeMethods.MK_LBUTTON;
        }

        if (e.RightButton == MouseButtonState.Pressed)
        {
            keyState |= NativeMethods.MK_RBUTTON;
        }

        ForwardMouseToSource(item, preview, NativeMethods.WM_MOUSEMOVE, keyState, e.GetPosition(preview));
    }

    private void PreviewSurface_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not FrameworkElement preview || preview.Tag is not ShelvedWindow item)
        {
            return;
        }

        if (item.IsInteractive)
        {
            ForwardWheelToSource(item, e, preview);
            return;
        }

        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0 && item == _peekedItem)
        {
            e.Handled = true;
            if (e.Delta > 0)
            {
                ActivateZoom(item);
            }

            return;
        }
    }

    private void PreviewSurface_KeyDown(object sender, KeyEventArgs e)
    {
        if (sender is FrameworkElement { Tag: ShelvedWindow item } && item.IsInteractive)
        {
            _shelver?.ForwardKeyInput(item, NativeMethods.WM_KEYDOWN, KeyInterop.VirtualKeyFromKey(e.Key == Key.System ? e.SystemKey : e.Key));
            e.Handled = true;
        }
    }

    private void PreviewSurface_KeyUp(object sender, KeyEventArgs e)
    {
        if (sender is FrameworkElement { Tag: ShelvedWindow item } && item.IsInteractive)
        {
            _shelver?.ForwardKeyInput(item, NativeMethods.WM_KEYUP, KeyInterop.VirtualKeyFromKey(e.Key == Key.System ? e.SystemKey : e.Key));
            e.Handled = true;
        }
    }

    private void PreviewSurface_TextInput(object sender, TextCompositionEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ShelvedWindow item } || !item.IsInteractive)
        {
            return;
        }

        foreach (var character in e.Text)
        {
            _shelver?.ForwardCharInput(item, character);
        }

        e.Handled = true;
    }

    private void PeekCollapseTimer_Tick(object? sender, EventArgs e)
    {
        _peekCollapseTimer.Stop();
        ClearPeek();
    }

    private void InteractiveExitTimer_Tick(object? sender, EventArgs e)
    {
        if (_zoomedItem is not { IsInteractive: true } item)
        {
            _interactiveExitTimer.Stop();
            return;
        }

        var bounds = GetPreviewScreenBounds(item);
        if (bounds.Width > 0 && bounds.Height > 0)
        {
            _shelver?.UpdateInteractiveZoomBounds(item, bounds);
        }

        if (DateTime.UtcNow < _interactiveExitSuppressedUntilUtc)
        {
            return;
        }

        if (IsMouseOver || IsCursorInsidePreviewBounds(item))
        {
            return;
        }

        DeactivateZoom(item);
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
            CancelPendingInteractiveActivation();
            _shelver?.EndInteractiveZoom(item);
            _zoomedItem = null;
            item.IsZoomed = false;
            _interactiveExitTimer.Stop();
        }
    }

    private void CollapsePeekedCard(ShelvedWindow item)
    {
        if (_zoomedItem == item)
        {
            CancelPendingInteractiveActivation();
            _shelver?.EndInteractiveZoom(item);
            _zoomedItem = null;
            _interactiveExitTimer.Stop();
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
        if (_isShelfHidden || item != _peekedItem || !item.IsSourceAlive)
        {
            return;
        }

        if (_zoomedItem == item)
        {
            if (!item.IsInteractive)
            {
                ScheduleInteractiveActivation(item);
            }

            return;
        }

        _zoomedItem = item;
        item.IsZoomed = true;
        item.MarkAttentionSeen();
        ScheduleInteractiveActivation(item);
        FocusPreview(item);
        AnimatePreviewHeight(item, GetZoomPreviewHeight(), ZoomAnimationMs);
        if (_cardElements.TryGetValue(item, out var card))
        {
            AnimateCardTransform(card, scale: 1, offsetX: 0, ZoomAnimationMs);
        }

        StatusMessage = $"Zooming {item.ProcessName}";
        PositionShelfWindow();
        QueueThumbnailRefresh();
    }

    private void DeactivateZoom(ShelvedWindow item)
    {
        if (_zoomedItem != item)
        {
            return;
        }

        CancelPendingInteractiveActivation();
        _shelver?.EndInteractiveZoom(item);
        _zoomedItem = null;
        item.IsZoomed = false;
        _interactiveExitTimer.Stop();
        AnimatePreviewHeight(item, PeekPreviewHeight, ZoomAnimationMs);

        if (_cardElements.TryGetValue(item, out var card))
        {
            AnimateCardTransform(card, scale: 1, offsetX: 0, ZoomAnimationMs);
        }

        StatusMessage = $"Peeking {item.ProcessName}";
        PositionShelfWindow();
        QueueThumbnailRefresh();
    }

    private void ScheduleInteractiveActivation(ShelvedWindow item)
    {
        var generation = ++_interactiveActivationGeneration;
        _pendingInteractiveItem = item;
        _interactiveExitSuppressedUntilUtc = DateTime.UtcNow.AddMilliseconds(InteractiveActivationDelayMs + 1200);
        StatusMessage = $"Zooming {item.ProcessName}";
        RefreshThumbnailsDuring(InteractiveActivationDelayMs + 80);

        RunAfter(InteractiveActivationDelayMs, () =>
        {
            if (generation != _interactiveActivationGeneration ||
                _zoomedItem != item ||
                !item.IsZoomed ||
                item.IsInteractive ||
                !item.IsSourceAlive)
            {
                return;
            }

            _shelver?.BeginInteractiveZoom(item, GetPreviewScreenBounds(item));
            _pendingInteractiveItem = null;
            _interactiveExitTimer.Start();
            StatusMessage = $"Using {item.ProcessName}";
            QueueThumbnailRefresh();
        });
    }

    private void CancelPendingInteractiveActivation()
    {
        _interactiveActivationGeneration++;
        if (_pendingInteractiveItem is { } item)
        {
            _shelver?.CancelInteractivePreparation(item);
        }

        _pendingInteractiveItem = null;
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
            if (!TryGetPreviewBounds(item, out var thumbnailDestination, out var screenBounds))
            {
                continue;
            }

            _shelver.UpdateThumbnailDestination(item, thumbnailDestination);
            if (item.IsInteractive)
            {
                _shelver.UpdateInteractiveZoomBounds(item, screenBounds);
            }
        }
    }

    private NativeMethods.RECT GetPreviewScreenBounds(ShelvedWindow item)
    {
        return TryGetPreviewBounds(item, out _, out var screenBounds)
            ? screenBounds
            : default;
    }

    private bool IsCursorInsidePreviewBounds(ShelvedWindow item)
    {
        if (!NativeMethods.GetCursorPos(out var point))
        {
            return false;
        }

        var bounds = GetPreviewScreenBounds(item);
        return bounds.Width > 0 &&
               bounds.Height > 0 &&
               point.X >= bounds.Left &&
               point.X <= bounds.Right &&
               point.Y >= bounds.Top &&
               point.Y <= bounds.Bottom;
    }

    private bool TryGetPreviewBounds(
        ShelvedWindow item,
        out NativeMethods.RECT thumbnailDestination,
        out NativeMethods.RECT screenBounds)
    {
        thumbnailDestination = default;
        screenBounds = default;

        if (!_previewElements.TryGetValue(item, out var element) ||
            element.ActualWidth <= 0 ||
            element.ActualHeight <= 0)
        {
            return false;
        }

        Rect rootBounds;
        try
        {
            rootBounds = element.TransformToAncestor(RootSurface)
                .TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
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

        var screenTopLeft = element.PointToScreen(new Point(0, 0));
        var screenBottomRight = element.PointToScreen(new Point(element.ActualWidth, element.ActualHeight));
        screenBounds = new NativeMethods.RECT(
            (int)Math.Round(screenTopLeft.X),
            (int)Math.Round(screenTopLeft.Y),
            (int)Math.Round(screenBottomRight.X),
            (int)Math.Round(screenBottomRight.Y));

        return true;
    }

    private void ForwardMouseToSource(
        ShelvedWindow item,
        FrameworkElement preview,
        int message,
        int keyState,
        Point position)
    {
        _shelver?.ForwardMouseInput(
            item,
            message,
            keyState | GetModifierKeyState(),
            position.X,
            position.Y,
            preview.ActualWidth,
            preview.ActualHeight);
    }

    private void ForwardWheelToSource(ShelvedWindow item, MouseWheelEventArgs e, FrameworkElement? preview = null)
    {
        preview ??= _previewElements.GetValueOrDefault(item);
        if (preview is null)
        {
            return;
        }

        var position = e.GetPosition(preview);
        var x = Math.Clamp(position.X, 0, Math.Max(0, preview.ActualWidth - 1));
        var y = Math.Clamp(position.Y, 0, Math.Max(0, preview.ActualHeight - 1));
        var screenPoint = preview.PointToScreen(new Point(x, y));

        _shelver?.ForwardMouseInput(
            item,
            NativeMethods.WM_MOUSEWHEEL,
            GetModifierKeyState(),
            x,
            y,
            preview.ActualWidth,
            preview.ActualHeight,
            e.Delta,
            screenPoint.X,
            screenPoint.Y);
        e.Handled = true;
    }

    private void FocusPreview(ShelvedWindow item)
    {
        if (!_previewElements.TryGetValue(item, out var preview))
        {
            return;
        }

        Dispatcher.BeginInvoke(() =>
        {
            preview.Focus();
            Keyboard.Focus(preview);
        }, DispatcherPriority.Input);
    }

    private static int GetModifierKeyState()
    {
        var state = 0;
        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
        {
            state |= NativeMethods.MK_SHIFT;
        }

        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            state |= NativeMethods.MK_CONTROL;
        }

        return state;
    }

    private void PositionShelfWindow(bool animate = true)
    {
        if (_windowHandle == IntPtr.Zero)
        {
            return;
        }

        var shouldShowShelf = ShouldShowShelf;
        RootSurface.IsHitTestVisible = shouldShowShelf;

        if (shouldShowShelf)
        {
            _shelver?.SetThumbnailsVisible(true);
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

            if (!shouldShowShelf)
            {
                _shelver?.SetThumbnailsVisible(false);
            }

            return;
        }

        AnimateDouble(this, Window.LeftProperty, targetLeft, ShelfAnimationMs);
        AnimateDouble(this, FrameworkElement.WidthProperty, targetWidth, ShelfAnimationMs);
        AnimateDouble(this, UIElement.OpacityProperty, targetOpacity, ShelfAnimationMs, () =>
        {
            if (generation == _shelfAnimationGeneration && !ShouldShowShelf)
            {
                _shelver?.SetThumbnailsVisible(false);
            }
        });
        RefreshThumbnailsDuring(ShelfAnimationMs);
    }

    private bool ShouldShowShelf => Items.Count > 0 && !_isShelfHidden;

    private double GetTargetShelfWidth()
    {
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

    private void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
