namespace LiveShelf;

internal enum SourceWindowPolicy
{
    Normal,
    Browser,
    Media
}

internal static class SourceWindowPolicyRules
{
    internal static SourceWindowPolicy Classify(string processName, bool isMediaCard)
    {
        if (isMediaCard || IsKnownMediaProcess(processName))
        {
            return SourceWindowPolicy.Media;
        }

        return IsBrowserProcess(processName)
            ? SourceWindowPolicy.Browser
            : SourceWindowPolicy.Normal;
    }

    internal static bool RequiresSourceSizePreservation(SourceWindowPolicy policy)
    {
        return policy is SourceWindowPolicy.Browser or SourceWindowPolicy.Media;
    }

    internal static bool KeepsLiveThumbnailVisibleWhenInteractive(SourceWindowPolicy policy)
    {
        return RequiresSourceSizePreservation(policy);
    }

    internal static int GetLivePreviewParkFlags()
    {
        return NativeMethods.SWP_NOACTIVATE |
               NativeMethods.SWP_NOOWNERZORDER |
               NativeMethods.SWP_SHOWWINDOW |
               NativeMethods.SWP_NOMOVE |
               NativeMethods.SWP_NOSIZE;
    }

    internal static int GetLivePreviewMoveToOriginalFlags()
    {
        return GetLivePreviewParkFlags() & ~NativeMethods.SWP_NOMOVE;
    }

    internal static int GetNormalParkFlags()
    {
        return NativeMethods.SWP_NOACTIVATE |
               NativeMethods.SWP_NOOWNERZORDER |
               NativeMethods.SWP_SHOWWINDOW;
    }

    internal static int GetLivePreviewInteractiveFlags()
    {
        return NativeMethods.SWP_NOACTIVATE |
               NativeMethods.SWP_NOOWNERZORDER |
               NativeMethods.SWP_SHOWWINDOW |
               NativeMethods.SWP_NOMOVE |
               NativeMethods.SWP_NOSIZE;
    }

    internal static bool IsBrowserProcess(string processName)
    {
        return processName.Contains("chrome", StringComparison.OrdinalIgnoreCase) ||
               processName.Contains("msedge", StringComparison.OrdinalIgnoreCase) ||
               processName.Contains("firefox", StringComparison.OrdinalIgnoreCase) ||
               processName.Contains("brave", StringComparison.OrdinalIgnoreCase) ||
               processName.Contains("opera", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsKnownMediaProcess(string processName)
    {
        return processName.Contains("vlc", StringComparison.OrdinalIgnoreCase) ||
               processName.Contains("mpv", StringComparison.OrdinalIgnoreCase) ||
               processName.Contains("wmplayer", StringComparison.OrdinalIgnoreCase) ||
               processName.Contains("potplayer", StringComparison.OrdinalIgnoreCase) ||
               processName.Contains("itunes", StringComparison.OrdinalIgnoreCase) ||
               processName.Contains("foobar", StringComparison.OrdinalIgnoreCase) ||
               processName.Contains("plex", StringComparison.OrdinalIgnoreCase);
    }
}
