namespace LiveShelf;

internal static class MonitorGeometry
{
    internal static DisplayMonitor? FindBestMonitor(
        NativeMethods.RECT sourceRect,
        IReadOnlyList<DisplayMonitor> monitors)
    {
        if (monitors.Count == 0)
        {
            return null;
        }

        var best = monitors[0];
        var bestArea = -1L;
        foreach (var monitor in monitors)
        {
            var area = GetIntersectionArea(sourceRect, monitor.Bounds);
            if (area > bestArea ||
                area == bestArea && monitor.IsPrimary && !best.IsPrimary)
            {
                best = monitor;
                bestArea = area;
            }
        }

        return bestArea > 0
            ? best
            : monitors.FirstOrDefault(monitor => monitor.IsPrimary, monitors[0]);
    }

    internal static long GetIntersectionArea(
        NativeMethods.RECT first,
        NativeMethods.RECT second)
    {
        var left = Math.Max(first.Left, second.Left);
        var top = Math.Max(first.Top, second.Top);
        var right = Math.Min(first.Right, second.Right);
        var bottom = Math.Min(first.Bottom, second.Bottom);
        var width = Math.Max(0, right - left);
        var height = Math.Max(0, bottom - top);
        return width * (long)height;
    }

    internal static double GetHiddenShelfLeft(NativeMethods.RECT virtualScreen, double hiddenOffset)
    {
        return virtualScreen.Right + hiddenOffset;
    }
}
