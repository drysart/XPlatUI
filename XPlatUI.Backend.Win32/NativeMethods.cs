using System.Runtime.InteropServices;

namespace XPlatUI.Backend.Win32;

/// <summary>
/// The Win32 surface used by the Windows backend: window creation, GDI double buffering, input
/// and clipboard helpers. Nothing here is needed by the UI library itself - a macOS or Linux
/// backend calls its own native API instead.
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
    internal const uint WmSettingChange = 0x001A;
    internal const uint WmThemeChanged = 0x031A;
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
    internal const int HtTransparent = -1;
    internal const int SwShow = 5;
    internal const int SwShowNoActivate = 4;
    internal const uint SwpNoZOrder = 0x0004;
    internal const uint SwpNoActivate = 0x0010;
    internal const uint SwpFrameChanged = 0x0020;
    internal const uint SwpShowWindow = 0x0040;

    /// <summary>SetWindowPos z-order insert-after values.</summary>
    internal static readonly IntPtr HwndTopMost = new(-1);
    internal static readonly IntPtr HwndTop = new(0);

    // Popup menu window.
    internal const uint WsPopup = 0x80000000;
    internal const uint WsClipChildren = 0x02000000;
    internal const uint WsExToolWindow = 0x00000080;
    internal const uint WsExTopmost = 0x00000008;
    internal const uint WsExLayered = 0x00080000;
    internal const int GwlpHwndParent = -8;

    internal const uint WmActivate = 0x0006;
    internal const uint WmActivateApp = 0x001C;
    internal const uint WmCaptureChanged = 0x0215;
    internal const uint WmNcHitTest = 0x0084;
    internal const int WaInactive = 0;

    internal const int VkReturn = 0x0D;
    internal const int VkEscape = 0x1B;
    internal const int VkEnd = 0x23;
    internal const int VkHome = 0x24;
    internal const int VkLeft = 0x25;
    internal const int VkUp = 0x26;
    internal const int VkRight = 0x27;
    internal const int VkDown = 0x28;

    // Painting.
    internal const uint SrcCopy = 0x00CC0020;

    /// <summary>Includes layered windows in the copy, which plain <c>SRCCOPY</c> skips.</summary>
    internal const uint CaptureBlt = 0x40000000;

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

    // Window icons. Windows keeps two per window: the small one for the title bar and the big one
    // for Alt-Tab and the taskbar.
    internal const uint WmSetIcon = 0x0080;
    internal const int IconSmall = 0;
    internal const int IconBig = 1;
    internal const int SmCxIcon = 11;
    internal const int SmCyIcon = 12;
    internal const int SmCxSmIcon = 49;
    internal const int SmCySmIcon = 50;

    // Clipboard.
    internal const uint CfUnicodeText = 13;
    internal const uint GmemMoveable = 0x0002;

    // DPI.
    internal const uint SpiGetWorkArea = 0x0030;
    internal const uint MonitorDefaultToNearest = 0x00000002;
    internal const uint MonitorInfoPrimary = 0x00000001;
    internal static readonly IntPtr DpiAwarenessUnaware = new(-1);
    internal static readonly IntPtr DpiAwarenessSystem = new(-2);
    internal static readonly IntPtr DpiAwarenessPerMonitorV2 = new(-4);

    /// <summary><c>GetDpiForMonitor</c> mode: the effective (post-scaling) DPI.</summary>
    internal const int MdtEffectiveDpi = 0;

    /// <summary>The pseudo-window that owns a message-only window, which never appears on screen.</summary>
    internal static readonly IntPtr HwndMessage = new(-3);

    /// <summary>The parent console, for <c>AttachConsole</c>.</summary>
    internal const uint AttachParentProcess = 0xFFFFFFFF;

    // Window styles toggled by IXPlatWindowBackend.Resizable.
    internal const uint WsThickFrame = 0x00040000;
    internal const uint WsMinimizeBox = 0x00020000;
    internal const uint WsMaximizeBox = 0x00010000;
    internal const int GwlStyle = -16;

    internal const uint SwpNoSize = 0x0001;
    internal const uint SwpNoMove = 0x0002;
    internal const int SwMinimize = 6;
    internal const int SwMaximize = 3;
    internal const int SwRestore = 9;

    // WM_SIZE wParam values, which are also how a state change is observed.
    internal const int SizeRestored = 0;
    internal const int SizeMinimized = 1;
    internal const int SizeMaximized = 2;

    /// <summary>First message id an application may define. Backend loop messages live from here.</summary>
    internal const uint WmApp = 0x8000;

    /// <summary>Posted to the message-only window to drain queued <c>Invoke</c> callbacks.</summary>
    internal const uint WmAppInvoke = WmApp + 1;

    /// <summary>Posted to the message-only window to end the loop on the thread that runs it.</summary>
    internal const uint WmAppQuit = WmApp + 2;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT lprcMonitor, IntPtr dwData);

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

    [StructLayout(LayoutKind.Sequential)]
    internal struct ICONINFO
    {
        /// <summary>Non-zero for an icon, zero for a cursor.</summary>
        public int fIcon;
        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
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

    [StructLayout(LayoutKind.Sequential)]
    internal struct SIZE
    {
        public int cx;
        public int cy;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct BLENDFUNCTION
    {
        public byte BlendOp;
        public byte BlendFlags;
        public byte SourceConstantAlpha;
        public byte AlphaFormat;
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

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern IntPtr SendMessageW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    /// <summary>Builds an icon from a colour bitmap plus the monochrome mask that describes its transparency.</summary>
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr CreateIconIndirect(ref ICONINFO piconinfo);

    /// <summary>Releases an icon created by <see cref="CreateIconIndirect"/>. System icons must not be passed to it.</summary>
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool DestroyIcon(IntPtr hIcon);

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

    /// <summary>System metrics for a specific DPI, so an icon matches the display it is drawn on.</summary>
    [DllImport("user32.dll")]
    internal static extern int GetSystemMetricsForDpi(int nIndex, uint dpi);

    [DllImport("user32.dll")]
    internal static extern bool TrackMouseEvent(ref TRACKMOUSEEVENT lpEventTrack);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern bool SystemParametersInfoW(uint uiAction, uint uiParam, ref RECT pvParam, uint fWinIni);

    [DllImport("user32.dll")]
    internal static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("user32.dll")]
    internal static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll")]
    internal static extern uint GetDpiForSystem();

    /// <summary>Returns an HRESULT; anything non-zero means the fallback DPI should be used.</summary>
    [DllImport("Shcore.dll")]
    internal static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

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
    internal static extern IntPtr SetFocus(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr SetWindowLongPtrW(IntPtr hwnd, int index, IntPtr value);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr GetWindowLongPtrW(IntPtr hwnd, int index);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool SetWindowTextW(IntPtr hwnd, string lpString);

    [DllImport("user32.dll")]
    internal static extern bool GetWindowRect(IntPtr hwnd, out RECT lpRect);

    [DllImport("user32.dll")]
    internal static extern bool EnableWindow(IntPtr hwnd, bool bEnable);

    [DllImport("user32.dll")]
    internal static extern bool IsZoomed(IntPtr hwnd);

    [DllImport("user32.dll")]
    internal static extern bool IsIconic(IntPtr hwnd);

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

    /// <summary>Creates a device-dependent bitmap, used here for an icon's monochrome AND mask.</summary>
    [DllImport("gdi32.dll")]
    internal static extern IntPtr CreateBitmap(int nWidth, int nHeight, uint nPlanes, uint nBitCount, byte[] lpvBits);

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

    // The BLENDFUNCTION used to describe a premultiplied surface to the compositor.
    internal const byte AcSrcOver = 0;
    internal const byte AcSrcAlpha = 1;

    // UpdateLayeredWindow flags.
    internal const uint UlwAlpha = 0x00000002;

    /// <summary>
    /// Hands a premultiplied 32bpp DIB to the compositor as the whole content of a layered window,
    /// so the alpha channel in it decides what the desktop shows through.
    /// </summary>
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool UpdateLayeredWindow(
        IntPtr hwnd,
        IntPtr hdcDst,
        ref POINT pptDst,
        ref SIZE psize,
        IntPtr hdcSrc,
        ref POINT pptSrc,
        uint crKey,
        ref BLENDFUNCTION pblend,
        uint dwFlags);

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

    // Appearance. The system's app-mode preference lives in the registry; WM_SETTINGCHANGE with
    // "ImmersiveColorSet" and WM_THEMECHANGED are the two notifications that say it may have moved.
    internal static readonly IntPtr HkeyCurrentUser = new(0x80000001);

    private const uint RrfRtRegDword = 0x00000010;
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaUseImmersiveDarkModeBefore20H1 = 19;

    /// <summary>Reads a REG_DWORD from HKEY_CURRENT_USER, or null when the value is absent.</summary>
    internal static uint? ReadUserDword(string subKey, string valueName)
    {
        uint type;
        uint data;
        uint size = sizeof(uint);
        int status = RegGetValueW(HkeyCurrentUser, subKey, valueName, RrfRtRegDword, out type, out data, ref size);
        return status == 0 && size == sizeof(uint) ? data : null;
    }

    /// <summary>
    /// Undocumented uxtheme export that reports the system-wide dark mode switch. Only consulted
    /// when the per-app value is missing, which happens on builds older than Windows 10 1903.
    /// </summary>
    internal static bool ShouldSystemUseDarkMode()
    {
        try
        {
            return ShouldSystemUseDarkModeNative();
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    /// <summary>Asks DWM to draw the non-client area (title bar, borders) dark.</summary>
    internal static void SetDarkTitleBar(IntPtr hwnd, bool dark)
    {
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        int value = dark ? 1 : 0;
        // 20 is DWMWA_USE_IMMERSIVE_DARK_MODE on Windows 10 2004+; 19 on 1809-1909.
        if (DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref value, sizeof(int)) != 0)
        {
            DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkModeBefore20H1, ref value, sizeof(int));
        }
    }

    /// <summary>
    /// Windows 11 (build 22000) is the first release whose menus use the modern rounded, softly
    /// shadowed style, so that is the cut-off for drawing one instead of calling
    /// <c>TrackPopupMenuEx</c>.
    /// </summary>
    internal static bool SupportsModernPopups => Environment.OSVersion.Version.Build >= 22000;

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int RegGetValueW(
        IntPtr hkey,
        string lpSubKey,
        string lpValue,
        uint dwFlags,
        out uint pdwType,
        out uint pvData,
        ref uint pcbData);

    [DllImport("uxtheme.dll", ExactSpelling = true, EntryPoint = "#104")]
    private static extern bool ShouldSystemUseDarkModeNative();

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    // Message box: the flags, the ids it returns, and the one call.
    internal const uint MbOk = 0x00000000;
    internal const uint MbOkCancel = 0x00000001;
    internal const uint MbAbortRetryIgnore = 0x00000002;
    internal const uint MbYesNoCancel = 0x00000003;
    internal const uint MbYesNo = 0x00000004;
    internal const uint MbRetryCancel = 0x00000005;
    internal const uint MbIconError = 0x00000010;
    internal const uint MbIconQuestion = 0x00000020;
    internal const uint MbIconWarning = 0x00000030;
    internal const uint MbIconInformation = 0x00000040;
    internal const uint MbDefButton1 = 0x00000000;
    internal const uint MbDefButton2 = 0x00000100;
    internal const uint MbDefButton3 = 0x00000200;
    internal const uint MbTopMost = 0x00040000;

    internal const int IdOk = 1;
    internal const int IdCancel = 2;
    internal const int IdAbort = 3;
    internal const int IdRetry = 4;
    internal const int IdIgnore = 5;
    internal const int IdYes = 6;
    internal const int IdNo = 7;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern int MessageBoxW(IntPtr hwnd, string text, string caption, uint type);

    // COM activation for the file dialogs.
    internal const uint ClsCtxInprocServer = 0x00000001;
    internal const int ErrorCancelled = unchecked((int)0x800704C7);

    // Apartment initialization. The shell's file dialogs are in-proc COM objects that only work on
    // a single-threaded apartment.
    internal const uint CoInitApartmentThreaded = 0x00000002;
    internal const int SOk = 0;
    internal const int SFalse = 1;

    [DllImport("ole32.dll", ExactSpelling = true)]
    internal static extern int CoInitializeEx(IntPtr reserved, uint flags);

    [DllImport("ole32.dll", ExactSpelling = true)]
    internal static extern void CoUninitialize();

    [DllImport("ole32.dll", ExactSpelling = true)]
    internal static extern int CoCreateInstance(
        in Guid classId,
        IntPtr outer,
        uint context,
        in Guid interfaceId,
        out IntPtr instance);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern int SHCreateItemFromParsingName(
        string path,
        IntPtr bindContext,
        in Guid interfaceId,
        out IntPtr item);
}
