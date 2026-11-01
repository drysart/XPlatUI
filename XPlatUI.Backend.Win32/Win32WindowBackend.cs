using System.Globalization;
using System.Runtime.InteropServices;
using SkiaSharp;

namespace XPlatUI.Backend.Win32;

/// <summary>
/// One Win32 window, built directly on <c>user32</c> so nothing here needs Windows Forms or
/// System.Drawing and the whole backend stays trimmable and NativeAOT friendly.
/// </summary>
/// <remarks>
/// <para>
/// The window owns an <see cref="XPlatUiHost"/>, rasterizes the control tree into a Skia surface
/// backed by a GDI DIB section, and forwards pointer, key and text input straight into the host.
/// The model never sees input; it only sees the events raised here.
/// </para>
/// <para>
/// The native window is created lazily, on the first <see cref="Show"/> or
/// <see cref="ShowDialog"/>: <see cref="XPlatWindow"/> constructs the backend before the caller
/// has had a chance to set <c>Title</c>, <c>ClientSize</c> or <c>Resizable</c>, so there is nothing
/// worth creating a window for yet.
/// </para>
/// <para>
/// Each window registers its own window class, because Win32 keeps the class name process-global
/// and a per-window class is the only way to give two windows different icons or styles.
/// </para>
/// </remarks>
internal sealed class Win32WindowBackend : IXPlatWindowBackend
{
    private static int _nextClassId;

    private readonly XPlatUiHost _host = new();
    private readonly Win32ApplicationBackend _application;
    private readonly NativeMethods.WndProc _wndProc;
    private readonly Dictionary<XPlatCursor, IntPtr> _cursors = new();
    private readonly string _className;

    private IntPtr _hwnd;
    private IntPtr _moduleHandle;
    private bool _classRegistered;
    private bool _disposed;

    private string _title = string.Empty;
    private SKSize _clientSize = new(800f, 600f);
    private SKSize _minimumClientSize;
    private SKSize _maximumClientSize;
    private SKPoint? _location;
    private bool _resizable = true;
    private bool _topMost;
    private IXPlatWindowBackend? _owner;
    private XPlatWindowState _state = XPlatWindowState.Normal;
    private XPlatMenuBar? _menuBar;

    private XPlatIcon? _icon;
    private IntPtr _bigIcon;
    private IntPtr _smallIcon;

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

    internal Win32WindowBackend(Win32ApplicationBackend application, XPlatWindow window)
    {
        ArgumentNullException.ThrowIfNull(application);
        ArgumentNullException.ThrowIfNull(window);

        _application = application;

        // The delegate must stay rooted for as long as the window class points at it.
        _wndProc = WindowProc;

        // One class per window: the name has to be unique process-wide, and UnregisterClassW in
        // Dispose would otherwise pull the class out from under a sibling window.
        _className = "XPlatUI.Window." + Interlocked.Increment(ref _nextClassId).ToString(CultureInfo.InvariantCulture);

        _host.Invalidated += (_, _) => Invalidate();
        _host.CursorChanged += (_, _) => ApplyCursor();
        XPlatTheme.Changed += OnThemeChanged;
    }

    /// <inheritdoc />
    public XPlatUiHost Host => _host;

    /// <inheritdoc />
    public float ScaleFactor => _scale;

    /// <inheritdoc />
    public XPlatScreen? Screen => _hwnd == IntPtr.Zero
        ? _application.PrimaryScreen
        : Win32ApplicationBackend.ScreenForWindow(_hwnd);

    /// <inheritdoc />
    public string Title
    {
        get => _title;
        set
        {
            _title = value ?? string.Empty;

            if (_hwnd != IntPtr.Zero)
            {
                NativeMethods.SetWindowTextW(_hwnd, _title);
            }
        }
    }

    /// <inheritdoc />
    public XPlatIcon? Icon
    {
        get => _icon;
        set
        {
            _icon = value;
            ApplyIcon();
        }
    }

    /// <inheritdoc />
    public SKSize ClientSize
    {
        get => _clientSize;
        set
        {
            if (value.Width <= 0f || value.Height <= 0f || _clientSize == value)
            {
                return;
            }

            _clientSize = value;

            if (_hwnd == IntPtr.Zero)
            {
                return;
            }

            var outer = OuterSizeFor(value.Width, value.Height);
            NativeMethods.SetWindowPos(
                _hwnd,
                IntPtr.Zero,
                0,
                0,
                outer.Width,
                outer.Height,
                NativeMethods.SwpNoMove | NativeMethods.SwpNoZOrder | NativeMethods.SwpNoActivate);
        }
    }

    /// <inheritdoc />
    public SKSize MinimumClientSize
    {
        get => _minimumClientSize;
        set
        {
            _minimumClientSize = value;

            // Win32 asks for the limit on every drag, so nothing has to be pushed to the window.
        }
    }

    /// <inheritdoc />
    public SKSize MaximumClientSize
    {
        get => _maximumClientSize;
        set
        {
            _maximumClientSize = value;
        }
    }

    /// <inheritdoc />
    public SKPoint Location
    {
        get
        {
            if (_hwnd != IntPtr.Zero && NativeMethods.GetWindowRect(_hwnd, out var rect))
            {
                return new SKPoint(rect.Left / _scale, rect.Top / _scale);
            }

            return _location ?? SKPoint.Empty;
        }
        set
        {
            _location = value;

            if (_hwnd == IntPtr.Zero)
            {
                return;
            }

            NativeMethods.SetWindowPos(
                _hwnd,
                IntPtr.Zero,
                (int)MathF.Round(value.X * _scale),
                (int)MathF.Round(value.Y * _scale),
                0,
                0,
                NativeMethods.SwpNoSize | NativeMethods.SwpNoZOrder | NativeMethods.SwpNoActivate);
        }
    }

    /// <inheritdoc />
    public bool Resizable
    {
        get => _resizable;
        set
        {
            if (_resizable == value)
            {
                return;
            }

            _resizable = value;
            ApplyResizable();
        }
    }

    /// <inheritdoc />
    public bool TopMost
    {
        get => _topMost;
        set
        {
            if (_topMost == value)
            {
                return;
            }

            _topMost = value;

            if (_hwnd == IntPtr.Zero)
            {
                return;
            }

            ApplyTopMost();
        }
    }

    /// <summary>
    /// The window's display state. <see cref="XPlatWindowState.FullScreen"/> maps to a maximized
    /// window for now; true borderless full screen needs a style save/restore that is not worth
    /// carrying until a control asks for it.
    /// </summary>
    public XPlatWindowState State
    {
        get => _hwnd == IntPtr.Zero ? _state : CurrentState();
        set
        {
            _state = value;

            if (_hwnd == IntPtr.Zero)
            {
                return;
            }

            var show = value switch
            {
                XPlatWindowState.Minimized => NativeMethods.SwMinimize,
                XPlatWindowState.Maximized or XPlatWindowState.FullScreen => NativeMethods.SwMaximize,
                _ => NativeMethods.SwRestore,
            };

            NativeMethods.ShowWindow(_hwnd, show);
        }
    }

    /// <summary>
    /// The window's menu bar, or null for none. Stored but not drawn yet: menu bar rendering is a
    /// separate piece of work from moving the harness onto the application model.
    /// </summary>
    public XPlatMenuBar? MenuBar
    {
        get => _menuBar;
        set => _menuBar = value;
    }

    /// <summary>The native window handle, or <see cref="IntPtr.Zero"/> before the window is shown.</summary>
    internal IntPtr Handle => _hwnd;

    /// <inheritdoc />
    public IXPlatWindowBackend? Owner
    {
        get => _owner;
        set
        {
            if (ReferenceEquals(_owner, value))
            {
                return;
            }

            _owner = value;

            if (_hwnd == IntPtr.Zero)
            {
                return;
            }

            // The owner is part of what the shell sees, so the frame is recomputed rather than just
            // the z-order adjusted: without it the taskbar keeps the old grouping.
            NativeMethods.SetWindowLongPtrW(_hwnd, NativeMethods.GwlpHwndParent, OwnerHandle);
            NativeMethods.SetWindowPos(
                _hwnd,
                IntPtr.Zero,
                0,
                0,
                0,
                0,
                NativeMethods.SwpNoMove |
                NativeMethods.SwpNoSize |
                NativeMethods.SwpNoZOrder |
                NativeMethods.SwpNoActivate |
                NativeMethods.SwpFrameChanged);
        }
    }

    /// <inheritdoc />
    public void Show()
    {
        if (!EnsureCreated())
        {
            return;
        }

        Present();
    }

    /// <inheritdoc />
    public void ShowDialog()
    {
        if (!EnsureCreated())
        {
            return;
        }

        var ownerHandle = OwnerHandle;

        if (ownerHandle != IntPtr.Zero)
        {
            NativeMethods.EnableWindow(ownerHandle, false);
        }

        try
        {
            Present();

            // The nested loop ends when this window is destroyed, which is the same signal
            // XPlatWindow uses to decide whether the dialog ever reported a close.
            _application.RunNestedLoop(() => _hwnd != IntPtr.Zero);
        }
        finally
        {
            if (ownerHandle != IntPtr.Zero)
            {
                NativeMethods.EnableWindow(ownerHandle, true);
            }
        }
    }

    /// <inheritdoc />
    public void Activate()
    {
        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        // A minimized window ignores SetForegroundWindow, so restore it first the way the taskbar
        // does; a second launch of the application must not leave the window it brought forward
        // still minimised.
        if (NativeMethods.IsIconic(_hwnd))
        {
            NativeMethods.ShowWindow(_hwnd, NativeMethods.SwRestore);
        }

        if (NativeMethods.SetForegroundWindow(_hwnd))
        {
            NativeMethods.SetFocus(_hwnd);
            return;
        }

        // Windows refuses a foreground change from a process that does not own the foreground.
        // Flashing the taskbar button is what is left, and it keeps the request visible.
        var flash = new NativeMethods.FLASHWINFO
        {
            cbSize = (uint)Marshal.SizeOf<NativeMethods.FLASHWINFO>(),
            hwnd = _hwnd,
            dwFlags = NativeMethods.FlashwAll | NativeMethods.FlashwTimerNoForeground,
            uCount = uint.MaxValue,
        };

        NativeMethods.FlashWindowEx(ref flash);
    }

    /// <inheritdoc />
    public void Close()
    {
        if (!_disposed)
        {
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <inheritdoc />
    public void SetTitleBarAppearance(bool dark) => NativeMethods.SetDarkTitleBar(_hwnd, dark);

    /// <inheritdoc />
    public event EventHandler? Shown;

    /// <inheritdoc />
    public event EventHandler? CloseRequested;

    /// <inheritdoc />
    public event EventHandler? Closed;

    /// <inheritdoc />
    public event EventHandler? Activated;

    /// <inheritdoc />
    public event EventHandler? Deactivated;

    /// <inheritdoc />
    public event EventHandler? ClientSizeChanged;

    /// <inheritdoc />
    public event EventHandler? ScaleChanged;

    /// <inheritdoc />
    public event EventHandler? StateChanged;

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        XPlatTheme.Changed -= OnThemeChanged;

        if (_hwnd != IntPtr.Zero)
        {
            NativeMethods.DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }

        ReleaseIcons();

        ReleaseBuffer();

        if (_classRegistered)
        {
            NativeMethods.UnregisterClassW(_className, _moduleHandle);
            _classRegistered = false;
        }

        // System cursors are shared, so the cached handles are dropped without being destroyed.
        _cursors.Clear();

        _application.RemoveWindow(this);
        Closed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Forwards a key press to the tree; returns true when the UI consumed it.</summary>
    internal bool TryHandleKeyDown(IntPtr wParam)
    {
        var key = MapKey((int)wParam);
        return key != XPlatKey.None && _host.ProcessKeyDown(key, CurrentModifiers());
    }

    /// <summary>Maps a logical client point to physical screen pixels, for native menus.</summary>
    internal NativeMethods.POINT LogicalToScreen(SKPoint point)
    {
        var screen = new NativeMethods.POINT
        {
            X = (int)MathF.Round(point.X * _scale),
            Y = (int)MathF.Round(point.Y * _scale),
        };

        NativeMethods.ClientToScreen(_hwnd, ref screen);
        return screen;
    }

    /// <summary>The native handle of the owning window, or <see cref="IntPtr.Zero"/> when there is none.</summary>
    private IntPtr OwnerHandle => (_owner as Win32WindowBackend)?._hwnd ?? IntPtr.Zero;

    private bool EnsureCreated()
    {
        if (_hwnd != IntPtr.Zero)
        {
            return true;
        }

        if (_disposed)
        {
            return false;
        }

        _moduleHandle = NativeMethods.GetModuleHandleW(null);

        var windowClass = new NativeMethods.WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<NativeMethods.WNDCLASSEXW>(),
            style = NativeMethods.CsHRedraw | NativeMethods.CsVRedraw | NativeMethods.CsDblClks,
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = _moduleHandle,
            lpszClassName = _className,
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
        var provisional = OuterSizeFor(_clientSize.Width, _clientSize.Height);

        _hwnd = NativeMethods.CreateWindowExW(
            0,
            _className,
            _title,
            StyleFor(),
            0,
            0,
            Math.Max(provisional.Width, 1),
            Math.Max(provisional.Height, 1),
            OwnerHandle,
            IntPtr.Zero,
            _moduleHandle,
            IntPtr.Zero);

        if (_hwnd == IntPtr.Zero)
        {
            if (_classRegistered)
            {
                NativeMethods.UnregisterClassW(_className, _moduleHandle);
                _classRegistered = false;
            }

            return false;
        }

        _scale = ScaleOf(NativeMethods.GetDpiForWindow(_hwnd));
        PlaceInitially();
        ApplyResizable();
        ApplyIcon();

        UpdateHostSize();
        return true;
    }

    private void Present()
    {
        NativeMethods.SetDarkTitleBar(_hwnd, XPlatTheme.IsDarkMode);

        // The presenter needs a live HWND, so it can only be installed once the window exists.
        XPlatContextMenuPresenter.Current = new Win32ContextMenuPresenter(_hwnd, LogicalToScreen);

        NativeMethods.ShowWindow(_hwnd, NativeMethods.SwShow);
        NativeMethods.UpdateWindow(_hwnd);

        // Windows ignores a z-order change on a hidden window, so topmost can only be applied once
        // the window is visible - which is why it is not done when the window is created.
        ApplyTopMost();

        Shown?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Puts the window in the topmost band, or takes it out of it. Only has an effect once the
    /// window is visible.
    /// </summary>
    private void ApplyTopMost() =>
        NativeMethods.SetWindowPos(
            _hwnd,
            _topMost ? NativeMethods.HwndTopMost : NativeMethods.HwndTop,
            0,
            0,
            0,
            0,
            NativeMethods.SwpNoMove | NativeMethods.SwpNoSize | NativeMethods.SwpNoActivate);

    /// <summary>Puts the window on the primary display's working area, honouring an explicit location.</summary>
    private void PlaceInitially()
    {
        var outer = OuterSizeFor(_clientSize.Width, _clientSize.Height);

        int x;
        int y;

        if (_location is { } location)
        {
            x = (int)MathF.Round(location.X * _scale);
            y = (int)MathF.Round(location.Y * _scale);
        }
        else
        {
            var work = NativeMethods.GetPrimaryWorkArea();
            x = work.Left + Math.Max(((work.Right - work.Left) - outer.Width) / 2, 0);
            y = work.Top + Math.Max(((work.Bottom - work.Top) - outer.Height) / 2, 0);
        }

        NativeMethods.SetWindowPos(
            _hwnd,
            IntPtr.Zero,
            x,
            y,
            outer.Width,
            outer.Height,
            NativeMethods.SwpNoZOrder | NativeMethods.SwpNoActivate);
    }

    private void ApplyResizable()
    {
        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        var style = (uint)(long)NativeMethods.GetWindowLongPtrW(_hwnd, NativeMethods.GwlStyle);
        const uint frame = NativeMethods.WsThickFrame | NativeMethods.WsMaximizeBox | NativeMethods.WsMinimizeBox;
        style = _resizable ? style | frame : style & ~frame;

        NativeMethods.SetWindowLongPtrW(_hwnd, NativeMethods.GwlStyle, new IntPtr(style));
        NativeMethods.SetWindowPos(
            _hwnd,
            IntPtr.Zero,
            0,
            0,
            0,
            0,
            NativeMethods.SwpNoMove | NativeMethods.SwpNoSize | NativeMethods.SwpNoZOrder |
            NativeMethods.SwpNoActivate | NativeMethods.SwpFrameChanged);
    }

    private uint StyleFor() => _resizable
        ? NativeMethods.WsOverlappedWindow
        : NativeMethods.WsOverlappedWindow & ~(NativeMethods.WsThickFrame | NativeMethods.WsMaximizeBox | NativeMethods.WsMinimizeBox);

    /// <summary>
    /// Rebuilds the window's two icon handles, each sized from the window's DPI, and hands them to
    /// the shell: the small one is the title bar, the big one the taskbar and Alt-Tab. A window with
    /// no icon of its own keeps the window class's generic application icon.
    /// </summary>
    private void ApplyIcon()
    {
        if (_hwnd == IntPtr.Zero || _disposed)
        {
            return;
        }

        var dpi = NativeMethods.GetDpiForWindow(_hwnd);
        var big = CreateIconHandle(NativeMethods.GetSystemMetricsForDpi(NativeMethods.SmCxIcon, dpi));
        var small = CreateIconHandle(NativeMethods.GetSystemMetricsForDpi(NativeMethods.SmCxSmIcon, dpi));

        if (big == IntPtr.Zero || small == IntPtr.Zero)
        {
            // A half-replaced pair is worse than none, so both handles are dropped and the class
            // icon stays in place.
            DestroyIconHandle(big);
            DestroyIconHandle(small);
            return;
        }

        var previousBig = _bigIcon;
        var previousSmall = _smallIcon;
        _bigIcon = big;
        _smallIcon = small;

        NativeMethods.SendMessageW(_hwnd, NativeMethods.WmSetIcon, new IntPtr(NativeMethods.IconBig), big);
        NativeMethods.SendMessageW(_hwnd, NativeMethods.WmSetIcon, new IntPtr(NativeMethods.IconSmall), small);

        // The shell has already switched to the new handles, so the old ones are free to go.
        DestroyIconHandle(previousBig);
        DestroyIconHandle(previousSmall);
    }

    private void ReleaseIcons()
    {
        DestroyIconHandle(_bigIcon);
        DestroyIconHandle(_smallIcon);
        _bigIcon = IntPtr.Zero;
        _smallIcon = IntPtr.Zero;
    }

    private static void DestroyIconHandle(IntPtr handle)
    {
        if (handle != IntPtr.Zero)
        {
            NativeMethods.DestroyIcon(handle);
        }
    }

    /// <summary>Builds an <c>HICON</c> at <paramref name="size"/> pixels square, or <see cref="IntPtr.Zero"/>.</summary>
    private IntPtr CreateIconHandle(int size)
    {
        if (size <= 0 || _icon?.GetBitmap(size) is not { } bitmap)
        {
            return IntPtr.Zero;
        }

        var info = new NativeMethods.BITMAPINFO
        {
            bmiHeader = new NativeMethods.BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<NativeMethods.BITMAPINFOHEADER>(),
                biWidth = size,
                // Negative height makes the DIB top-down, which is the row order Skia uses.
                biHeight = -size,
                biPlanes = 1,
                biBitCount = 32,
                biCompression = NativeMethods.BiRgb,
            },
        };

        var color = NativeMethods.CreateDIBSection(
            IntPtr.Zero,
            ref info,
            NativeMethods.DibRgbColors,
            out var bits,
            IntPtr.Zero,
            0);

        if (color == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        // XPlatIcon hands out tightly packed premultiplied BGRA. GDI blends an icon's colour bitmap
        // as straight alpha, so the premultiplied bytes have to be expanded back or every partly
        // transparent pixel - which is every antialiased edge - comes out too dark.
        var pixels = bitmap.GetPixelSpan();
        var colorBytes = new byte[size * size * 4];

        // The monochrome mask repeats the alpha channel: a set bit means "show the desktop here".
        // Windows blends the colour bitmap's alpha itself, but the mask is what an icon falls back
        // to wherever alpha is ignored.
        var maskStride = ((size + 15) / 16) * 2;
        var maskBytes = new byte[maskStride * size];

        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var offset = ((y * size) + x) * 4;
                var alpha = pixels[offset + 3];

                if (alpha == 0)
                {
                    maskBytes[(y * maskStride) + (x / 8)] |= (byte)(0x80 >> (x % 8));
                    continue;
                }

                colorBytes[offset] = Unpremultiply(pixels[offset], alpha);
                colorBytes[offset + 1] = Unpremultiply(pixels[offset + 1], alpha);
                colorBytes[offset + 2] = Unpremultiply(pixels[offset + 2], alpha);
                colorBytes[offset + 3] = alpha;
            }
        }

        Marshal.Copy(colorBytes, 0, bits, colorBytes.Length);

        var mask = NativeMethods.CreateBitmap(size, size, 1, 1, maskBytes);
        if (mask == IntPtr.Zero)
        {
            NativeMethods.DeleteObject(color);
            return IntPtr.Zero;
        }

        var iconInfo = new NativeMethods.ICONINFO
        {
            fIcon = 1,
            hbmMask = mask,
            hbmColor = color,
        };

        var handle = NativeMethods.CreateIconIndirect(ref iconInfo);

        NativeMethods.DeleteObject(mask);
        NativeMethods.DeleteObject(color);

        return handle;
    }

    /// <summary>Expands one premultiplied colour channel back to straight alpha.</summary>
    private static byte Unpremultiply(byte value, byte alpha) =>
        (byte)Math.Min(255, ((value * 255) + (alpha / 2)) / alpha);

    private XPlatWindowState CurrentState()
    {
        if (NativeMethods.IsIconic(_hwnd))
        {
            return XPlatWindowState.Minimized;
        }

        return NativeMethods.IsZoomed(_hwnd) ? XPlatWindowState.Maximized : XPlatWindowState.Normal;
    }

    private IntPtr WindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            return HandleMessage(hwnd, message, wParam, lParam);
        }
        catch (Exception ex)
        {
            // An exception must never unwind into DispatchMessage: the native frame below this
            // delegate turns an escaping exception into a fail-fast with no hook at all.
            XPlatApplication.HandleNativeBoundary(ex);
            return IntPtr.Zero;
        }
    }

    private IntPtr HandleMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        // Messages raised while CreateWindowExW is still running arrive before _hwnd is assigned.
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
                OnSize(NativeMethods.LowWord(lParam), NativeMethods.HighWord(lParam), (uint)wParam);
                return IntPtr.Zero;

            case NativeMethods.WmDpiChanged:
                OnDpiChanged(hwnd, wParam, lParam);
                return IntPtr.Zero;

            case NativeMethods.WmGetMinMaxInfo:
                OnGetMinMaxInfo(lParam);
                return IntPtr.Zero;

            case NativeMethods.WmActivate:
                if (NativeMethods.LowWord(wParam) == 0)
                {
                    Deactivated?.Invoke(this, EventArgs.Empty);
                }
                else
                {
                    Activated?.Invoke(this, EventArgs.Empty);
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

                // Windows turns a right button release into WM_CONTEXTMENU inside the default
                // handler, so this message has to reach it or no menu is ever requested.
                return NativeMethods.DefWindowProcW(hwnd, message, wParam, lParam);

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

            case NativeMethods.WmSettingChange:
            case NativeMethods.WmThemeChanged:
                // The system appearance may have moved; RefreshTheme only acts on a real change.
                _host.RefreshTheme();
                return NativeMethods.DefWindowProcW(hwnd, message, wParam, lParam);

            case NativeMethods.WmContextMenu:
                // Let the default handler run when nothing opened a menu, so a missing menu still
                // behaves like an ordinary window instead of silently swallowing the message.
                return OnContextMenu(lParam) ? IntPtr.Zero : NativeMethods.DefWindowProcW(hwnd, message, wParam, lParam);

            case NativeMethods.WmClose:
                // The model decides: Closing may cancel, in which case the window stays up.
                CloseRequested?.Invoke(this, EventArgs.Empty);
                return IntPtr.Zero;

            case NativeMethods.WmDestroy:
                _hwnd = IntPtr.Zero;
                return IntPtr.Zero;
        }

        return NativeMethods.DefWindowProcW(hwnd, message, wParam, lParam);
    }

    /// <summary>
    /// Repaints whenever the palette is swapped - by the system appearance changing, or by the
    /// application assigning <see cref="XPlatTheme.Palette"/> - and keeps the title bar in step.
    /// </summary>
    private void OnThemeChanged(object? sender, EventArgs e)
    {
        NativeMethods.SetDarkTitleBar(_hwnd, XPlatTheme.IsDarkMode);
        Invalidate();
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

    private void OnSize(int width, int height, uint sizeType)
    {
        if (width <= 0 || height <= 0)
        {
            return;
        }

        EnsureBuffer(width, height);
        UpdateHostSize();
        Invalidate();
        RaiseStateChanged(sizeType);
    }

    private void RaiseStateChanged(uint sizeType)
    {
        var state = sizeType switch
        {
            NativeMethods.SizeMinimized => XPlatWindowState.Minimized,
            NativeMethods.SizeMaximized => XPlatWindowState.Maximized,
            _ => XPlatWindowState.Normal,
        };

        if (state == _state)
        {
            return;
        }

        _state = state;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnDpiChanged(IntPtr hwnd, IntPtr wParam, IntPtr lParam)
    {
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

        // The high word of wParam is the new DPI; the low word is the direction of the change.
        var scale = ScaleOf((uint)NativeMethods.HighWord(wParam));
        if (MathF.Abs(scale - _scale) < 0.001f)
        {
            return;
        }

        _scale = scale;

        // WM_SIZE above ran against the old scale; re-measure so the host sees logical pixels.
        UpdateHostSize();
        ApplyIcon();
        ScaleChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnGetMinMaxInfo(IntPtr lParam)
    {
        var info = Marshal.PtrToStructure<NativeMethods.MINMAXINFO>(lParam);

        if (_minimumClientSize.Width > 0f || _minimumClientSize.Height > 0f)
        {
            var minimum = OuterSizeFor(_minimumClientSize.Width, _minimumClientSize.Height);
            info.ptMinTrackSize.X = minimum.Width;
            info.ptMinTrackSize.Y = minimum.Height;
        }

        if (_maximumClientSize.Width > 0f || _maximumClientSize.Height > 0f)
        {
            var maximum = OuterSizeFor(_maximumClientSize.Width, _maximumClientSize.Height);
            info.ptMaxTrackSize.X = maximum.Width;
            info.ptMaxTrackSize.Y = maximum.Height;
        }

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

    /// <summary>
    /// Windows asks for a context menu through its own message, after the right button release or
    /// when the keyboard menu key is pressed, which is exactly the single entry point the UI
    /// library expects. The location is in screen pixels; the host works in logical client pixels.
    /// </summary>
    private bool OnContextMenu(IntPtr lParam)
    {
        var screen = new NativeMethods.POINT
        {
            X = NativeMethods.LowWord(lParam),
            Y = NativeMethods.HighWord(lParam),
        };

        // (-1, -1) means the keyboard asked for the menu. There is no per-control anchor to hang
        // it off here, so it opens at the pointer, which is where the user is looking.
        if (screen.X == -1 && screen.Y == -1 && !NativeMethods.GetCursorPos(out screen))
        {
            return false;
        }

        return _host.ProcessContextMenu(ScreenToLogical(screen));
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

        var size = new SKSize(client.Right / _scale, client.Bottom / _scale);
        _clientSize = size;

        // The host is the authority on whether anything actually changed: _clientSize can already
        // hold this value because the model assigned ClientSize before the window existed, and in
        // that case the host has still never been told and the control tree has never been laid out.
        if (size == _host.ClientSize)
        {
            return;
        }

        _host.SetClientSize(size.Width, size.Height);
        ClientSizeChanged?.Invoke(this, EventArgs.Empty);
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

    private SKPoint ScreenToLogical(NativeMethods.POINT point)
    {
        NativeMethods.ScreenToClient(_hwnd, ref point);
        return new SKPoint(point.X / _scale, point.Y / _scale);
    }

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

    private static float ScaleOf(uint dpi) => dpi > 0 ? dpi / 96f : 1f;

    private (int Width, int Height) OuterSizeFor(float clientWidth, float clientHeight)
    {
        var rect = new NativeMethods.RECT
        {
            Right = Math.Max((int)MathF.Round(clientWidth * _scale), 0),
            Bottom = Math.Max((int)MathF.Round(clientHeight * _scale), 0),
        };

        var dpi = (uint)MathF.Round(_scale * 96f);
        NativeMethods.AdjustWindowRectExForDpi(ref rect, StyleFor(), false, 0, dpi == 0 ? 96u : dpi);
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
