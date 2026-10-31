using System.Runtime.InteropServices;
using SkiaSharp;
using XPlatUI;

namespace XPlatUI.Backend.Win32;

/// <summary>
/// Shows an <see cref="XPlatContextMenu"/> in its own borderless popup window, drawn with Skia in
/// the Windows 11 style: rounded corners, an acrylic backdrop and a pill behind the hovered row.
/// </summary>
/// <remarks>
/// <para>
/// This is deliberately not a child of, or a drawing pass over, the application window. The popup
/// is a separate top-level window <em>owned</em> by the application window, so Windows keeps it
/// above its owner, keeps it out of the taskbar and Alt-Tab, and closes it when the owner goes
/// away. Nothing about the owner's surface is touched.
/// </para>
/// <para>
/// The window is layered (<c>WS_EX_LAYERED</c> + <c>UpdateLayeredWindow</c>) so the per-pixel alpha
/// of the Skia surface reaches the compositor. That is what makes the rounded corners genuinely
/// transparent and lets the drop shadow fade out over whatever is behind, neither of which a
/// GDI-painted window can do: GDI cannot write an alpha channel, so a normal window would show
/// the corners as solid squares.
/// </para>
/// <para>
/// The acrylic backdrop is produced here rather than by DWM, because a layered window cannot borrow
/// a system backdrop material (<c>DWMWA_SYSTEMBACKDROP_TYPE</c> is ignored) and translucent-without
/// -blur would leave the text behind the menu legible through it. Instead the popup grabs a copy of
/// the screen region it is about to cover, blurs it with Skia, adds the acrylic noise, and paints
/// the translucent surface over that - the same three layers real acrylic is made of. The blur is
/// baked into the captured image because Skia ignores an image filter on the paint handed to
/// <c>DrawImage</c>. If the capture fails the surface simply falls back to opaque.
/// </para>
/// <para>
/// <see cref="Show"/> runs a nested message loop, so from the caller's point of view it behaves
/// exactly like <c>TrackPopupMenuEx</c>: it returns once the user has picked an item or dismissed
/// the menu.
/// </para>
/// </remarks>
internal sealed class Win32MenuWindow : IDisposable
{
    private const string ClassName = "XPlatUI.MenuWindow";

    /// <summary>Blur applied to the captured backdrop, in logical pixels so it scales with the DPI.</summary>
    private const float BlurSigma = 14f;

    /// <summary>
    /// Opacity of the menu surface over the backdrop. Windows 11's acrylic tint is deliberately
    /// strong enough that item text stays readable over whatever is behind it.
    /// </summary>
    private static float SurfaceOpacity => XPlatTheme.IsDarkMode ? 0.80f : 0.84f;

    /// <summary>Peak alpha of the acrylic noise, which is meant to be felt rather than seen.</summary>
    private const byte NoiseAlpha = 9;

    /// <summary>
    /// The popup pumping messages on this thread. A menu command may open another menu, so each
    /// window remembers the one it replaced and puts it back when it closes.
    /// </summary>
    [ThreadStatic]
    private static Win32MenuWindow? _active;

    private static NativeMethods.WndProc? _wndProc;
    private static IntPtr _moduleHandle;
    private static bool _classRegistered;

    private readonly NativeMethods.WndProc _instanceProc;

    private IntPtr _hwnd;
    private IntPtr _memoryDc;
    private IntPtr _dib;
    private IntPtr _dibBits;
    private IntPtr _previousBitmap;
    private int _bufferWidth;
    private int _bufferHeight;
    private SKSurface? _skia;

    private XPlatMenuLayout? _layout;
    private float _scale = 1f;
    private int _windowX;
    private int _windowY;
    private int _hovered = -1;
    private bool _trackingLeave;
    private bool _activated;
    private bool _finished;
    private XPlatMenuItem? _picked;

    /// <summary>
    /// What was on screen where the menu now sits, captured before the popup is shown. Blurred and
    /// laid under the translucent surface this is the acrylic backdrop; null when the capture failed
    /// or the display is not composited, in which case the menu paints solid instead.
    /// </summary>
    private SKImage? _backdrop;

    private Win32MenuWindow()
    {
        // The delegate has to stay rooted for as long as the window class can call it.
        _instanceProc = WindowProc;
    }

    /// <summary>
    /// Shows <paramref name="menu"/> at <paramref name="screen"/> and blocks until it is dismissed.
    /// </summary>
    /// <param name="owner">Window that owns the popup, and whose DPI and monitor it borrows.</param>
    /// <param name="screen">Top left of the menu, in physical screen pixels.</param>
    /// <param name="menu">The menu to show. Must not be empty.</param>
    /// <returns>
    /// <c>Shown</c> is false when the popup could not be created, in which case the caller should
    /// fall back to a classic menu. <c>Item</c> is the item the user picked, or null when the menu
    /// was dismissed without a choice.
    /// </returns>
    public static (bool Shown, XPlatMenuItem? Item) Show(IntPtr owner, NativeMethods.POINT screen, XPlatContextMenu menu)
    {
        ArgumentNullException.ThrowIfNull(menu);

        if (!EnsureClass())
        {
            return (false, null);
        }

        using var window = new Win32MenuWindow();
        return window.ShowCore(owner, screen, menu);
    }

    public void Dispose()
    {
        DestroyWindow();
        ReleaseBuffer();
        ReleaseBackdrop();
    }

    private static bool EnsureClass()
    {
        if (_classRegistered)
        {
            return true;
        }

        _moduleHandle = NativeMethods.GetModuleHandleW(null);
        _wndProc ??= WindowProc;

        var windowClass = new NativeMethods.WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<NativeMethods.WNDCLASSEXW>(),
            style = NativeMethods.CsHRedraw | NativeMethods.CsVRedraw,
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = _moduleHandle,
            hCursor = NativeMethods.LoadCursorW(IntPtr.Zero, new IntPtr(NativeMethods.IdcArrow)),
            lpszClassName = ClassName,
        };

        if (NativeMethods.RegisterClassExW(ref windowClass) == 0)
        {
            return false;
        }

        _classRegistered = true;
        return true;
    }

    private (bool Shown, XPlatMenuItem? Item) ShowCore(IntPtr owner, NativeMethods.POINT screen, XPlatContextMenu menu)
    {
        var dpi = owner != IntPtr.Zero ? NativeMethods.GetDpiForWindow(owner) : 96;
        _scale = dpi > 0 ? dpi / 96f : 1f;

        // Nothing else paints a shadow, so the surface reserves a margin for one. The window is
        // that much bigger than the menu and the margin is left transparent.
        _layout = XPlatMenuRenderer.Measure(menu, shadow: true);

        var surfaceWidth = Math.Max((int)MathF.Ceiling(_layout.SurfaceSize.Width * _scale), 1);
        var surfaceHeight = Math.Max((int)MathF.Ceiling(_layout.SurfaceSize.Height * _scale), 1);
        var shadow = Math.Max((int)MathF.Ceiling(_layout.ShadowExtent * _scale), 0);
        var width = surfaceWidth + (shadow * 2);
        var height = surfaceHeight + (shadow * 2);

        // Placement is about where the menu itself appears, so clamp the surface and then pull the
        // window back by its shadow margin.
        var (surfaceX, surfaceY) = PlacePopup(owner, screen, surfaceWidth, surfaceHeight);
        var x = surfaceX - shadow;
        var y = surfaceY - shadow;
        _windowX = x;
        _windowY = y;

        _hwnd = NativeMethods.CreateWindowExW(
            NativeMethods.WsExTopmost | NativeMethods.WsExLayered | NativeMethods.WsExToolWindow,
            ClassName,
            "XPlatUI.MenuWindow",
            NativeMethods.WsPopup | NativeMethods.WsClipChildren,
            x,
            y,
            width,
            height,
            owner,
            IntPtr.Zero,
            _moduleHandle,
            IntPtr.Zero);

        if (_hwnd == IntPtr.Zero)
        {
            return (false, null);
        }

        // Owned windows are not in the taskbar or Alt-Tab, but only an explicit owner relationship
        // keeps them above the window that spawned them.
        NativeMethods.SetWindowLongPtrW(_hwnd, NativeMethods.GwlpHwndParent, owner);

        EnsureBuffer(width, height);

        // Must happen before the popup is shown, or the capture would contain the menu itself.
        _backdrop = CaptureBackdrop(x, y, width, height);

        var previous = _active;
        _active = this;

        try
        {
            // Ownership matters for dismissal: a menu whose owner is not foreground keeps its loop
            // alive after the user clicks elsewhere, which looks like a hung application.
            NativeMethods.SetForegroundWindow(owner);
            NativeMethods.ShowWindow(_hwnd, NativeMethods.SwShowNoActivate);
            NativeMethods.SetWindowPos(
                _hwnd,
                NativeMethods.HwndTopMost,
                x,
                y,
                width,
                height,
                NativeMethods.SwpNoActivate | NativeMethods.SwpShowWindow);

            NativeMethods.SetForegroundWindow(_hwnd);
            NativeMethods.SetFocus(_hwnd);
            Present();

            PumpMessages();
        }
        finally
        {
            _active = previous;

            DestroyWindow();
            ReleaseBuffer();

            if (owner != IntPtr.Zero)
            {
                NativeMethods.SetForegroundWindow(owner);
                NativeMethods.SetFocus(owner);
            }
        }

        return (true, _picked);
    }

    /// <summary>
    /// Positions the popup at <paramref name="screen"/>, sliding it back inside the monitor's work
    /// area when it would hang off the right or bottom edge.
    /// </summary>
    private static (int X, int Y) PlacePopup(IntPtr owner, NativeMethods.POINT screen, int width, int height)
    {
        var work = NativeMethods.GetWorkArea(owner);

        var x = screen.X;
        var y = screen.Y;

        if (x + width > work.Right)
        {
            x = work.Right - width;
        }

        if (y + height > work.Bottom)
        {
            y = work.Bottom - height;
        }

        x = Math.Max(x, work.Left);
        y = Math.Max(y, work.Top);
        return (x, y);
    }

    /// <summary>
    /// The modal loop. It pumps every message on the thread, not just the popup's, so the owner
    /// window keeps painting and responding while the menu is up - the same arrangement
    /// <c>TrackPopupMenuEx</c> uses internally.
    /// </summary>
    private void PumpMessages()
    {
        while (!_finished)
        {
            var result = NativeMethods.GetMessageW(out var message, IntPtr.Zero, 0, 0);

            if (result == 0)
            {
                // A quit posted while the menu was open (the user closed the main window) belongs
                // to the outer loop, so hand it back rather than swallowing it.
                NativeMethods.PostQuitMessage((int)message.wParam);
                return;
            }

            if (result < 0)
            {
                return;
            }

            NativeMethods.TranslateMessage(ref message);
            NativeMethods.DispatchMessageW(ref message);
        }
    }

    private static IntPtr WindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (_active is { } window)
        {
            if (window._hwnd == IntPtr.Zero)
            {
                window._hwnd = hwnd;
            }

            if (window._hwnd == hwnd)
            {
                return window.HandleMessage(hwnd, message, wParam, lParam);
            }
        }

        return NativeMethods.DefWindowProcW(hwnd, message, wParam, lParam);
    }

    private IntPtr HandleMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        switch (message)
        {
            case NativeMethods.WmPaint:
            {
                var hdc = NativeMethods.BeginPaint(hwnd, out var paint);
                try
                {
                    // A layered window has no surface of its own to paint, so this only keeps the
                    // window valid; the visible frame comes from Present().
                    Present();
                }
                finally
                {
                    NativeMethods.EndPaint(hwnd, ref paint);
                }

                return IntPtr.Zero;
            }

            case NativeMethods.WmPrintClient:
                Present();
                return IntPtr.Zero;

            case NativeMethods.WmEraseBkgnd:
                // The Skia surface covers every pixel, including the transparent ones.
                return new IntPtr(1);

            case NativeMethods.WmMouseMove:
                UpdateHover(hwnd, NativeMethods.LowWord(lParam), NativeMethods.HighWord(lParam));
                return IntPtr.Zero;

            case NativeMethods.WmMouseLeave:
                _trackingLeave = false;
                SetHover(hwnd, -1);
                return IntPtr.Zero;

            case NativeMethods.WmLButtonDown:
                Pick(_layout?.HitTest(ToLogical(NativeMethods.LowWord(lParam), NativeMethods.HighWord(lParam))) ?? -1);
                return IntPtr.Zero;

            case NativeMethods.WmLButtonDblClk:
            case NativeMethods.WmRButtonDown:
            case NativeMethods.WmMButtonDown:
                // A second button press is a dismissal, exactly as it is for a native menu.
                Dismiss();
                return IntPtr.Zero;

            case NativeMethods.WmKeyDown:
                HandleKey(wParam);
                return IntPtr.Zero;

            case NativeMethods.WmActivate:
                if (NativeMethods.LowWord(wParam) == NativeMethods.WaInactive)
                {
                    // Ignore the deactivation that can arrive before the popup has ever been
                    // active, otherwise a menu opened while the app is in the background closes
                    // itself immediately.
                    if (_activated)
                    {
                        Dismiss();
                    }
                }
                else
                {
                    _activated = true;
                }

                return IntPtr.Zero;

            case NativeMethods.WmActivateApp:
                if (wParam == IntPtr.Zero)
                {
                    Dismiss();
                }

                return IntPtr.Zero;

            case NativeMethods.WmCaptureChanged:
                return IntPtr.Zero;

            case NativeMethods.WmSetCursor:
                NativeMethods.SetCursor(NativeMethods.LoadCursorW(IntPtr.Zero, new IntPtr(NativeMethods.IdcArrow)));
                return new IntPtr(1);

            case NativeMethods.WmNcHitTest:
            {
                // The shadow margin is transparent, so a click there has to fall through to
                // whatever is underneath instead of picking the nearest menu row.
                var x = NativeMethods.LowWord(lParam);
                var y = NativeMethods.HighWord(lParam);
                var point = new NativeMethods.POINT { X = x, Y = y };
                NativeMethods.ScreenToClient(hwnd, ref point);
                return new IntPtr(IsInsideSurface(point.X, point.Y) ? NativeMethods.HtClient : NativeMethods.HtTransparent);
            }
        }

        return NativeMethods.DefWindowProcW(hwnd, message, wParam, lParam);
    }

    private void HandleKey(IntPtr key)
    {
        if (_layout is null)
        {
            return;
        }

        switch ((int)key)
        {
            case NativeMethods.VkDown:
                SetHover(_hwnd, _layout.NextPickable(_hovered, 1));
                break;

            case NativeMethods.VkUp:
                SetHover(_hwnd, _layout.NextPickable(_hovered < 0 ? _layout.FirstPickable : _hovered, -1));
                break;

            case NativeMethods.VkHome:
                SetHover(_hwnd, _layout.FirstPickable);
                break;

            case NativeMethods.VkEnd:
                SetHover(_hwnd, _layout.NextPickable(_layout.FirstPickable, -1));
                break;

            case NativeMethods.VkReturn:
                Pick(_hovered >= 0 ? _hovered : _layout.FirstPickable);
                break;

            case NativeMethods.VkEscape:
            case NativeMethods.VkLeft:
            case NativeMethods.VkRight:
                Dismiss();
                break;
        }
    }

    private SKPoint ToLogical(int clientX, int clientY) => new(clientX / _scale, clientY / _scale);

    private void UpdateHover(IntPtr hwnd, int clientX, int clientY)
    {
        if (_layout is null)
        {
            return;
        }

        SetHover(hwnd, _layout.HitTest(ToLogical(clientX, clientY)));

        if (_trackingLeave)
        {
            return;
        }

        var track = new NativeMethods.TRACKMOUSEEVENT
        {
            cbSize = (uint)Marshal.SizeOf<NativeMethods.TRACKMOUSEEVENT>(),
            dwFlags = NativeMethods.TmeLeave,
            hwndTrack = hwnd,
        };

        _trackingLeave = NativeMethods.TrackMouseEvent(ref track);
    }

    private void SetHover(IntPtr hwnd, int index)
    {
        if (_hovered == index)
        {
            return;
        }

        _hovered = index;

        if (hwnd != IntPtr.Zero)
        {
            Present();
        }
    }

    private void Pick(int index)
    {
        if (_layout is null || index < 0 || index >= _layout.Rows.Count)
        {
            return;
        }

        var row = _layout.Rows[index];
        if (!row.IsPickable)
        {
            // Clicking a separator or a disabled item leaves the menu open, like the native one.
            return;
        }

        _picked = row.Item;
        _finished = true;
    }

    private void Dismiss()
    {
        _picked = null;
        _finished = true;
    }

    private void DestroyWindow()
    {
        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        NativeMethods.DestroyWindow(_hwnd);
        _hwnd = IntPtr.Zero;
    }

    /// <summary>
    /// Renders the menu and hands the buffer to the compositor, which is the only way a layered
    /// window ever shows anything.
    /// </summary>
    private void Present()
    {
        if (_skia is null || _memoryDc == IntPtr.Zero || _hwnd == IntPtr.Zero)
        {
            return;
        }

        Render();

        var screen = NativeMethods.GetDC(IntPtr.Zero);
        if (screen == IntPtr.Zero)
        {
            return;
        }

        try
        {
            var destination = new NativeMethods.POINT { X = _windowX, Y = _windowY };
            var source = new NativeMethods.POINT { X = 0, Y = 0 };
            var size = new NativeMethods.SIZE { cx = _bufferWidth, cy = _bufferHeight };
            var blend = new NativeMethods.BLENDFUNCTION
            {
                BlendOp = NativeMethods.AcSrcOver,
                SourceConstantAlpha = 255,
                AlphaFormat = NativeMethods.AcSrcAlpha,
            };

            NativeMethods.UpdateLayeredWindow(
                _hwnd,
                screen,
                ref destination,
                ref size,
                _memoryDc,
                ref source,
                0,
                ref blend,
                NativeMethods.UlwAlpha);
        }
        finally
        {
            NativeMethods.ReleaseDC(IntPtr.Zero, screen);
        }
    }

    /// <summary>True when a client-space point lands on the menu itself rather than its shadow.</summary>
    private bool IsInsideSurface(int clientX, int clientY)
    {
        if (_layout is null)
        {
            return false;
        }

        var bounds = _layout.SurfaceBounds;
        var left = bounds.Left * _scale;
        var top = bounds.Top * _scale;
        return clientX >= left
            && clientY >= top
            && clientX < left + (bounds.Width * _scale)
            && clientY < top + (bounds.Height * _scale);
    }

    private void Render()
    {
        if (_skia is null || _layout is null)
        {
            return;
        }

        var canvas = _skia.Canvas;
        canvas.Clear(SKColors.Transparent);
        canvas.Save();
        canvas.Scale(_scale);

        var acrylic = _backdrop is not null;
        if (acrylic)
        {
            DrawBackdrop(canvas);
        }

        XPlatMenuRenderer.Paint(canvas, _layout, _hovered, acrylic ? SurfaceOpacity : 1f);
        canvas.Restore();
        canvas.Flush();
    }

    /// <summary>
    /// Paints the acrylic backdrop under the surface: the screen behind the popup, blurred, plus the
    /// fine noise real acrylic carries so flat areas do not look like plain translucency.
    /// </summary>
    private void DrawBackdrop(SKCanvas canvas)
    {
        var bounds = _layout!.SurfaceBounds;
        var radius = XPlatMenuMetrics.Default.CornerRadius;

        canvas.Save();
        canvas.ClipRoundRect(new SKRoundRect(bounds, radius), SKClipOperation.Intersect, antialias: true);

        // The capture is in physical pixels; the canvas is already scaled to the DPI, so the image
        // has to be placed in logical units for it to land back on the pixels it came from.
        var destination = new SKRect(0f, 0f, _bufferWidth / _scale, _bufferHeight / _scale);
        canvas.DrawImage(_backdrop!, destination, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None), null);

        using (var noise = SKShader.CreatePerlinNoiseFractalNoise(0.45f, 0.45f, 2, 0f, SKPointI.Empty))
        using (var paint = new SKPaint { Shader = noise, Color = SKColors.White.WithAlpha(NoiseAlpha) })
        {
            canvas.DrawRect(bounds, paint);
        }

        canvas.Restore();
    }

    /// <summary>
    /// Copies the screen region the popup will cover and blurs it. Returns null when the copy fails,
    /// which simply leaves the menu opaque.
    /// </summary>
    private SKImage? CaptureBackdrop(int x, int y, int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            return null;
        }

        var screen = NativeMethods.GetDC(IntPtr.Zero);
        if (screen == IntPtr.Zero)
        {
            return null;
        }

        var memoryDc = IntPtr.Zero;
        var dib = IntPtr.Zero;
        var previous = IntPtr.Zero;

        try
        {
            memoryDc = NativeMethods.CreateCompatibleDC(screen);
            if (memoryDc == IntPtr.Zero)
            {
                return null;
            }

            var info = new NativeMethods.BITMAPINFO();
            info.bmiHeader.biSize = (uint)Marshal.SizeOf<NativeMethods.BITMAPINFOHEADER>();
            info.bmiHeader.biWidth = width;
            info.bmiHeader.biHeight = -height;
            info.bmiHeader.biPlanes = 1;
            info.bmiHeader.biBitCount = 32;
            info.bmiHeader.biCompression = NativeMethods.BiRgb;

            dib = NativeMethods.CreateDIBSection(IntPtr.Zero, ref info, NativeMethods.DibRgbColors, out var bits, IntPtr.Zero, 0);
            if (dib == IntPtr.Zero || bits == IntPtr.Zero)
            {
                return null;
            }

            previous = NativeMethods.SelectObject(memoryDc, dib);

            if (!NativeMethods.BitBlt(memoryDc, 0, 0, width, height, screen, x, y, NativeMethods.SrcCopy | NativeMethods.CaptureBlt))
            {
                return null;
            }

            // FromPixelCopy so the image survives the DIB being freed below.
            var imageInfo = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque);
            using var captured = SKImage.FromPixelCopy(imageInfo, bits, width * 4);
            return BlurBackdrop(captured);
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            if (memoryDc != IntPtr.Zero)
            {
                if (previous != IntPtr.Zero)
                {
                    NativeMethods.SelectObject(memoryDc, previous);
                }

                NativeMethods.DeleteDC(memoryDc);
            }

            if (dib != IntPtr.Zero)
            {
                NativeMethods.DeleteObject(dib);
            }

            NativeMethods.ReleaseDC(IntPtr.Zero, screen);
        }
    }

    /// <summary>
    /// Blurs a captured frame into a new image. The blur is baked into the pixels rather than left on
    /// a paint because Skia ignores an image filter on the paint handed to <c>DrawImage</c>.
    /// </summary>
    private SKImage? BlurBackdrop(SKImage source)
    {
        var info = new SKImageInfo(source.Width, source.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        if (surface is null)
        {
            return null;
        }

        var canvas = surface.Canvas;
        var bounds = new SKRect(0f, 0f, source.Width, source.Height);

        // Sigma is expressed in logical pixels, so it has to be scaled to the physical pixels of the
        // capture. Clamp rather than the default decal tiling: decal fades the alpha out at the
        // capture border, and the surface sits only a shadow-margin inside that border, so the fade
        // would reach the menu rim and let the sharp desktop show through it.
        using (var filter = SKImageFilter.CreateBlur(BlurSigma * _scale, BlurSigma * _scale, SKShaderTileMode.Clamp))
        using (var paint = new SKPaint { ImageFilter = filter })
        {
            canvas.SaveLayer(bounds, paint);
            canvas.DrawImage(source, bounds, new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None), null);
            canvas.Restore();
        }

        canvas.Flush();
        return surface.Snapshot();
    }

    private void ReleaseBackdrop()
    {
        _backdrop?.Dispose();
        _backdrop = null;
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
