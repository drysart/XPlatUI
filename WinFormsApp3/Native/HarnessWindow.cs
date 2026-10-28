using System.Runtime.InteropServices;
using SkiaSharp;
using XPlatUI;

namespace WinFormsApp3;

/// <summary>
/// The Windows host for the XPlat UI library, built directly on Win32 so the harness needs
/// neither Windows Forms nor System.Drawing (and therefore stays trimmable / NativeAOT friendly).
/// It owns an <see cref="XPlatUiHost"/>, rasterizes the control tree into a Skia surface backed by
/// a GDI DIB section and forwards mouse/keyboard input. A macOS (NSView) or Linux (X11/Wayland)
/// host would implement exactly the same duties with its own native API.
/// </summary>
internal sealed class HarnessWindow : IDisposable
{
    private const string ClassName = "XPlatUI.HarnessWindow";
    private const string WindowTitle = "XPlat UI - SkiaSharp test harness";

    /// <summary>The sample UI is designed against this logical (96 DPI) client size.</summary>
    private const int DesignClientWidth = 1040;
    private const int DesignClientHeight = 760;
    private const int DesignMinimumWidth = 820;
    private const int DesignMinimumHeight = 660;

    /// <summary>Interval of the timer that drives time based UI behaviour (caret blinking).</summary>
    private const int TickIntervalMs = 250;
    private const int TickTimerId = 1;

    private readonly XPlatUiHost _host = new();
    private readonly NativeMethods.WndProc _wndProc;
    private readonly Dictionary<XPlatCursor, IntPtr> _cursors = new();

    private IntPtr _hwnd;
    private IntPtr _moduleHandle;
    private bool _classRegistered;
    private bool _disposed;

    private float _scale = 1f;
    private IntPtr _memoryDc;
    private IntPtr _dib;
    private IntPtr _dibBits;
    private IntPtr _previousBitmap;
    private int _bufferWidth;
    private int _bufferHeight;
    private SKSurface? _skia;

    private bool _trackingLeave;
    private long _lastClickTime;
    private int _clickCount;
    private int _lastClickX;
    private int _lastClickY;

    public HarnessWindow()
    {
        // The delegate must stay rooted for as long as the window class points at it.
        _wndProc = WindowProc;

        _host.ClearColor = new SKColor(0xFF, 0xFF, 0xFF);
        _host.Invalidated += (_, _) => Invalidate();
        _host.CursorChanged += (_, _) => ApplyCursor();
    }

    /// <summary>The platform independent UI host.</summary>
    public XPlatUiHost Host => _host;

    /// <summary>Sets the root of the control tree and lays it out to fill the window.</summary>
    public XPlatControl? Root
    {
        get => _host.Root;
        set
        {
            _host.Root = value;
            UpdateHostSize();
        }
    }

    /// <summary>Device pixels per logical pixel.</summary>
    public float DpiScale => _scale;

    /// <summary>Registers the window class and creates and shows the window.</summary>
    public bool Create()
    {
        _moduleHandle = NativeMethods.GetModuleHandleW(null);

        var windowClass = new NativeMethods.WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<NativeMethods.WNDCLASSEXW>(),
            style = NativeMethods.CsHRedraw | NativeMethods.CsVRedraw | NativeMethods.CsDblClks,
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = _moduleHandle,
            lpszClassName = ClassName,
        };

        var icon = NativeMethods.LoadIconW(IntPtr.Zero, new IntPtr(NativeMethods.IdiApplication));
        windowClass.hIcon = icon;
        windowClass.hIconSm = icon;

        if (NativeMethods.RegisterClassExW(ref windowClass) == 0)
        {
            return false;
        }

        _classRegistered = true;

        // Create the window first: its DPI can only be queried once it exists, and sizing against
        // the wrong scale would crop the logical layout on high-DPI displays.
        var provisional = OuterSizeFor(DesignClientWidth, DesignClientHeight);
        var work = NativeMethods.GetPrimaryWorkArea();
        var x = work.Left + Math.Max(((work.Right - work.Left) - provisional.Width) / 2, 0);
        var y = work.Top + Math.Max(((work.Bottom - work.Top) - provisional.Height) / 2, 0);

        _hwnd = NativeMethods.CreateWindowExW(
            0,
            ClassName,
            WindowTitle,
            NativeMethods.WsOverlappedWindow,
            x,
            y,
            provisional.Width,
            provisional.Height,
            IntPtr.Zero,
            IntPtr.Zero,
            _moduleHandle,
            IntPtr.Zero);

        if (_hwnd == IntPtr.Zero)
        {
            return false;
        }

        ApplyDpi(NativeMethods.GetDpiForWindow(_hwnd));

        NativeMethods.SetTimer(_hwnd, (nuint)TickTimerId, TickIntervalMs, IntPtr.Zero);
        NativeMethods.ShowWindow(_hwnd, NativeMethods.SwShow);
        NativeMethods.UpdateWindow(_hwnd);
        return true;
    }

    /// <summary>
    /// The XPlat tree lays out in logical (96 DPI) units, so the window is resized to the display
    /// scale; otherwise the sample UI would be cropped on high-DPI screens.
    /// </summary>
    private void ApplyDpi(uint dpi)
    {
        _scale = dpi > 0 ? dpi / 96f : 1f;

        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        var work = NativeMethods.GetWorkArea(_hwnd);
        var frame = OuterSizeFor(0, 0);
        var workWidth = Math.Max((work.Right - work.Left) - frame.Width, 1);
        var workHeight = Math.Max((work.Bottom - work.Top) - frame.Height, 1);

        var clientWidth = Math.Max(ToDevicePixels(DesignClientWidth, workWidth), 1);
        var clientHeight = Math.Max(ToDevicePixels(DesignClientHeight, workHeight), 1);
        var outer = OuterSizeFor(clientWidth, clientHeight);

        var x = work.Left + Math.Max(((work.Right - work.Left) - outer.Width) / 2, 0);
        var y = work.Top + Math.Max(((work.Bottom - work.Top) - outer.Height) / 2, 0);

        NativeMethods.SetWindowPos(
            _hwnd,
            IntPtr.Zero,
            x,
            y,
            outer.Width,
            outer.Height,
            NativeMethods.SwpNoZOrder | NativeMethods.SwpNoActivate);
    }

    /// <summary>Runs the Win32 message pump until the window closes; returns the process exit code.</summary>
    public int Run()
    {
        while (true)
        {
            var result = NativeMethods.GetMessageW(out var message, IntPtr.Zero, 0, 0);
            if (result == 0)
            {
                return (int)message.wParam;
            }

            if (result < 0)
            {
                return -1;
            }

            // Swallowing the key here (instead of in the window procedure) keeps TranslateMessage
            // from also turning it into a WM_CHAR, which would double up text input.
            if (message.message == NativeMethods.WmKeyDown && TryHandleKeyDown(message.wParam))
            {
                continue;
            }

            NativeMethods.TranslateMessage(ref message);
            NativeMethods.DispatchMessageW(ref message);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_hwnd != IntPtr.Zero)
        {
            NativeMethods.KillTimer(_hwnd, (nuint)TickTimerId);
            NativeMethods.DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }

        ReleaseBuffer();

        if (_classRegistered)
        {
            NativeMethods.UnregisterClassW(ClassName, _moduleHandle);
            _classRegistered = false;
        }

        // System cursors are shared, so the cached handles are dropped without being destroyed.
        _cursors.Clear();
    }

    private IntPtr WindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (_hwnd == IntPtr.Zero)
        {
            _hwnd = hwnd;
        }

        switch (message)
        {
            case NativeMethods.WmPaint:
                OnPaint(hwnd);
                return IntPtr.Zero;

            case NativeMethods.WmPrintClient:
                // Lets the window be captured into an arbitrary DC (PrintWindow, screenshot tools,
                // automated visual tests) using the same back buffer that WM_PAINT blits from.
                OnPrintClient(wParam);
                return IntPtr.Zero;

            case NativeMethods.WmEraseBkgnd:
                // The Skia back buffer always covers the whole client area.
                return new IntPtr(1);

            case NativeMethods.WmSize:
                OnSize(NativeMethods.LowWord(lParam), NativeMethods.HighWord(lParam));
                return IntPtr.Zero;

            case NativeMethods.WmDpiChanged:
                OnDpiChanged(hwnd, wParam, lParam);
                return IntPtr.Zero;

            case NativeMethods.WmGetMinMaxInfo:
                OnGetMinMaxInfo(lParam);
                return IntPtr.Zero;

            case NativeMethods.WmTimer:
                if (wParam == TickTimerId)
                {
                    _host.ProcessTick(TimeSpan.FromMilliseconds(TickIntervalMs));
                }

                return IntPtr.Zero;

            case NativeMethods.WmMouseMove:
                OnMouseMove(hwnd, lParam);
                return IntPtr.Zero;

            case NativeMethods.WmMouseLeave:
                _trackingLeave = false;
                _host.ProcessMouseLeave();
                return IntPtr.Zero;

            case NativeMethods.WmLButtonDown:
            case NativeMethods.WmLButtonDblClk:
                OnMouseDown(hwnd, lParam, XPlatMouseButtons.Left);
                return IntPtr.Zero;

            case NativeMethods.WmRButtonDown:
            case NativeMethods.WmRButtonDblClk:
                OnMouseDown(hwnd, lParam, XPlatMouseButtons.Right);
                return IntPtr.Zero;

            case NativeMethods.WmMButtonDown:
            case NativeMethods.WmMButtonDblClk:
                OnMouseDown(hwnd, lParam, XPlatMouseButtons.Middle);
                return IntPtr.Zero;

            case NativeMethods.WmLButtonUp:
                OnMouseUp(lParam, XPlatMouseButtons.Left);
                return IntPtr.Zero;

            case NativeMethods.WmRButtonUp:
                OnMouseUp(lParam, XPlatMouseButtons.Right);
                return IntPtr.Zero;

            case NativeMethods.WmMButtonUp:
                OnMouseUp(lParam, XPlatMouseButtons.Middle);
                return IntPtr.Zero;

            case NativeMethods.WmKeyUp:
                OnKeyUp(wParam);
                return IntPtr.Zero;

            case NativeMethods.WmChar:
                OnChar(wParam);
                return IntPtr.Zero;

            case NativeMethods.WmSetCursor:
                if (NativeMethods.LowWord(lParam) == NativeMethods.HtClient)
                {
                    NativeMethods.SetCursor(CursorFor(_host.Cursor));
                    return new IntPtr(1);
                }

                break;

            case NativeMethods.WmClose:
                NativeMethods.DestroyWindow(hwnd);
                return IntPtr.Zero;

            case NativeMethods.WmDestroy:
                NativeMethods.KillTimer(hwnd, (nuint)TickTimerId);
                _hwnd = IntPtr.Zero;
                NativeMethods.PostQuitMessage(0);
                return IntPtr.Zero;
        }

        return NativeMethods.DefWindowProcW(hwnd, message, wParam, lParam);
    }

    private void OnPaint(IntPtr hwnd)
    {
        var hdc = NativeMethods.BeginPaint(hwnd, out var paint);
        try
        {
            if (_skia is not null && _memoryDc != IntPtr.Zero)
            {
                Render();
                NativeMethods.BitBlt(hdc, 0, 0, _bufferWidth, _bufferHeight, _memoryDc, 0, 0, NativeMethods.SrcCopy);
            }
        }
        finally
        {
            NativeMethods.EndPaint(hwnd, ref paint);
        }
    }

    private void OnPrintClient(IntPtr hdc)
    {
        if (_skia is null || _memoryDc == IntPtr.Zero)
        {
            return;
        }

        Render();
        NativeMethods.BitBlt(hdc, 0, 0, _bufferWidth, _bufferHeight, _memoryDc, 0, 0, NativeMethods.SrcCopy);
    }

    private void Render()
    {
        if (_skia is null)
        {
            return;
        }

        var canvas = _skia.Canvas;
        canvas.Save();
        canvas.Scale(_scale);
        _host.Paint(canvas);
        canvas.Restore();
        canvas.Flush();
    }

    private void OnSize(int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            return;
        }

        EnsureBuffer(width, height);
        UpdateHostSize();
        Invalidate();
    }

    private void OnDpiChanged(IntPtr hwnd, IntPtr wParam, IntPtr lParam)
    {
        var dpi = NativeMethods.HighWord(wParam);
        if (dpi > 0)
        {
            _scale = dpi / 96f;
        }

        // Follow the rectangle Windows suggests so the window keeps its logical size.
        var suggested = Marshal.PtrToStructure<NativeMethods.RECT>(lParam);
        NativeMethods.SetWindowPos(
            hwnd,
            IntPtr.Zero,
            suggested.Left,
            suggested.Top,
            suggested.Right - suggested.Left,
            suggested.Bottom - suggested.Top,
            NativeMethods.SwpNoZOrder | NativeMethods.SwpNoActivate);
    }

    private void OnGetMinMaxInfo(IntPtr lParam)
    {
        var info = Marshal.PtrToStructure<NativeMethods.MINMAXINFO>(lParam);
        var minimum = OuterSizeFor(ToDevicePixels(DesignMinimumWidth, int.MaxValue), ToDevicePixels(DesignMinimumHeight, int.MaxValue));
        info.ptMinTrackSize.X = minimum.Width;
        info.ptMinTrackSize.Y = minimum.Height;
        Marshal.StructureToPtr(info, lParam, false);
    }

    private void OnMouseMove(IntPtr hwnd, IntPtr lParam)
    {
        if (!_trackingLeave)
        {
            var track = new NativeMethods.TRACKMOUSEEVENT
            {
                cbSize = (uint)Marshal.SizeOf<NativeMethods.TRACKMOUSEEVENT>(),
                dwFlags = NativeMethods.TmeLeave,
                hwndTrack = hwnd,
            };

            _trackingLeave = NativeMethods.TrackMouseEvent(ref track);
        }

        _host.ProcessMouseMove(ToLogical(lParam));
    }

    private void OnMouseDown(IntPtr hwnd, IntPtr lParam, XPlatMouseButtons button)
    {
        NativeMethods.SetCapture(hwnd);
        _host.ProcessMouseDown(ToLogical(lParam), button, CurrentModifiers(), NextClickCount(lParam));
    }

    private void OnMouseUp(IntPtr lParam, XPlatMouseButtons button)
    {
        _host.ProcessMouseUp(ToLogical(lParam), button, CurrentModifiers());
        NativeMethods.ReleaseCapture();
    }

    private void OnKeyUp(IntPtr wParam)
    {
        var key = MapKey((int)wParam);
        if (key != XPlatKey.None)
        {
            _host.ProcessKeyUp(key, CurrentModifiers());
        }
    }

    private void OnChar(IntPtr wParam)
    {
        var character = (char)(int)wParam;
        if (char.IsControl(character))
        {
            return;
        }

        _host.ProcessTextInput(character.ToString());
    }

    /// <summary>Forwards a key press; returns true when the UI consumed it.</summary>
    private bool TryHandleKeyDown(IntPtr wParam)
    {
        var key = MapKey((int)wParam);
        return key != XPlatKey.None && _host.ProcessKeyDown(key, CurrentModifiers());
    }

    private void UpdateHostSize()
    {
        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        if (!NativeMethods.GetClientRect(_hwnd, out var client) || client.Right <= 0 || client.Bottom <= 0)
        {
            return;
        }

        _host.SetClientSize(client.Right / _scale, client.Bottom / _scale);
    }

    private void Invalidate()
    {
        if (_hwnd != IntPtr.Zero)
        {
            NativeMethods.InvalidateRect(_hwnd, IntPtr.Zero, false);
        }
    }

    private SKPoint ToLogical(IntPtr lParam) =>
        new(NativeMethods.LowWord(lParam) / _scale, NativeMethods.HighWord(lParam) / _scale);

    private static XPlatModifiers CurrentModifiers()
    {
        var modifiers = XPlatModifiers.None;

        if ((NativeMethods.GetKeyState(NativeMethods.VkShift) & 0x8000) != 0)
        {
            modifiers |= XPlatModifiers.Shift;
        }

        if ((NativeMethods.GetKeyState(NativeMethods.VkControl) & 0x8000) != 0)
        {
            modifiers |= XPlatModifiers.Control;
        }

        if ((NativeMethods.GetKeyState(NativeMethods.VkMenu) & 0x8000) != 0)
        {
            modifiers |= XPlatModifiers.Alt;
        }

        return modifiers;
    }

    /// <summary>
    /// Win32 sends a separate double-click message, so successive presses are counted here to give
    /// the control tree the 1, 2, 3... sequence it expects.
    /// </summary>
    private int NextClickCount(IntPtr lParam)
    {
        var x = NativeMethods.LowWord(lParam);
        var y = NativeMethods.HighWord(lParam);
        var now = Environment.TickCount64;
        var slopX = NativeMethods.GetSystemMetrics(NativeMethods.SmCxDoubleClk) / 2;
        var slopY = NativeMethods.GetSystemMetrics(NativeMethods.SmCyDoubleClk) / 2;

        var withinTime = _clickCount > 0 && now - _lastClickTime <= NativeMethods.GetDoubleClickTime();
        var withinArea = Math.Abs(x - _lastClickX) <= slopX && Math.Abs(y - _lastClickY) <= slopY;

        _clickCount = withinTime && withinArea ? _clickCount + 1 : 1;
        _lastClickTime = now;
        _lastClickX = x;
        _lastClickY = y;
        return _clickCount;
    }

    private void ApplyCursor() => NativeMethods.SetCursor(CursorFor(_host.Cursor));

    private IntPtr CursorFor(XPlatCursor cursor)
    {
        if (_cursors.TryGetValue(cursor, out var handle))
        {
            return handle;
        }

        var id = cursor switch
        {
            XPlatCursor.Hand => NativeMethods.IdcHand,
            XPlatCursor.IBeam => NativeMethods.IdcIBeam,
            XPlatCursor.Cross => NativeMethods.IdcCross,
            XPlatCursor.SizeAll => NativeMethods.IdcSizeAll,
            XPlatCursor.SizeNS => NativeMethods.IdcSizeNs,
            XPlatCursor.SizeWE => NativeMethods.IdcSizeWe,
            _ => NativeMethods.IdcArrow,
        };

        handle = NativeMethods.LoadCursorW(IntPtr.Zero, new IntPtr(id));
        _cursors[cursor] = handle;
        return handle;
    }

    private static XPlatKey MapKey(int virtualKey)
    {
        if (virtualKey is >= 0x41 and <= 0x5A)
        {
            return XPlatKey.A + (virtualKey - 0x41);
        }

        if (virtualKey is >= 0x30 and <= 0x39)
        {
            return XPlatKey.D0 + (virtualKey - 0x30);
        }

        return virtualKey switch
        {
            0x09 => XPlatKey.Tab,
            0x0D => XPlatKey.Enter,
            0x20 => XPlatKey.Space,
            0x1B => XPlatKey.Escape,
            0x08 => XPlatKey.Backspace,
            0x2E => XPlatKey.Delete,
            0x25 => XPlatKey.Left,
            0x27 => XPlatKey.Right,
            0x26 => XPlatKey.Up,
            0x28 => XPlatKey.Down,
            0x24 => XPlatKey.Home,
            0x23 => XPlatKey.End,
            0x21 => XPlatKey.PageUp,
            0x22 => XPlatKey.PageDown,
            _ => XPlatKey.None,
        };
    }

    private int ToDevicePixels(int logicalPixels, int limit) =>
        Math.Max(Math.Min((int)MathF.Round(logicalPixels * _scale), limit), 0);

    private (int Width, int Height) OuterSizeFor(int clientWidth, int clientHeight)
    {
        var rect = new NativeMethods.RECT { Right = clientWidth, Bottom = clientHeight };
        var dpi = (uint)MathF.Round(_scale * 96f);
        NativeMethods.AdjustWindowRectExForDpi(ref rect, NativeMethods.WsOverlappedWindow, false, 0, dpi == 0 ? 96u : dpi);
        return (rect.Right - rect.Left, rect.Bottom - rect.Top);
    }

    private void EnsureBuffer(int width, int height)
    {
        if (_skia is not null && _bufferWidth == width && _bufferHeight == height)
        {
            return;
        }

        ReleaseBuffer();

        var info = new NativeMethods.BITMAPINFO();
        info.bmiHeader.biSize = (uint)Marshal.SizeOf<NativeMethods.BITMAPINFOHEADER>();
        info.bmiHeader.biWidth = width;
        info.bmiHeader.biHeight = -height; // Top-down rows match Skia's memory layout.
        info.bmiHeader.biPlanes = 1;
        info.bmiHeader.biBitCount = 32;
        info.bmiHeader.biCompression = NativeMethods.BiRgb;

        _dib = NativeMethods.CreateDIBSection(IntPtr.Zero, ref info, NativeMethods.DibRgbColors, out _dibBits, IntPtr.Zero, 0);
        if (_dib == IntPtr.Zero)
        {
            return;
        }

        _memoryDc = NativeMethods.CreateCompatibleDC(IntPtr.Zero);
        _previousBitmap = NativeMethods.SelectObject(_memoryDc, _dib);

        var imageInfo = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        _skia = SKSurface.Create(imageInfo, _dibBits, width * 4);

        if (_skia is null)
        {
            ReleaseBuffer();
            return;
        }

        _bufferWidth = width;
        _bufferHeight = height;
    }

    private void ReleaseBuffer()
    {
        _skia?.Dispose();
        _skia = null;

        if (_memoryDc != IntPtr.Zero)
        {
            if (_previousBitmap != IntPtr.Zero)
            {
                NativeMethods.SelectObject(_memoryDc, _previousBitmap);
                _previousBitmap = IntPtr.Zero;
            }

            NativeMethods.DeleteDC(_memoryDc);
            _memoryDc = IntPtr.Zero;
        }

        if (_dib != IntPtr.Zero)
        {
            NativeMethods.DeleteObject(_dib);
            _dib = IntPtr.Zero;
        }

        _dibBits = IntPtr.Zero;
        _bufferWidth = 0;
        _bufferHeight = 0;
    }
}
