namespace LiveShelf;

internal readonly record struct DisplayMonitor(
    IntPtr Handle,
    string DeviceName,
    NativeMethods.RECT Bounds,
    NativeMethods.RECT WorkArea,
    bool IsPrimary,
    uint DpiX,
    uint DpiY,
    string StableKey)
{
    internal string Key => StableKey;
}
