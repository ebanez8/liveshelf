namespace LiveShelf;

internal static class MonitorService
{
    internal static IReadOnlyList<DisplayMonitor> GetMonitors()
    {
        return NativeMethods.GetDisplayMonitors();
    }

    internal static DisplayMonitor? FromWindow(
        IntPtr hwnd,
        IReadOnlyList<DisplayMonitor> monitors)
    {
        var handle = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
        if (handle != IntPtr.Zero)
        {
            var match = monitors.FirstOrDefault(monitor => monitor.Handle == handle);
            if (match.Handle != IntPtr.Zero)
            {
                return match;
            }
        }

        return monitors.FirstOrDefault(monitor => monitor.IsPrimary, monitors.FirstOrDefault());
    }

    internal static NativeMethods.RECT FitRectIntoWorkArea(
        NativeMethods.RECT rect,
        NativeMethods.RECT workArea)
    {
        var width = Math.Min(Math.Max(1, rect.Width), Math.Max(1, workArea.Width));
        var height = Math.Min(Math.Max(1, rect.Height), Math.Max(1, workArea.Height));
        var left = Math.Clamp(rect.Left, workArea.Left, Math.Max(workArea.Left, workArea.Right - width));
        var top = Math.Clamp(rect.Top, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - height));
        return new NativeMethods.RECT(left, top, left + width, top + height);
    }
}
