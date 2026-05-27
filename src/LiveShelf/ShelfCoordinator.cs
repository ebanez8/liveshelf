using System.Collections.ObjectModel;
using System.Windows;
using Microsoft.Win32;

namespace LiveShelf;

internal sealed class MultiMonitorShelfManager : IDisposable
{
    private readonly ObservableCollection<ShelvedWindow> _allItems = [];
    private MainWindow? _window;
    private bool _isDisposed;

    internal WindowShelver? Shelver { get; private set; }

    internal void Start()
    {
        SystemEvents.DisplaySettingsChanged += SystemEvents_DisplaySettingsChanged;
        EnsureWindow();
        EnsureShelver();
    }

    internal void RegisterShelfWindow(MainWindow window)
    {
        if (Shelver is not null)
        {
            window.SetShelver(Shelver);
        }
    }

    internal void RestoreAll()
    {
        Shelver?.RestoreAll();
    }

    internal void RefreshMonitors()
    {
        RealignActiveMonitor();
    }

    internal void ShowAllShelvesForDebug()
    {
        RealignActiveMonitor();
        _window?.ShowForDebug();
    }

    internal void RevealShelvedItem(ShelvedWindow item)
    {
        if (_window is null || !_window.Items.Contains(item))
        {
            return;
        }

        _window.RevealAfterManualShelve(item);
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        SystemEvents.DisplaySettingsChanged -= SystemEvents_DisplaySettingsChanged;
        Shelver?.RestoreAll();
    }

    private void SystemEvents_DisplaySettingsChanged(object? sender, EventArgs e)
    {
        RealignActiveMonitor();
    }

    private void EnsureWindow()
    {
        if (_window is not null)
        {
            return;
        }

        var monitors = MonitorService.GetMonitors();
        var monitor = monitors.FirstOrDefault(m => m.IsPrimary, monitors[0]);
        _window = new MainWindow(monitor, this, isHotkeyHost: true);
        Application.Current.MainWindow = _window;
        _window.Show();
    }

    private void EnsureShelver()
    {
        if (Shelver is not null || _window is null)
        {
            return;
        }

        Shelver = new WindowShelver(
            _allItems,
            ResolveShelfTarget,
            PrepareShelfTarget,
            AddItemToShelf,
            RemoveItemFromShelf,
            IsShelfWindow);

        _window.SetShelver(Shelver);
    }

    private void RealignActiveMonitor()
    {
        if (_window is null)
        {
            return;
        }

        var monitors = MonitorService.GetMonitors();
        if (monitors.Count == 0)
        {
            return;
        }

        var current = _window.Monitor;
        var match = monitors.FirstOrDefault(m =>
            string.Equals(m.Key, current.Key, StringComparison.Ordinal));
        if (match.Key is null)
        {
            match = monitors.FirstOrDefault(m => m.IsPrimary, monitors[0]);
        }

        _window.SetMonitor(match);
    }

    private ShelfTarget? ResolveShelfTarget(IntPtr sourceHwnd, NativeMethods.RECT sourceRect)
    {
        if (_window is null)
        {
            return null;
        }

        var monitors = MonitorService.GetMonitors();
        if (monitors.Count == 0)
        {
            return null;
        }

        var monitor = MonitorService.FromWindow(sourceHwnd, monitors)
            ?? MonitorGeometry.FindBestMonitor(sourceRect, monitors);
        if (monitor is null)
        {
            return null;
        }

        _window.AssignMonitorForShelving(monitor.Value);
        return new ShelfTarget(monitor.Value, _window.WindowHandle, _window, _window.Items);
    }

    private void AddItemToShelf(ShelvedWindow item, ShelfTarget target)
    {
        if (!target.Items.Contains(item))
        {
            target.Items.Add(item);
        }

        target.Window.EnsureVisibleForItems();
    }

    private void PrepareShelfTarget(ShelfTarget target)
    {
        target.Window.ForceVisibleForShelving(out _, out _);
    }

    private void RemoveItemFromShelf(ShelvedWindow item)
    {
        if (_window is null)
        {
            return;
        }

        _window.Items.Remove(item);
        _window.ParkWhenEmpty();
    }

    private bool IsShelfWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || _window is null)
        {
            return false;
        }

        return hwnd == _window.WindowHandle ||
               NativeMethods.GetAncestor(hwnd, NativeMethods.GA_ROOT) == _window.WindowHandle;
    }
}
