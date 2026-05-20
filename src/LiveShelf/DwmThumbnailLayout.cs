namespace LiveShelf;

internal static class DwmThumbnailLayout
{
    internal readonly record struct Placement(
        NativeMethods.RECT Destination,
        NativeMethods.RECT Source);

    internal static NativeMethods.RECT ComputeContainDestination(
        NativeMethods.RECT hostBounds,
        NativeMethods.SIZE sourceSize)
    {
        return NativeMethods.FitInside(hostBounds, sourceSize);
    }

    internal static NativeMethods.RECT GetFullSourceRect(NativeMethods.SIZE sourceSize)
    {
        return new NativeMethods.RECT(0, 0, Math.Max(0, sourceSize.Width), Math.Max(0, sourceSize.Height));
    }

    internal static Placement? ComputeVisiblePlacement(
        NativeMethods.RECT hostBounds,
        NativeMethods.RECT visibleBounds,
        NativeMethods.SIZE sourceSize)
    {
        if (!HasUsableSourceSize(sourceSize))
        {
            return null;
        }

        var containedDestination = ComputeContainDestination(hostBounds, sourceSize);
        if (!TryIntersect(containedDestination, visibleBounds, out var visibleDestination))
        {
            return null;
        }

        var source = MapDestinationClipToSource(visibleDestination, containedDestination, sourceSize);
        if (source.Width <= 0 || source.Height <= 0)
        {
            return null;
        }

        return new Placement(visibleDestination, source);
    }

    internal static bool TryIntersect(
        NativeMethods.RECT first,
        NativeMethods.RECT second,
        out NativeMethods.RECT intersection)
    {
        var left = Math.Max(first.Left, second.Left);
        var top = Math.Max(first.Top, second.Top);
        var right = Math.Min(first.Right, second.Right);
        var bottom = Math.Min(first.Bottom, second.Bottom);

        if (right <= left || bottom <= top)
        {
            intersection = default;
            return false;
        }

        intersection = new NativeMethods.RECT(left, top, right, bottom);
        return true;
    }

    internal static bool HasUsableSourceSize(NativeMethods.SIZE sourceSize)
    {
        return sourceSize.Width > 0 && sourceSize.Height > 0;
    }

    internal static bool IsSeverelyWrongSourceSize(
        NativeMethods.SIZE sourceSize,
        NativeMethods.RECT originalSourceRect)
    {
        if (!HasUsableSourceSize(sourceSize) ||
            originalSourceRect.Width <= 0 ||
            originalSourceRect.Height <= 0)
        {
            return false;
        }

        var sourceArea = sourceSize.Width * (double)sourceSize.Height;
        var originalArea = originalSourceRect.Width * (double)originalSourceRect.Height;
        if (originalArea <= 0)
        {
            return false;
        }

        var widthRatio = sourceSize.Width / (double)originalSourceRect.Width;
        var heightRatio = sourceSize.Height / (double)originalSourceRect.Height;
        var areaRatio = sourceArea / originalArea;

        return widthRatio < 0.40 || heightRatio < 0.40 || areaRatio < 0.20;
    }

    private static NativeMethods.RECT MapDestinationClipToSource(
        NativeMethods.RECT visibleDestination,
        NativeMethods.RECT fullDestination,
        NativeMethods.SIZE sourceSize)
    {
        if (fullDestination.Width <= 0 || fullDestination.Height <= 0)
        {
            return GetFullSourceRect(sourceSize);
        }

        var leftRatio = (visibleDestination.Left - fullDestination.Left) / (double)fullDestination.Width;
        var topRatio = (visibleDestination.Top - fullDestination.Top) / (double)fullDestination.Height;
        var rightRatio = (visibleDestination.Right - fullDestination.Left) / (double)fullDestination.Width;
        var bottomRatio = (visibleDestination.Bottom - fullDestination.Top) / (double)fullDestination.Height;

        var left = ClampToSource((int)Math.Floor(sourceSize.Width * leftRatio), sourceSize.Width);
        var top = ClampToSource((int)Math.Floor(sourceSize.Height * topRatio), sourceSize.Height);
        var right = ClampToSource((int)Math.Ceiling(sourceSize.Width * rightRatio), sourceSize.Width);
        var bottom = ClampToSource((int)Math.Ceiling(sourceSize.Height * bottomRatio), sourceSize.Height);

        if (right <= left)
        {
            right = Math.Min(sourceSize.Width, left + 1);
        }

        if (bottom <= top)
        {
            bottom = Math.Min(sourceSize.Height, top + 1);
        }

        return new NativeMethods.RECT(left, top, right, bottom);
    }

    private static int ClampToSource(int value, int sourceExtent)
    {
        return Math.Clamp(value, 0, Math.Max(0, sourceExtent));
    }
}
