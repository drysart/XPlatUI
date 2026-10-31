using SkiaSharp;

namespace XPlatUI.Backend.Gtk4;

/// <summary>
/// The GTK4 host widget for the XPlat UI library. It owns an <see cref="XPlatUiHost"/>,
/// rasterizes the control tree with SkiaSharp into a Cairo image surface (premultiplied BGRA, which
/// is exactly <see cref="SKColorType.Bgra8888"/> with <see cref="SKAlphaType.Premul"/>, so no
/// swizzle is needed) and forwards mouse and keyboard input from GTK's event controllers. This is
/// the counterpart of the Win32 host and of the AppKit view: same duties, different native API.
/// </summary>
/// <remarks>
/// The view does not run a timer. Time based behaviour (caret blinking) is driven by
/// <see cref="XPlatApplication"/>'s single tick, which calls into every shown window's host.
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

    private readonly Gtk.DrawingArea _area;
    private readonly GLib.SourceFunc _redrawIdle;
    private readonly GLib.SourceFunc _notifyMetrics;

    private Cairo.ImageSurface? _cairo;
    private SKSurface? _skia;
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

        Host.Invalidated += OnHostInvalidated;
        Host.CursorChanged += OnHostCursorChanged;
        XPlatTheme.Changed += OnThemeChanged;

        AttachInput();
    }

    /// <summary>Raised after a paint when the widget's size or scale factor changed.</summary>
    public event EventHandler? MetricsChanged;

    /// <summary>The GTK widget that shows the UI tree.</summary>
    public Gtk.Widget Widget => _area;

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

        ReleaseBuffer();
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
            EnsureBuffer(width, height, scale);

            if (_skia is null || _cairo is null)
            {
                return;
            }

            Trace($"draw {width}x{height} at {scale}x into {_width}x{_height}");

            // Skia writes straight into Cairo's pixel buffer, so Cairo has to be told the buffer is
            // about to be scribbled on and then that it changed; without the mark it keeps using
            // stale cached data and the drawing area stays blank.
            _cairo.Flush();

            var canvas = _skia.Canvas;
            canvas.Save();
            canvas.Scale(scale);
            Host.Paint(canvas);
            canvas.Restore();
            canvas.Flush();

            _cairo.MarkDirty();

            // The Cairo context arrives already scaled by the surface scale factor, so undo it: one
            // pixel of the Skia surface has to land on one device pixel.
            cr.Save();
            cr.Scale(1.0 / scale, 1.0 / scale);
            cr.SetSourceSurface(_cairo, 0, 0);
            cr.Paint();
            cr.Restore();

            ReportMetrics(width, height, scale);
        }
        finally
        {
            _inDraw = false;
            _deferredRepaints = 0;
        }
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

    private unsafe void EnsureBuffer(int width, int height, int scale)
    {
        if (_cairo is not null && _width == width && _height == height && _scale == scale)
        {
            return;
        }

        ReleaseBuffer();

        var pixelWidth = width * scale;
        var pixelHeight = height * scale;

        try
        {
            // Cairo's Argb32 is premultiplied BGRA on little endian, which is Skia's Bgra8888/Premul.
            _cairo = new Cairo.ImageSurface(Cairo.Format.Argb32, pixelWidth, pixelHeight);

            var info = new SKImageInfo(pixelWidth, pixelHeight, SKColorType.Bgra8888, SKAlphaType.Premul);

            // The pixel buffer belongs to Cairo and never moves, so the pointer stays valid for as
            // long as the surface is alive.
            fixed (byte* pixels = _cairo.GetData())
            {
                _skia = SKSurface.Create(info, (IntPtr)pixels, _cairo.Stride);
            }

            if (_skia is null)
            {
                ReleaseBuffer();
                return;
            }

            _width = width;
            _height = height;
            _scale = scale;
        }
        catch (Exception ex)
        {
            Trace($"buffer {pixelWidth}x{pixelHeight} failed: {ex.GetType().Name}: {ex.Message}");
            ReleaseBuffer();
        }
    }

    private void ReleaseBuffer()
    {
        _skia?.Dispose();
        _skia = null;

        _cairo?.Dispose();
        _cairo = null;

        _width = 0;
        _height = 0;
        _scale = 0;
    }

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
