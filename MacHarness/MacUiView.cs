using AppKit;
using CoreGraphics;
using Foundation;
using SkiaSharp;
using XPlatUI;

namespace MacHarness;

/// <summary>
/// The macOS host for the XPlat UI library: a plain AppKit <see cref="NSView"/> that owns an
/// <see cref="XPlatUiHost"/>, rasterizes the control tree with SkiaSharp into a BGRA bitmap and
/// blits it into the view in <see cref="DrawRect"/>. Input arrives as AppKit events and is
/// forwarded to the host in the same form the Win32 and GTK harnesses use.
/// <para>
/// The bitmap route is deliberate: there is no SkiaSharp view for AppKit in SkiaSharp 4.x, and a
/// view that draws itself is both simpler and more predictable than a CALayer backed one.
/// </para>
/// </summary>
internal sealed class MacUiView : NSView
{
    /// <summary>Interval of the timer that drives time based UI behaviour (caret blinking).</summary>
    private const double TickIntervalSeconds = 0.25;

    /// <summary>Set <c>XPLATUI_DEBUG=1</c> to trace host work to stderr, which helps on odd setups.</summary>
    private static readonly bool DebugTrace =
        Environment.GetEnvironmentVariable("XPLATUI_DEBUG") is "1" or "true";

    private readonly XPlatUiHost _host = new();

    private NSTimer? _tickTimer;
    private NSTrackingArea? _trackingArea;
    private SKBitmap? _bitmap;
    private SKSurface? _skia;
    private CGDataProvider? _provider;
    private CGImage? _image;
    private CGColorSpace? _colorSpace;
    private int _width;
    private int _height;
    private int _scale;
    private bool _disposed;

    public MacUiView()
    {
        _host.Invalidated += OnHostInvalidated;
        _host.CursorChanged += OnHostCursorChanged;
        XPlatTheme.Changed += OnThemeChanged;
    }

    /// <summary>Raised once per tick, before the UI is ticked.</summary>
    public event EventHandler? Ticked;

    /// <summary>The platform independent UI host.</summary>
    public XPlatUiHost Host => _host;

    /// <summary>Sets the root of the control tree and lays it out to fill the view.</summary>
    public XPlatControl? Root
    {
        get => _host.Root;
        set
        {
            _host.Root = value;
            SyncHostSize();
        }
    }

    /// <summary>Top-left origin, y down: the coordinate space the UI library draws in.</summary>
    public override bool IsFlipped => true;

    /// <summary>Starts the tick timer that drives caret blinking and other time based behaviour.</summary>
    public void Start()
    {
        if (_tickTimer is not null)
        {
            return;
        }

        _tickTimer = NSTimer.CreateRepeatingScheduledTimer(TickIntervalSeconds, _ => OnTick());
    }

    /// <summary>
    /// Feeds one of the standard editing accelerators into the UI, so the focused control handles
    /// it exactly as it would a key press. The menu bar routes Command+X/C/V/A here.
    /// </summary>
    public void SendAccelerator(XPlatKey key) => _host.ProcessKeyDown(key, XPlatModifiers.Control);

    public override void DrawRect(CGRect dirtyRect)
    {
        var context = NSGraphicsContext.CurrentContext?.CGContext;
        var bounds = Bounds;
        if (context is null || bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        var scale = Math.Max((int)Math.Round(Window is null ? 1d : (double)Window.BackingScaleFactor), 1);
        SyncHostSize();
        EnsureBuffer((int)Math.Round(bounds.Width), (int)Math.Round(bounds.Height), scale);

        if (_skia is null || _image is null)
        {
            return;
        }

        Trace($"draw {_width}x{_height} at {_scale}x");

        var canvas = _skia.Canvas;
        canvas.Save();
        canvas.Scale(scale);
        _host.Paint(canvas);
        canvas.Restore();
        canvas.Flush();

        // The view is flipped, so the context is y-down; undo that for the image blit or the whole
        // UI comes out upside down.
        context.SaveState();
        context.TranslateCTM(0, bounds.Height);
        context.ScaleCTM(1, -1);
        context.DrawImage(new CGRect(0, 0, bounds.Width, bounds.Height), _image);
        context.RestoreState();
    }

    public override bool AcceptsFirstResponder() => true;

    /// <summary>Clicking an inactive window both activates it and clicks, like every other app.</summary>
    public override bool AcceptsFirstMouse(NSEvent? theEvent) => true;

    public override void ViewDidMoveToWindow()
    {
        base.ViewDidMoveToWindow();
        SyncHostSize();
        Window?.MakeFirstResponder(this);
        NeedsDisplay = true;
    }

    public override void UpdateTrackingAreas()
    {
        base.UpdateTrackingAreas();

        if (_trackingArea is not null)
        {
            RemoveTrackingArea(_trackingArea);
            _trackingArea.Dispose();
            _trackingArea = null;
        }

        _trackingArea = new NSTrackingArea(
            Bounds,
            NSTrackingAreaOptions.MouseEnteredAndExited |
            NSTrackingAreaOptions.MouseMoved |
            NSTrackingAreaOptions.ActiveInKeyWindow |
            NSTrackingAreaOptions.InVisibleRect |
            NSTrackingAreaOptions.EnabledDuringMouseDrag,
            this,
            null!);

        AddTrackingArea(_trackingArea);
    }

    public override void MouseDown(NSEvent theEvent)
    {
        Window?.MakeFirstResponder(this);
        _host.ProcessMouseDown(PointFrom(theEvent), XPlatMouseButtons.Left, ModifiersFrom(theEvent), ClickCountFrom(theEvent));
    }

    public override void MouseUp(NSEvent theEvent) =>
        _host.ProcessMouseUp(PointFrom(theEvent), XPlatMouseButtons.Left, ModifiersFrom(theEvent));

    public override void MouseDragged(NSEvent theEvent) => MoveTo(theEvent);

    public override void MouseMoved(NSEvent theEvent) => MoveTo(theEvent);

    public override void MouseEntered(NSEvent theEvent) => MoveTo(theEvent);

    public override void MouseExited(NSEvent theEvent) => _host.ProcessMouseLeave();

    public override void RightMouseDown(NSEvent theEvent) =>
        _host.ProcessMouseDown(PointFrom(theEvent), XPlatMouseButtons.Right, ModifiersFrom(theEvent), ClickCountFrom(theEvent));

    public override void RightMouseUp(NSEvent theEvent)
    {
        var point = PointFrom(theEvent);
        _host.ProcessMouseUp(point, XPlatMouseButtons.Right, ModifiersFrom(theEvent));

        // AppKit has no context menu message of its own here, so the release drives it, which is
        // also when Win32 asks for one.
        _host.ProcessContextMenu(point);
    }

    public override void OtherMouseDown(NSEvent theEvent) =>
        _host.ProcessMouseDown(PointFrom(theEvent), XPlatMouseButtons.Middle, ModifiersFrom(theEvent), ClickCountFrom(theEvent));

    public override void OtherMouseUp(NSEvent theEvent) =>
        _host.ProcessMouseUp(PointFrom(theEvent), XPlatMouseButtons.Middle, ModifiersFrom(theEvent));

    public override void OtherMouseDragged(NSEvent theEvent) => MoveTo(theEvent);

    public override void KeyDown(NSEvent theEvent)
    {
        var modifiers = ModifiersFrom(theEvent);
        var key = MacKeyMap.ToKey(theEvent.KeyCode);

        if (key != XPlatKey.None && _host.ProcessKeyDown(key, modifiers))
        {
            return;
        }

        // Command combinations belong to the menu bar. Anything the menu did not claim must not
        // fall through to text input, or Cmd+D would type a "d".
        if ((theEvent.ModifierFlags & NSEventModifierMask.CommandKeyMask) != 0)
        {
            return;
        }

        // Control combinations are the library's own shortcuts; they never produce text.
        if ((modifiers & XPlatModifiers.Control) != XPlatModifiers.None)
        {
            return;
        }

        // Everything else is text. The characters already carry the effect of Shift, the keyboard
        // layout and Option dead keys, so nothing has to be composed here.
        var text = MacKeyMap.ToText(theEvent);
        if (text.Length > 0)
        {
            _host.ProcessTextInput(text);
        }
    }

    public override void KeyUp(NSEvent theEvent)
    {
        var key = MacKeyMap.ToKey(theEvent.KeyCode);
        if (key != XPlatKey.None)
        {
            _host.ProcessKeyUp(key, ModifiersFrom(theEvent));
        }
    }

    /// <summary>
    /// The parameterless <c>Dispose</c> on <see cref="NSObject"/> is sealed, so the teardown hook is
    /// the protected overload every NSObject subclass overrides.
    /// </summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;

            XPlatTheme.Changed -= OnThemeChanged;
            _host.Invalidated -= OnHostInvalidated;
            _host.CursorChanged -= OnHostCursorChanged;

            _tickTimer?.Invalidate();
            _tickTimer?.Dispose();
            _tickTimer = null;

            if (_trackingArea is not null)
            {
                RemoveTrackingArea(_trackingArea);
                _trackingArea.Dispose();
                _trackingArea = null;
            }

            ReleaseBuffer();
        }

        base.Dispose(disposing);
    }

    private void MoveTo(NSEvent theEvent)
    {
        _host.ProcessMouseMove(PointFrom(theEvent));

        // AppKit re-applies the arrow cursor on every move unless the view claims otherwise, so
        // the cursor the control asked for has to be re-asserted here.
        ApplyCursor();
    }

    private void OnTick()
    {
        Ticked?.Invoke(this, EventArgs.Empty);
        _host.ProcessTick(TimeSpan.FromSeconds(TickIntervalSeconds));
    }

    private void OnHostInvalidated(object? sender, EventArgs e) => NeedsDisplay = true;

    /// <summary>Repaints whenever the palette is swapped, by the system or by the application.</summary>
    private void OnThemeChanged(object? sender, EventArgs e) => NeedsDisplay = true;

    private void OnHostCursorChanged(object? sender, EventArgs e) => ApplyCursor();

    private void ApplyCursor()
    {
        var cursor = _host.Cursor switch
        {
            XPlatCursor.Hand => NSCursor.PointingHandCursor,
            XPlatCursor.IBeam => NSCursor.IBeamCursor,
            XPlatCursor.Cross => NSCursor.CrosshairCursor,
            XPlatCursor.SizeAll => NSCursor.OpenHandCursor,
            XPlatCursor.SizeNS => NSCursor.ResizeUpDownCursor,
            XPlatCursor.SizeWE => NSCursor.ResizeLeftRightCursor,
            _ => NSCursor.ArrowCursor,
        };

        cursor.Set();
    }

    private SKPoint PointFrom(NSEvent theEvent)
    {
        var point = ConvertPointFromView(theEvent.LocationInWindow, null!);
        return new SKPoint((float)point.X, (float)point.Y);
    }

    private static XPlatModifiers ModifiersFrom(NSEvent theEvent) => MacKeyMap.ToModifiers(theEvent.ModifierFlags);

    private static int ClickCountFrom(NSEvent theEvent) => Math.Max((int)theEvent.ClickCount, 1);

    private void SyncHostSize()
    {
        var bounds = Bounds;
        if (bounds.Width > 0 && bounds.Height > 0)
        {
            _host.SetClientSize((float)bounds.Width, (float)bounds.Height);
        }
    }

    private void EnsureBuffer(int width, int height, int scale)
    {
        if (_bitmap is not null && _width == width && _height == height && _scale == scale)
        {
            return;
        }

        ReleaseBuffer();

        var pixelWidth = width * scale;
        var pixelHeight = height * scale;

        try
        {
            // Premultiplied BGRA, which is what CoreGraphics wants on a little endian machine with
            // ByteOrder32Little, so the same buffer can be handed to both Skia and CoreGraphics.
            var info = new SKImageInfo(pixelWidth, pixelHeight, SKColorType.Bgra8888, SKAlphaType.Premul);
            _bitmap = new SKBitmap(info);

            var pixels = _bitmap.GetPixels();
            _skia = SKSurface.Create(info, pixels, _bitmap.RowBytes);
            if (_skia is null)
            {
                ReleaseBuffer();
                return;
            }

            // The bitmap owns the memory and outlives the provider, so CoreGraphics must not free it.
            _provider = new CGDataProvider(pixels, _bitmap.ByteCount, ownBuffer: false);
            _colorSpace = CGColorSpace.CreateDeviceRGB();
            _image = new CGImage(
                pixelWidth,
                pixelHeight,
                8,
                32,
                _bitmap.RowBytes,
                _colorSpace,
                CGBitmapFlags.PremultipliedFirst | CGBitmapFlags.ByteOrder32Little,
                _provider,
                null,
                false,
                CGColorRenderingIntent.Default);

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
        // The image and its provider read the bitmap's memory, so they have to go first.
        _image?.Dispose();
        _image = null;

        _provider?.Dispose();
        _provider = null;

        _colorSpace?.Dispose();
        _colorSpace = null;

        _skia?.Dispose();
        _skia = null;

        _bitmap?.Dispose();
        _bitmap = null;

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
}