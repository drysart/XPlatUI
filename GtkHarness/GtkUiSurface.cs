using System.Text;
using SkiaSharp;
using XPlatUI;

namespace GtkHarness;

/// <summary>
/// The Linux host for the XPlat UI library, built directly on GTK4 so the harness needs no other
/// toolkit. It owns an <see cref="XPlatUiHost"/>, rasterizes the control tree with SkiaSharp into a
/// Cairo image surface (premultiplied BGRA, which is exactly <see cref="SKColorType.Bgra8888"/>
/// with <see cref="SKAlphaType.Premul"/>, so no swizzle is needed) and forwards mouse and keyboard
/// input from GTK's event controllers. This is the counterpart of the Win32 host in the Windows
/// harness: same duties, different native API.
/// </summary>
internal sealed class GtkUiSurface : IDisposable
{
    /// <summary>Interval of the timer that drives time based UI behaviour (caret blinking).</summary>
    private const int TickIntervalMs = 250;

    /// <summary>Set <c>XPLATUI_DEBUG=1</c> to trace host work to stderr, which helps on odd setups.</summary>
    private static readonly bool DebugTrace =
        Environment.GetEnvironmentVariable("XPLATUI_DEBUG") is "1" or "true";

    private readonly XPlatUiHost _host = new();
    private readonly Gtk.DrawingArea _area;
    private readonly GLib.SourceFunc _tick;

    private Cairo.ImageSurface? _cairo;
    private SKSurface? _skia;
    private int _width;
    private int _height;
    private int _scale;
    private uint _tickSource;
    private bool _disposed;

    public GtkUiSurface()
    {
        _area = Gtk.DrawingArea.New();
        _area.SetHexpand(true);
        _area.SetVexpand(true);
        _area.SetFocusable(true);
        _area.SetCanFocus(true);
        _area.SetDrawFunc(OnDraw);

        _host.Invalidated += OnHostInvalidated;
        _host.CursorChanged += OnHostCursorChanged;
        XPlatTheme.Changed += OnThemeChanged;

        AttachInput();

        // The delegate must stay rooted for as long as the GLib source points at it.
        _tick = OnTick;
    }

    /// <summary>The GTK widget that shows the UI tree.</summary>
    public Gtk.Widget Widget => _area;

    /// <summary>The platform independent UI host.</summary>
    public XPlatUiHost Host => _host;

    /// <summary>Sets the root of the control tree and lays it out to fill the widget.</summary>
    public XPlatControl? Root
    {
        get => _host.Root;
        set
        {
            _host.Root = value;
            SyncHostSize();
        }
    }

    /// <summary>Starts the tick timer that drives caret blinking and other time based behaviour.</summary>
    public void Start()
    {
        if (_tickSource == 0)
        {
            _tickSource = GLib.Functions.TimeoutAdd(0, TickIntervalMs, _tick);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        XPlatTheme.Changed -= OnThemeChanged;
        _host.Invalidated -= OnHostInvalidated;
        _host.CursorChanged -= OnHostCursorChanged;

        if (_tickSource != 0)
        {
            GLib.Functions.SourceRemove(_tickSource);
            _tickSource = 0;
        }

        ReleaseBuffer();
    }

    private void OnDraw(Gtk.DrawingArea area, Cairo.Context cr, int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            return;
        }

        SyncHostSize(width, height);

        var scale = Math.Max(area.ScaleFactor, 1);
        EnsureBuffer(width, height, scale);

        if (_skia is null || _cairo is null)
        {
            return;
        }

        Trace($"draw {width}x{height} at {scale}x into {_width}x{_height}");

        // Skia writes straight into Cairo's pixel buffer, so Cairo has to be told the buffer is
        // about to be scribbled on and then that it changed; without the mark it keeps using stale
        // cached data and the drawing area stays blank.
        _cairo.Flush();

        var canvas = _skia.Canvas;
        canvas.Save();
        canvas.Scale(scale);
        _host.Paint(canvas);
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

        // Clicking anywhere inside the surface takes keyboard focus, exactly like a Win32 window.
        _area.GrabFocus();
        _host.ProcessMouseDown(
            new SKPoint((float)args.X, (float)args.Y),
            button,
            ToModifiers(gesture.GetCurrentEventState()),
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
        _host.ProcessMouseUp(point, button, ToModifiers(gesture.GetCurrentEventState()));

        // GTK has no context menu message of its own for a drawing area, so the release drives it,
        // which is also when Win32 asks for one.
        if (button == XPlatMouseButtons.Right)
        {
            _host.ProcessContextMenu(point);
        }
    }

    private void OnMotion(Gtk.EventControllerMotion sender, Gtk.EventControllerMotion.MotionSignalArgs args) =>
        _host.ProcessMouseMove(new SKPoint((float)args.X, (float)args.Y));

    private void OnLeave(Gtk.EventControllerMotion sender, EventArgs args) => _host.ProcessMouseLeave();

    /// <summary>Forwards a key press; returns true when the UI consumed it.</summary>
    private bool OnKeyPressed(Gtk.EventControllerKey sender, Gtk.EventControllerKey.KeyPressedSignalArgs args)
    {
        var modifiers = ToModifiers(args.State);
        var key = GtkKeyMap.Map(args.Keyval);

        if (key != XPlatKey.None && _host.ProcessKeyDown(key, modifiers))
        {
            return true;
        }

        // Text input only when no accelerator modifier is held; the key value already carries the
        // effect of Shift and the keyboard layout.
        if ((modifiers & (XPlatModifiers.Control | XPlatModifiers.Alt)) != XPlatModifiers.None)
        {
            return false;
        }

        var codepoint = Gdk.Functions.KeyvalToUnicode(args.Keyval);
        if (codepoint is < 0x20 or 0x7F || !Rune.IsValid((int)codepoint))
        {
            return false;
        }

        _host.ProcessTextInput(char.ConvertFromUtf32((int)codepoint));
        return true;
    }

    private void OnKeyReleased(Gtk.EventControllerKey sender, Gtk.EventControllerKey.KeyReleasedSignalArgs args)
    {
        var key = GtkKeyMap.Map(args.Keyval);
        if (key != XPlatKey.None)
        {
            _host.ProcessKeyUp(key, ToModifiers(args.State));
        }
    }

    private void OnHostInvalidated(object? sender, EventArgs e) => _area.QueueDraw();

    private void OnHostCursorChanged(object? sender, EventArgs e) =>
        _area.SetCursorFromName(_host.Cursor switch
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
    private void OnThemeChanged(object? sender, EventArgs e) => _area.QueueDraw();

    private bool OnTick()
    {
        _host.ProcessTick(TimeSpan.FromMilliseconds(TickIntervalMs));
        return true;
    }

    private void SyncHostSize() => SyncHostSize(_area.GetWidth(), _area.GetHeight());

    private void SyncHostSize(int width, int height)
    {
        if (width > 0 && height > 0)
        {
            _host.SetClientSize(width, height);
        }
    }

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

    private static XPlatModifiers ToModifiers(Gdk.ModifierType state)
    {
        var modifiers = XPlatModifiers.None;

        if ((state & Gdk.ModifierType.ShiftMask) != 0)
        {
            modifiers |= XPlatModifiers.Shift;
        }

        if ((state & Gdk.ModifierType.ControlMask) != 0)
        {
            modifiers |= XPlatModifiers.Control;
        }

        if ((state & Gdk.ModifierType.AltMask) != 0)
        {
            modifiers |= XPlatModifiers.Alt;
        }

        return modifiers;
    }
}