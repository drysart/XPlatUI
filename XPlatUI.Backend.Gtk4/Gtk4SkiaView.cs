using SkiaSharp;

namespace XPlatUI.Backend.Gtk4;

/// <summary>
/// The GTK4 host widget for the XPlat UI library. It owns an <see cref="XPlatUiHost"/>, rasterizes
/// the control tree with SkiaSharp into a premultiplied BGRA bitmap and shows it through a
/// <see cref="Gtk.Picture"/>, and forwards mouse and keyboard input from GTK's event controllers.
/// This is the counterpart of the Win32 host and of the AppKit view: same duties, different native
/// API.
/// </summary>
/// <remarks>
/// <para>
/// The frame is handed to GTK as a <see cref="Gdk.MemoryTexture"/> rather than painted into the
/// Cairo context of a <see cref="Gtk.DrawingArea"/>. GTK4 gives a drawing area a Cairo recording
/// surface, so compositing a bitmap into it means using the bitmap as a Cairo source, and Cairo
/// answers a mutable source with a full size snapshot. GTK keeps the render nodes those snapshots
/// end up in, so every repaint leaked one frame buffer. A texture copies its pixels once, on
/// construction, and owns the copy - which is also what lets the Skia surface be resized without
/// leaving the renderer pointing at freed memory.
/// </para>
/// <para>
/// A <see cref="Gtk.DrawingArea"/> is kept on top of the picture, transparent, because it is what
/// reports the widget's size and scale factor and what receives input. GTK only draws an overlay
/// child above the main child, so the picture has to be the main child.
/// </para>
/// <para>
/// The view does not run a timer. Time based behaviour (caret blinking) is driven by
/// <see cref="XPlatApplication"/>'s single tick, which calls into every shown window's host.
/// </para>
/// </remarks>
public sealed class Gtk4SkiaView : IDisposable
{
    /// <summary>
    /// A control that invalidates while it is being painted would otherwise keep the drawing area
    /// permanently dirty, so repaints requested during a paint are deferred and capped.
    /// </summary>
    private const int MaxDeferredRepaints = 8;

    private const long MemoryReportIntervalMs = 5000;

    /// <summary>Set <c>XPLATUI_DEBUG=1</c> to trace host work to stderr, which helps on odd setups.</summary>
    private static readonly bool DebugTrace =
        Environment.GetEnvironmentVariable("XPLATUI_DEBUG") is "1" or "true";

    private readonly Gtk.Overlay _root;
    private readonly Gtk.DrawingArea _area;
    private readonly Gtk.Picture _picture;
    private readonly GLib.SourceFunc _redrawIdle;
    private readonly GLib.SourceFunc _notifyMetrics;

    private SKSurface? _frame;
    private IntPtr _framePixels;
    private int _frameWidth;
    private int _frameHeight;
    private int _frameStride;

    private int _width;
    private int _height;
    private int _scale;

    private int _metricsWidth;
    private int _metricsHeight;
    private int _metricsScale;

    private bool _inDraw;
    private bool _redrawPending;
    private bool _metricsPending;
    private int _deferredRepaints;
    private bool _disposed;

    private long _frames;
    private long _reportedAt;
    private long _reportedFrames;

    public Gtk4SkiaView(XPlatUiHost host)
    {
        Host = host;

        // The delegates must stay rooted for as long as GLib may call them.
        _redrawIdle = OnRedrawIdle;
        _notifyMetrics = OnNotifyMetrics;

        _area = Gtk.DrawingArea.New();
        _area.SetHexpand(true);
        _area.SetVexpand(true);
        _area.SetFocusable(true);
        _area.SetCanFocus(true);
        _area.SetDrawFunc(OnDraw);

        _picture = Gtk.Picture.New();
        _picture.SetHexpand(true);
        _picture.SetVexpand(true);
        _picture.SetHalign(Gtk.Align.Fill);
        _picture.SetValign(Gtk.Align.Fill);
        _picture.SetContentFit(Gtk.ContentFit.Fill);
        _picture.SetCanShrink(true);
        _picture.SetFocusable(false);
        _picture.SetCanFocus(false);

        _root = Gtk.Overlay.New();
        _root.SetChild(_picture);
        _root.AddOverlay(_area);

        Host.Invalidated += OnHostInvalidated;
        Host.CursorChanged += OnHostCursorChanged;
        XPlatTheme.Changed += OnThemeChanged;

        AttachInput();
    }

    /// <summary>Raised after a paint when the widget's size or scale factor changed.</summary>
    public event EventHandler? MetricsChanged;

    /// <summary>The GTK widget that shows the UI tree.</summary>
    public Gtk.Widget Widget => _root;

    /// <summary>The platform independent UI host.</summary>
    public XPlatUiHost Host { get; }

    /// <summary>The widget's width in logical pixels.</summary>
    public int Width => _metricsWidth > 0 ? _metricsWidth : _area.GetWidth();

    /// <summary>The widget's height in logical pixels.</summary>
    public int Height => _metricsHeight > 0 ? _metricsHeight : _area.GetHeight();

    /// <summary>The integer device pixel ratio GTK reports for the widget's monitor.</summary>
    public int ScaleFactor
    {
        get
        {
            try
            {
                return Math.Max(_area.ScaleFactor, 1);
            }
            catch (Exception)
            {
                return 1;
            }
        }
    }

    /// <summary>Gives the drawing area keyboard focus, which is how key events reach the host.</summary>
    public void Focus() => _area.GrabFocus();

    /// <summary>Called periodically by the application backend; only reports under XPLATUI_DEBUG.</summary>
    public void ReportStatistics()
    {
        if (!DebugTrace)
        {
            return;
        }

        var now = Environment.TickCount64;
        _frames++;

        if (_reportedAt == 0)
        {
            _reportedAt = now;
            _reportedFrames = _frames;
            return;
        }

        if (now - _reportedAt < MemoryReportIntervalMs)
        {
            return;
        }

        var seconds = (now - _reportedAt) / 1000.0;
        var frames = _frames - _reportedFrames;
        var kib = (long)_width * _height * _scale * _scale * 4 / 1024;

        Console.Error.WriteLine(
            $"[xplat] gtk4 view {_width}x{_height}@{_scale} {frames / seconds:F1} fps, buffer {kib} KiB");

        _reportedAt = now;
        _reportedFrames = _frames;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        XPlatTheme.Changed -= OnThemeChanged;
        Host.Invalidated -= OnHostInvalidated;
        Host.CursorChanged -= OnHostCursorChanged;

        _picture.SetPaintable(null);
        _frame?.Dispose();
        _frame = null;
        _framePixels = IntPtr.Zero;
    }

    private void OnDraw(Gtk.DrawingArea area, Cairo.Context cr, int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            return;
        }

        _inDraw = true;

        try
        {
            Host.SetClientSize(width, height);

            var scale = Math.Max(area.ScaleFactor, 1);

            _width = width;
            _height = height;
            _scale = scale;

            Trace($"draw {width}x{height} at {scale}x");

            PaintFrame(width, height, scale);

            ReportMetrics(width, height, scale);
        }
        finally
        {
            _inDraw = false;
            _deferredRepaints = 0;
        }
    }

    /// <summary>
    /// Rasterizes the control tree at the widget's device resolution and hands the result to GTK.
    /// </summary>
    private unsafe void PaintFrame(int width, int height, int scale)
    {
        var pixelWidth = width * scale;
        var pixelHeight = height * scale;

        if (!EnsureFrame(pixelWidth, pixelHeight))
        {
            return;
        }

        var canvas = _frame!.Canvas;
        canvas.Save();
        canvas.Scale(scale);
        canvas.ClipRect(SKRect.Create(0, 0, width, height));
        Host.Paint(canvas);
        canvas.Restore();
        canvas.Flush();

        // The bytes must own the pixels. NewStatic only aliases them, which makes the texture read
        // memory that EnsureFrame frees on the next size change, and GTK's renderer still holds the
        // previous frame's node while it renders the new one - a resize then faults inside the GL
        // upload. New copies into GLib's own memory, so a texture outlives the surface it came from.
        var pixels = new Span<byte>((void*)_framePixels, _frameStride * pixelHeight);
        var bytes = GLib.Bytes.New(pixels);
        var texture = Gdk.MemoryTexture.New(
            pixelWidth,
            pixelHeight,
            Gdk.MemoryFormat.B8g8r8a8Premultiplied,
            bytes,
            (nuint)_frameStride);

        _picture.SetPaintable(texture);

        texture.Dispose();
        bytes.Dispose();
    }

    /// <summary>Allocates or reallocates the Skia surface that backs the picture, if needed.</summary>
    private bool EnsureFrame(int pixelWidth, int pixelHeight)
    {
        if (pixelWidth <= 0 || pixelHeight <= 0)
        {
            return false;
        }

        if (_frame is not null && _frameWidth == pixelWidth && _frameHeight == pixelHeight)
        {
            return true;
        }

        _frame?.Dispose();

        var info = new SKImageInfo(pixelWidth, pixelHeight, SKColorType.Bgra8888, SKAlphaType.Premul);
        _frame = SKSurface.Create(info);

        if (_frame is null)
        {
            Trace($"could not allocate a {pixelWidth}x{pixelHeight} frame");
            _framePixels = IntPtr.Zero;
            _frameWidth = 0;
            _frameHeight = 0;
            return false;
        }

        using var pixmap = _frame.PeekPixels();
        _framePixels = pixmap.GetPixels();
        _frameStride = pixmap.RowBytes;
        _frameWidth = pixelWidth;
        _frameHeight = pixelHeight;

        _frame.Canvas.Clear(SKColors.Transparent);

        return _framePixels != IntPtr.Zero;
    }

    private void ReportMetrics(int width, int height, int scale)
    {
        if (width == _metricsWidth && height == _metricsHeight && scale == _metricsScale)
        {
            return;
        }

        _metricsWidth = width;
        _metricsHeight = height;
        _metricsScale = scale;

        if (_metricsPending)
        {
            return;
        }

        // Raised from an idle rather than from inside the paint: the listeners resize the control
        // tree and raise model events, and neither is safe while Skia is mid-frame.
        _metricsPending = true;
        GLib.Functions.IdleAdd(0, _notifyMetrics);
    }

    private bool OnNotifyMetrics()
    {
        _metricsPending = false;

        if (!_disposed)
        {
            MetricsChanged?.Invoke(this, EventArgs.Empty);
        }

        return false;
    }

    private void AttachInput()
    {
        // Button 0 listens for every button, so one gesture covers left, right and middle.
        var click = Gtk.GestureClick.New();
        click.SetButton(0);
        click.OnPressed += OnPressed;
        click.OnReleased += OnReleased;
        _area.AddController(click);

        var motion = Gtk.EventControllerMotion.New();
        motion.OnMotion += OnMotion;
        motion.OnLeave += OnLeave;
        _area.AddController(motion);

        var key = Gtk.EventControllerKey.New();
        key.OnKeyPressed += OnKeyPressed;
        key.OnKeyReleased += OnKeyReleased;
        _area.AddController(key);
    }

    private void OnPressed(Gtk.GestureClick gesture, Gtk.GestureClick.PressedSignalArgs args)
    {
        var button = ToMouseButton(gesture.GetCurrentButton());
        if (button == XPlatMouseButtons.None)
        {
            return;
        }

        // Clicking anywhere inside the view takes keyboard focus, exactly like a Win32 window.
        _area.GrabFocus();
        Host.ProcessMouseDown(
            new SKPoint((float)args.X, (float)args.Y),
            button,
            Gtk4KeyMap.MapModifiers(gesture.GetCurrentEventState()),
            Math.Max(args.NPress, 1));
    }

    private void OnReleased(Gtk.GestureClick gesture, Gtk.GestureClick.ReleasedSignalArgs args)
    {
        var button = ToMouseButton(gesture.GetCurrentButton());
        if (button == XPlatMouseButtons.None)
        {
            return;
        }

        var point = new SKPoint((float)args.X, (float)args.Y);
        Host.ProcessMouseUp(point, button, Gtk4KeyMap.MapModifiers(gesture.GetCurrentEventState()));

        // GTK has no context menu message of its own for a drawing area, so the release drives it,
        // which is also when Win32 asks for one.
        if (button == XPlatMouseButtons.Right)
        {
            Host.ProcessContextMenu(point);
        }
    }

    private void OnMotion(Gtk.EventControllerMotion sender, Gtk.EventControllerMotion.MotionSignalArgs args) =>
        Host.ProcessMouseMove(new SKPoint((float)args.X, (float)args.Y));

    private void OnLeave(Gtk.EventControllerMotion sender, EventArgs args) => Host.ProcessMouseLeave();

    /// <summary>Forwards a key press; returns true when the UI consumed it.</summary>
    private bool OnKeyPressed(Gtk.EventControllerKey sender, Gtk.EventControllerKey.KeyPressedSignalArgs args)
    {
        var modifiers = Gtk4KeyMap.MapModifiers(args.State);
        var key = Gtk4KeyMap.Map(args.Keyval);

        if (key != XPlatKey.None && Host.ProcessKeyDown(key, modifiers))
        {
            return true;
        }

        var text = Gtk4KeyMap.MapText(args.Keyval, args.State);
        if (text is null)
        {
            return false;
        }

        Host.ProcessTextInput(text);
        return true;
    }

    private void OnKeyReleased(Gtk.EventControllerKey sender, Gtk.EventControllerKey.KeyReleasedSignalArgs args)
    {
        var key = Gtk4KeyMap.Map(args.Keyval);
        if (key != XPlatKey.None)
        {
            Host.ProcessKeyUp(key, Gtk4KeyMap.MapModifiers(args.State));
        }
    }

    private void OnHostInvalidated(object? sender, EventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        if (_inDraw)
        {
            if (_deferredRepaints >= MaxDeferredRepaints || _redrawPending)
            {
                return;
            }

            _deferredRepaints++;
            _redrawPending = true;
            GLib.Functions.IdleAdd(0, _redrawIdle);
            return;
        }

        _area.QueueDraw();
    }

    private bool OnRedrawIdle()
    {
        _redrawPending = false;

        if (!_disposed)
        {
            _area.QueueDraw();
        }

        return false;
    }

    private void OnHostCursorChanged(object? sender, EventArgs e) =>
        _area.SetCursorFromName(Host.Cursor switch
        {
            XPlatCursor.Hand => "pointer",
            XPlatCursor.IBeam => "text",
            XPlatCursor.Cross => "crosshair",
            XPlatCursor.SizeAll => "move",
            XPlatCursor.SizeNS => "ns-resize",
            XPlatCursor.SizeWE => "ew-resize",
            _ => "default",
        });

    /// <summary>Repaints whenever the palette is swapped, by the system or by the application.</summary>
    private void OnThemeChanged(object? sender, EventArgs e) => OnHostInvalidated(sender, e);

    private static void Trace(string message)
    {
        if (DebugTrace)
        {
            Console.Error.WriteLine($"[xplat] {message}");
        }
    }

    private static XPlatMouseButtons ToMouseButton(uint button) => button switch
    {
        1 => XPlatMouseButtons.Left,
        2 => XPlatMouseButtons.Middle,
        3 => XPlatMouseButtons.Right,
        _ => XPlatMouseButtons.None,
    };
}
