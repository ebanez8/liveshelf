using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace LiveShelf;

internal static class NativeMethods
{
    internal const int WM_HOTKEY = 0x0312;
    internal const int WM_NCHITTEST = 0x0084;
    internal const int WM_CLOSE = 0x0010;
    internal const int WM_KEYDOWN = 0x0100;
    internal const int WM_KEYUP = 0x0101;
    internal const int WM_CHAR = 0x0102;
    internal const int WM_MOUSEMOVE = 0x0200;
    internal const int WM_LBUTTONDOWN = 0x0201;
    internal const int WM_LBUTTONUP = 0x0202;
    internal const int WM_RBUTTONDOWN = 0x0204;
    internal const int WM_RBUTTONUP = 0x0205;
    internal const int WM_MOUSEWHEEL = 0x020A;

    internal const int VK_LBUTTON = 0x01;

    internal const int MOD_SHIFT = 0x0004;

    internal const int MOD_ALT = 0x0001;
    internal const int MOD_CONTROL = 0x0002;
    internal const int MOD_NOREPEAT = 0x4000;

    internal const int MK_LBUTTON = 0x0001;
    internal const int MK_RBUTTON = 0x0002;
    internal const int MK_SHIFT = 0x0004;
    internal const int MK_CONTROL = 0x0008;

    internal const int HTCLIENT = 1;
    internal const int HTTRANSPARENT = -1;

    internal const int SW_SHOWNORMAL = 1;
    internal const int SW_SHOWMINIMIZED = 2;
    internal const int SW_SHOWMAXIMIZED = 3;
    internal const int SW_SHOWNOACTIVATE = 4;
    internal const int SW_SHOW = 5;
    internal const int SW_MINIMIZE = 6;
    internal const int SW_SHOWMINNOACTIVE = 7;
    internal const int SW_SHOWNA = 8;
    internal const int SW_RESTORE = 9;

    internal const int GWL_EXSTYLE = -20;
    internal const int WS_EX_APPWINDOW = 0x00040000;
    internal const int WS_EX_TOOLWINDOW = 0x00000080;
    internal const int WS_EX_NOACTIVATE = 0x08000000;

    internal const int GW_OWNER = 4;
    internal const int GA_ROOT = 2;

    internal const int SM_XVIRTUALSCREEN = 76;
    internal const int SM_YVIRTUALSCREEN = 77;
    internal const int SM_CXVIRTUALSCREEN = 78;
    internal const int SM_CYVIRTUALSCREEN = 79;

    internal const int SWP_NOSIZE = 0x0001;
    internal const int SWP_NOMOVE = 0x0002;
    internal const int SWP_NOACTIVATE = 0x0010;
    internal const int SWP_SHOWWINDOW = 0x0040;
    internal const int SWP_NOOWNERZORDER = 0x0200;

    internal const uint CWP_SKIPINVISIBLE = 0x0001;
    internal const uint CWP_SKIPDISABLED = 0x0002;

    internal const int DWM_TNP_RECTDESTINATION = 0x00000001;
    internal const int DWM_TNP_RECTSOURCE = 0x00000002;
    internal const int DWM_TNP_OPACITY = 0x00000004;
    internal const int DWM_TNP_VISIBLE = 0x00000008;
    internal const int DWM_TNP_SOURCECLIENTAREAONLY = 0x00000010;

    internal static readonly IntPtr HWND_TOPMOST = new(-1);
    internal static readonly IntPtr HWND_NOTOPMOST = new(-2);
    internal static readonly IntPtr HWND_BOTTOM = new(1);

    private static readonly HashSet<string> BlockedWindowClasses = new(StringComparer.Ordinal)
    {
        "Progman",
        "WorkerW",
        "Shell_TrayWnd",
        "DV2ControlHost",
        "Windows.UI.Core.CoreWindow"
    };

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool RegisterHotKey(IntPtr hWnd, int id, int fsModifiers, int vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    internal static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int x,
        int y,
        int cx,
        int cy,
        int uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT lpwndpl);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT lpwndpl);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    internal static extern IntPtr SetFocus(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    internal static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    internal static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    internal static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", EntryPoint = "GetWindowTextLengthW", SetLastError = true)]
    internal static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", EntryPoint = "GetWindowTextW", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", EntryPoint = "GetClassNameW", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    internal static extern IntPtr ChildWindowFromPointEx(IntPtr hwndParent, POINT pt, uint flags);

    [DllImport("user32.dll")]
    internal static extern int MapWindowPoints(IntPtr hWndFrom, IntPtr hWndTo, ref POINT lpPoints, uint cPoints);

    [DllImport("dwmapi.dll", SetLastError = true)]
    internal static extern int DwmRegisterThumbnail(IntPtr hwndDestination, IntPtr hwndSource, out IntPtr phThumbnailId);

    [DllImport("dwmapi.dll", SetLastError = true)]
    internal static extern int DwmUnregisterThumbnail(IntPtr hThumbnailId);

    [DllImport("dwmapi.dll", SetLastError = true)]
    internal static extern int DwmUpdateThumbnailProperties(IntPtr hThumbnailId, ref DWM_THUMBNAIL_PROPERTIES ptnProperties);

    [DllImport("dwmapi.dll", SetLastError = true)]
    internal static extern int DwmQueryThumbnailSourceSize(IntPtr hThumbnail, out SIZE pSize);

    [DllImport("dwmapi.dll")]
    internal static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

    [DllImport("dwmapi.dll")]
    internal static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS pMarInset);

    [DllImport("kernel32.dll")]
    internal static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    internal static string GetWindowTitle(IntPtr hwnd)
    {
        var length = GetWindowTextLength(hwnd);
        if (length <= 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder(length + 1);
        GetWindowText(hwnd, builder, builder.Capacity);
        return builder.ToString();
    }

    internal static string GetWindowClassName(IntPtr hwnd)
    {
        var builder = new StringBuilder(256);
        GetClassName(hwnd, builder, builder.Capacity);
        return builder.ToString();
    }

    internal static string GetProcessName(IntPtr hwnd)
    {
        try
        {
            GetWindowThreadProcessId(hwnd, out var processId);
            return processId == 0 ? "Unknown" : Process.GetProcessById((int)processId).ProcessName;
        }
        catch
        {
            return "Unknown";
        }
    }

    internal static string GetProcessExePath(int processId)
    {
        if (processId <= 0)
        {
            return string.Empty;
        }

        try
        {
            using var process = Process.GetProcessById(processId);
            return process.MainModule?.FileName ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    internal static int GetProcessId(IntPtr hwnd)
    {
        GetWindowThreadProcessId(hwnd, out var processId);
        return (int)processId;
    }

    internal static bool IsNormalAppWindow(IntPtr hwnd, IntPtr shelfHwnd, out string reason)
    {
        if (hwnd == IntPtr.Zero)
        {
            reason = "No active window";
            return false;
        }

        if (hwnd == shelfHwnd || GetAncestor(hwnd, GA_ROOT) == shelfHwnd)
        {
            reason = "Live Shelf cannot shelf itself";
            return false;
        }

        if (!IsWindow(hwnd) || !IsWindowVisible(hwnd))
        {
            reason = "Active window is not visible";
            return false;
        }

        if (GetAncestor(hwnd, GA_ROOT) != hwnd)
        {
            reason = "Only top-level windows can be shelved";
            return false;
        }

        var className = GetWindowClassName(hwnd);
        if (BlockedWindowClasses.Contains(className))
        {
            reason = "This Windows shell surface cannot be shelved";
            return false;
        }

        var exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
        if ((exStyle & WS_EX_TOOLWINDOW) != 0 && (exStyle & WS_EX_APPWINDOW) == 0)
        {
            reason = "Tool windows are not supported";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    internal static void MarkAsToolWindow(IntPtr hwnd)
    {
        var exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
        exStyle &= ~WS_EX_APPWINDOW;
        exStyle |= WS_EX_TOOLWINDOW;
        SetWindowLong(hwnd, GWL_EXSTYLE, exStyle);
    }

    internal static RECT GetVirtualScreenRect()
    {
        var left = GetSystemMetrics(SM_XVIRTUALSCREEN);
        var top = GetSystemMetrics(SM_YVIRTUALSCREEN);
        var width = GetSystemMetrics(SM_CXVIRTUALSCREEN);
        var height = GetSystemMetrics(SM_CYVIRTUALSCREEN);
        return new RECT(left, top, left + width, top + height);
    }

    internal static RECT FitInside(RECT bounds, SIZE sourceSize)
    {
        if (sourceSize.Width <= 0 || sourceSize.Height <= 0 || bounds.Width <= 0 || bounds.Height <= 0)
        {
            return bounds;
        }

        var sourceRatio = sourceSize.Width / (double)sourceSize.Height;
        var boundsRatio = bounds.Width / (double)bounds.Height;

        if (sourceRatio > boundsRatio)
        {
            var height = (int)Math.Round(bounds.Width / sourceRatio);
            var top = bounds.Top + ((bounds.Height - height) / 2);
            return new RECT(bounds.Left, top, bounds.Right, top + height);
        }

        var width = (int)Math.Round(bounds.Height * sourceRatio);
        var left = bounds.Left + ((bounds.Width - width) / 2);
        return new RECT(left, bounds.Top, left + width, bounds.Bottom);
    }

    internal static bool Intersects(RECT first, RECT second)
    {
        return first.Left < second.Right &&
               first.Right > second.Left &&
               first.Top < second.Bottom &&
               first.Bottom > second.Top;
    }

    internal static void ThrowLastWin32Error(string operation)
    {
        throw new Win32InteropException($"{operation} failed: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");
    }

    internal static void ThrowForHResult(string operation, int hresult)
    {
        if (hresult >= 0)
        {
            return;
        }

        throw new Win32InteropException($"{operation} failed: 0x{hresult:X8}");
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SIZE
    {
        public int Width;
        public int Height;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public RECT(int left, int top, int right, int bottom)
        {
            Left = left;
            Top = top;
            Right = right;
            Bottom = bottom;
        }

        public int Width => Right - Left;

        public int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct WINDOWPLACEMENT
    {
        public int Length;
        public int Flags;
        public int ShowCmd;
        public POINT MinPosition;
        public POINT MaxPosition;
        public RECT NormalPosition;

        public static WINDOWPLACEMENT Create()
        {
            return new WINDOWPLACEMENT
            {
                Length = Marshal.SizeOf<WINDOWPLACEMENT>()
            };
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DWM_THUMBNAIL_PROPERTIES
    {
        public int dwFlags;
        public RECT rcDestination;
        public RECT rcSource;
        public byte opacity;
        [MarshalAs(UnmanagedType.Bool)]
        public bool fVisible;
        [MarshalAs(UnmanagedType.Bool)]
        public bool fSourceClientAreaOnly;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MARGINS
    {
        public int Left;
        public int Right;
        public int Top;
        public int Bottom;

        public MARGINS(int all)
        {
            Left = Right = Top = Bottom = all;
        }
    }

    internal static void EnableMicaBackdrop(IntPtr hwnd)
    {
        var darkMode = 1;
        DwmSetWindowAttribute(hwnd, 20, ref darkMode, sizeof(int));

        var backdropType = 4;
        DwmSetWindowAttribute(hwnd, 38, ref backdropType, sizeof(int));

        var margins = new MARGINS(-1);
        DwmExtendFrameIntoClientArea(hwnd, ref margins);
    }

    internal static void ForceSetForegroundWindow(IntPtr hwnd)
    {
        if (GetForegroundWindow() == hwnd)
        {
            return;
        }

        var currentThread = GetCurrentThreadId();
        var foregroundHwnd = GetForegroundWindow();
        var foregroundThread = foregroundHwnd != IntPtr.Zero
            ? GetWindowThreadProcessId(foregroundHwnd, out _)
            : 0u;

        var attached = foregroundThread != 0 &&
                       foregroundThread != currentThread &&
                       AttachThreadInput(currentThread, foregroundThread, true);

        SetForegroundWindow(hwnd);

        if (attached)
        {
            AttachThreadInput(currentThread, foregroundThread, false);
        }
    }
}
