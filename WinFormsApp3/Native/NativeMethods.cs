using System.Runtime.InteropServices;

namespace WinFormsApp3;

/// <summary>
/// The Win32 surface used by the harness: window creation, GDI double buffering, input
/// and clipboard helpers. Nothing here is needed by the UI library itself - a macOS or
/// Linux host would call its own native API instead.
/// </summary>
internal static class NativeMethods
{
    // Window styles and messages.
    internal const uint WsOverlappedWindow = 0x00CF0000;
    internal const uint CsHRedraw = 0x0002;
    internal const uint CsVRedraw = 0x0001;
    internal const uint CsDblClks = 0x0008;

    internal const uint WmDestroy = 0x0002;
    internal const uint WmSize = 0x0005;
    internal const uint WmSetFocus = 0x0007;
    internal const uint WmKillFocus = 0x0008;
    internal const uint WmPaint = 0x000F;
    internal const uint WmClose = 0x0010;
    internal const uint WmEraseBkgnd = 0x0014;
    internal const uint WmPrintClient = 0x0318;
    internal const uint WmGetMinMaxInfo = 0x0024;
    internal const uint WmTimer = 0x0113;
    internal const uint WmMouseMove = 0x0200;
    internal const uint WmLButtonDown = 0x0201;
    internal const uint WmLButtonUp = 0x0202;
    internal const uint WmLButtonDblClk = 0x0203;
    internal const uint WmRButtonDown = 0x0204;
    internal const uint WmRButtonUp = 0x0205;
    internal const uint WmRButtonDblClk = 0x0206;
    internal const uint WmMButtonDown = 0x0207;
    internal const uint WmMButtonUp = 0x0208;
    internal const uint WmMButtonDblClk = 0x0209;
    internal const uint WmKeyDown = 0x0100;
    internal const uint WmKeyUp = 0x0101;
    internal const uint WmChar = 0x0102;
    internal const uint WmSetCursor = 0x0020;
    internal const uint WmMouseLeave = 0x02A3;
    internal const uint WmDpiChanged = 0x02E0;
    internal const uint WmContextMenu = 0x007B;
    internal const uint WmNull = 0x0000;

    // Menus.
    internal const uint MfString = 0x00000000;
    internal const uint MfSeparator = 0x00000800;
    internal const uint MfGrayed = 0x00000001;
    internal const uint MfChecked = 0x00000008;
    internal const uint MfDefault = 0x00001000;
    internal const uint MfByPosition = 0x00000400;
    internal const uint MfByCommand = 0x00000000;
    internal const uint TpmLeftAlign = 0x0000;
    internal const uint TpmTopAlign = 0x0000;
    internal const uint TpmRightButton = 0x0002;
    internal const uint TpmReturnCmd = 0x0100;

    internal const int HtClient = 1;
    internal const int SwShow = 5;
    internal const uint SwpNoZOrder = 0x0004;
    internal const uint SwpNoActivate = 0x0010;

    // Painting.
    internal const uint SrcCopy = 0x00CC0020;
    internal const uint DibRgbColors = 0;
    internal const uint BiRgb = 0;

    // Mouse.
    internal const uint TmeLeave = 0x00000002;
    internal const int SmCxDoubleClk = 36;
    internal const int SmCyDoubleClk = 37;
    internal const int VkShift = 0x10;
    internal const int VkControl = 0x11;
    internal const int VkMenu = 0x12;

    // System cursors (MAKEINTRESOURCE ids).
    internal const int IdiApplication = 32512;
    internal const int IdcArrow = 32512;
    internal const int IdcIBeam = 32513;
    internal const int IdcCross = 32515;
    internal const int IdcSizeAll = 32646;
    internal const int IdcSizeNs = 32645;
    internal const int IdcSizeWe = 32644;
    internal const int IdcHand = 32649;

    // Clipboard.
    internal const uint CfUnicodeText = 13;
    internal const uint GmemMoveable = 0x0002;

    // DPI.
    internal const uint SpiGetWorkArea = 0x0030;
    internal const uint MonitorDefaultToNearest = 0x00000002;
    internal static readonly IntPtr DpiAwarenessPerMonitorV2 = new(-4);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public POINT pt;
        public uint lPrivate;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MONITORINFO
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential, Size = 32)]
    internal struct Reserved32
    {
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PAINTSTRUCT
    {
        public IntPtr hdc;
        public int fErase;
        public RECT rcPaint;
        public int fRestore;
        public int fIncUpdate;
        public Reserved32 rgbReserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct TRACKMOUSEEVENT
    {
        public uint cbSize;
        public uint dwFlags;
        public IntPtr hwndTrack;
        public uint dwHoverTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        public uint bmiColors;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct WNDCLASSEXW
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    // user32 - window management.
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern ushort RegisterClassExW(ref WNDCLASSEXW lpwcx);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool UnregisterClassW(string lpClassName, IntPtr hInstance);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr CreateWindowExW(
        uint dwExStyle,
        string lpClassName,
        string lpWindowName,
        uint dwStyle,
        int x,
        int y,
        int nWidth,
        int nHeight,
        IntPtr hWndParent,
        IntPtr hMenu,
        IntPtr hInstance,
        IntPtr lpParam);

    [DllImport("user32.dll")]
    internal static extern IntPtr DefWindowProcW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    internal static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    internal static extern bool ShowWindow(IntPtr hwnd, int nCmdShow);

    [DllImport("user32.dll")]
    internal static extern bool UpdateWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    internal static extern bool InvalidateRect(IntPtr hwnd, IntPtr lpRect, bool bErase);

    [DllImport("user32.dll")]
    internal static extern bool ValidateRect(IntPtr hwnd, IntPtr lpRect);

    [DllImport("user32.dll")]
    internal static extern bool GetClientRect(IntPtr hwnd, out RECT lpRect);

    [DllImport("user32.dll")]
    internal static extern bool SetWindowPos(IntPtr hwnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    internal static extern IntPtr SetCapture(IntPtr hwnd);

    [DllImport("user32.dll")]
    internal static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    internal static extern IntPtr SetCursor(IntPtr hCursor);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern IntPtr LoadCursorW(IntPtr hInstance, IntPtr lpCursorName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern IntPtr LoadIconW(IntPtr hInstance, IntPtr lpIconName);

    [DllImport("user32.dll")]
    internal static extern nuint SetTimer(IntPtr hwnd, nuint nIDEvent, uint uElapse, IntPtr lpTimerFunc);

    [DllImport("user32.dll")]
    internal static extern bool KillTimer(IntPtr hwnd, nuint uIDEvent);

    [DllImport("user32.dll")]
    internal static extern short GetKeyState(int nVirtKey);

    [DllImport("user32.dll")]
    internal static extern uint GetDoubleClickTime();

    [DllImport("user32.dll")]
    internal static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    internal static extern bool TrackMouseEvent(ref TRACKMOUSEEVENT lpEventTrack);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern bool SystemParametersInfoW(uint uiAction, uint uiParam, ref RECT pvParam, uint fWinIni);

    [DllImport("user32.dll")]
    internal static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    [DllImport("user32.dll")]
    internal static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    internal static extern bool AdjustWindowRectExForDpi(ref RECT lpRect, uint dwStyle, bool bMenu, uint dwExStyle, uint dpi);

    [DllImport("user32.dll")]
    internal static extern int GetMessageW(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    internal static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    internal static extern IntPtr DispatchMessageW(ref MSG lpMsg);

    [DllImport("user32.dll")]
    internal static extern void PostQuitMessage(int nExitCode);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern bool PostMessageW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    internal static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    internal static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    internal static extern bool ScreenToClient(IntPtr hwnd, ref POINT lpPoint);

    [DllImport("user32.dll")]
    internal static extern bool ClientToScreen(IntPtr hwnd, ref POINT lpPoint);

    // user32 - popup menus.
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool AppendMenuW(IntPtr hMenu, uint uFlags, IntPtr uIDNewItem, string? lpNewItem);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern int TrackPopupMenuEx(IntPtr hMenu, uint uFlags, int x, int y, IntPtr hwnd, IntPtr lptpm);

    [DllImport("user32.dll")]
    internal static extern int GetMenuItemCount(IntPtr hMenu);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetMenuStringW(IntPtr hMenu, uint uIDItem, [Out] char[]? lpString, int cchMax, uint flags);

    [DllImport("user32.dll")]
    internal static extern uint GetMenuState(IntPtr hMenu, uint uId, uint uFlags);

    // user32 - painting and GDI.
    [DllImport("user32.dll")]
    internal static extern IntPtr BeginPaint(IntPtr hwnd, out PAINTSTRUCT lpPaint);

    [DllImport("user32.dll")]
    internal static extern bool EndPaint(IntPtr hwnd, ref PAINTSTRUCT lpPaint);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    internal static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

    [DllImport("gdi32.dll")]
    internal static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    internal static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    internal static extern IntPtr SelectObject(IntPtr hdc, IntPtr hObject);

    [DllImport("gdi32.dll")]
    internal static extern bool DeleteObject(IntPtr hObject);

    [DllImport("gdi32.dll")]
    internal static extern IntPtr CreateDIBSection(
        IntPtr hdc,
        ref BITMAPINFO pbmi,
        uint usage,
        out IntPtr ppvBits,
        IntPtr hSection,
        uint offset);

    [DllImport("gdi32.dll")]
    internal static extern bool BitBlt(IntPtr hdc, int x, int y, int cx, int cy, IntPtr hdcSrc, int x1, int y1, uint rop);

    // kernel32.
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    internal static extern IntPtr GetModuleHandleW(string? lpModuleName);

    [DllImport("kernel32.dll")]
    internal static extern bool AttachConsole(uint dwProcessId);

    // Clipboard.
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool IsClipboardFormatAvailable(uint format);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern uint RegisterClipboardFormatW(string lpszFormat);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr GetClipboardData(uint uFormat);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

    [DllImport("kernel32.dll")]
    internal static extern IntPtr GlobalAlloc(uint uFlags, nuint dwBytes);

    [DllImport("kernel32.dll")]
    internal static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll")]
    internal static extern bool GlobalUnlock(IntPtr hMem);

    [DllImport("kernel32.dll")]
    internal static extern IntPtr GlobalFree(IntPtr hMem);

    [DllImport("kernel32.dll")]
    internal static extern nuint GlobalSize(IntPtr hMem);

    /// <summary>Opts the process into per-monitor DPI awareness so the UI is crisp at any scale.</summary>
    internal static bool TryEnablePerMonitorDpi() => SetProcessDpiAwarenessContext(DpiAwarenessPerMonitorV2);

    /// <summary>Working area of the primary monitor, in physical pixels.</summary>
    internal static RECT GetPrimaryWorkArea()
    {
        var work = new RECT { Right = 1920, Bottom = 1080 };
        SystemParametersInfoW(SpiGetWorkArea, 0, ref work, 0);
        return work;
    }

    /// <summary>Working area of the monitor that hosts <paramref name="hwnd"/>, in physical pixels.</summary>
    internal static RECT GetWorkArea(IntPtr hwnd)
    {
        var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        if (monitor != IntPtr.Zero)
        {
            var info = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfoW(monitor, ref info))
            {
                return info.rcWork;
            }
        }

        return GetPrimaryWorkArea();
    }

    /// <summary>Extracts the signed low word of a packed coordinate.</summary>
    internal static int LowWord(IntPtr value) => (short)((long)value & 0xFFFF);

    /// <summary>Extracts the signed high word of a packed coordinate.</summary>
    internal static int HighWord(IntPtr value) => (short)(((long)value >> 16) & 0xFFFF);
}
