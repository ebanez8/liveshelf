namespace LiveShelf;

internal static class DwmThumbnailLayout
{
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
}
