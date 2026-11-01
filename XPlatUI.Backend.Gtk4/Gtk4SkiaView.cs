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
/// The view does not run a timer. Time based behaviour is the business of whichever control needs
/// it: a control that animates owns an <see cref="XPlatTimer"/> and only runs it while it matters.
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

    /// <summary>The effects a drop target offers the drag source.</summary>
    private const XPlatDragDropEffects DragEffects =
        XPlatDragDropEffects.Copy | XPlatDragDropEffects.Move;

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

    private Gtk.DropTarget? _fileDropTarget;
    private Gtk.DropTarget? _textDropTarget;
    private XPlatDataObject? _dragData;

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

        if (_fileDropTarget is not null)
        {
            _area.RemoveController(_fileDropTarget);
            _fileDropTarget = null;
        }

        if (_textDropTarget is not null)
        {
            _area.RemoveController(_textDropTarget);
            _textDropTarget = null;
        }

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

        AttachDrop();
    }

    /// <summary>Wires up the drop targets, which is how GTK4 delivers a drag from another app.</summary>
    /// <remarks>
    /// <para>
    /// One target per kind of drag, because the binding offers no way to add a second type to a
    /// target. Both are declared as text: a file list offers itself as the paths of the files it
    /// carries, and asking GTK for a <c>GdkFileList</c> instead fails, because a file drag also
    /// offers the portal transfer token that GDK then prefers, and that token can only be turned
    /// into files by a portal round trip that the synchronous read cannot wait for. The text form
    /// reads back immediately, one path per line.
    /// </para>
    /// <para>
    /// Both targets match a file drag, so each accepts only the drags it is meant for: the file
    /// target takes the drags that carry files and the text target turns those away rather than
    /// read a file drop as the paths of the files it carries.
    /// </para>
    /// <para>
    /// Preloading makes GTK fetch the data as soon as the drag is accepted, so the payload is in
    /// hand before the drag ends. The fetch is asynchronous: <c>accept</c> and <c>enter</c> run
    /// before it finishes and see no value at all, and the first <c>motion</c> is the earliest
    /// signal that can read the payload.
    /// </para>
    /// </remarks>
    private void AttachDrop()
    {
        _fileDropTarget = AddDropTarget(GObject.Type.String);
        _textDropTarget = AddDropTarget(GObject.Type.String);
    }

    /// <summary>Adds a drop target for one data type, and hooks the signals a drag raises on it.</summary>
    private Gtk.DropTarget AddDropTarget(GObject.Type type)
    {
        var target = Gtk.DropTarget.New(type, Gdk.DragAction.Copy | Gdk.DragAction.Move);
        target.SetPreload(true);
        target.OnAccept += OnDragAccept;
        target.OnEnter += OnDragEnter;
        target.OnMotion += OnDragMotion;
        target.OnLeave += OnDragLeaveSignal;
        target.OnDrop += OnDragDrop;
        _area.AddController(target);
        return target;
    }

    /// <summary>Decides whether a drag may be dropped.</summary>
    /// <remarks>
    /// The payload is not readable yet - the preload fetch is still running - so the formats on
    /// offer decide which target the drag belongs to. A drag that carries files goes to the file
    /// target and one that does not goes to the text target, which keeps a file drop from being
    /// read as the paths of the files it carries.
    /// </remarks>
    private bool OnDragAccept(Gtk.DropTarget sender, Gtk.DropTarget.AcceptSignalArgs args)
    {
        var formats = args.Drop?.GetFormats();
        if (formats is null)
        {
            return true;
        }

        var files = formats.ContainGtype(Gdk.FileList.GetGType()) ||
            formats.ContainMimeType("text/uri-list") ||
            formats.ContainMimeType("application/vnd.portal.filetransfer");

        return ReferenceEquals(sender, _fileDropTarget) ? files : !files;
    }

    /// <summary>Reports the drag crossing into the view, and what a drop there would do.</summary>
    /// <remarks>
    /// The payload usually arrives too late for this signal, so an unread drag answers with an
    /// optimistic copy. Answering with <see cref="Gdk.DragAction.None"/> would cancel the drag
    /// outright, and the first motion corrects the answer once the data is in hand.
    /// </remarks>
    private Gdk.DragAction OnDragEnter(Gtk.DropTarget sender, Gtk.DropTarget.EnterSignalArgs args)
    {
        try
        {
            _dragData ??= ReadDropValue(sender, sender.GetValue());
            if (_dragData is null)
            {
                return Gdk.DragAction.Copy;
            }

            var point = new SKPoint((float)args.X, (float)args.Y);
            return ToAction(Host.ProcessDragEnter(point, _dragData, DragEffects, CurrentModifiers(sender)));
        }
        catch (Exception ex)
        {
            XPlatApplication.HandleNativeBoundary(ex);
            return Gdk.DragAction.None;
        }
    }

    /// <summary>Reports the drag moving over the view, and what a drop there would do.</summary>
    private Gdk.DragAction OnDragMotion(Gtk.DropTarget sender, Gtk.DropTarget.MotionSignalArgs args)
    {
        try
        {
            _dragData ??= ReadDropValue(sender, sender.GetValue());
            if (_dragData is null)
            {
                return Gdk.DragAction.Copy;
            }

            var point = new SKPoint((float)args.X, (float)args.Y);
            return ToAction(Host.ProcessDragOver(point, _dragData, DragEffects, CurrentModifiers(sender)));
        }
        catch (Exception ex)
        {
            XPlatApplication.HandleNativeBoundary(ex);
            return Gdk.DragAction.None;
        }
    }

    /// <summary>Reports the drag leaving the view without being dropped.</summary>
    private void OnDragLeaveSignal(Gtk.DropTarget sender, EventArgs args)
    {
        if (_dragData is null)
        {
            return;
        }

        _dragData = null;
        Host.ProcessDragLeave();
    }

    /// <summary>Routes the drop into the control tree.</summary>
    private bool OnDragDrop(Gtk.DropTarget sender, Gtk.DropTarget.DropSignalArgs args)
    {
        try
        {
            var data = ReadDropValue(sender, args.Value) ?? _dragData;
            _dragData = null;

            if (data is null)
            {
                return false;
            }

            var point = new SKPoint((float)args.X, (float)args.Y);
            return Host.ProcessDragDrop(point, data, DragEffects, CurrentModifiers(sender)) !=
                XPlatDragDropEffects.None;
        }
        catch (Exception ex)
        {
            XPlatApplication.HandleNativeBoundary(ex);
            _dragData = null;
            return false;
        }
    }

    /// <summary>Turns the value GTK negotiated for a drag into the library's data object.</summary>
    /// <remarks>
    /// A file drag arrives as text, one path per line. A drag that offers files but names none that
    /// are local - a web address, say - reads back as its text.
    /// </remarks>
    private XPlatDataObject? ReadDropValue(Gtk.DropTarget sender, GObject.Value? value)
    {
        if (value is null || value.GetString() is not { Length: > 0 } text)
        {
            return null;
        }

        if (ReferenceEquals(sender, _fileDropTarget) && ParsePaths(text) is { Length: > 0 } paths)
        {
            XPlatDataObject fromFiles = new();
            fromFiles.SetFiles(paths);
            return fromFiles;
        }

        return XPlatDataObject.FromText(text);
    }

    /// <summary>Reads the local paths out of the text form of a file drag, one per line.</summary>
    /// <remarks>
    /// The text form holds a plain path per line, but a source that only offers
    /// <c>text/uri-list</c> hands over <c>file:</c> URIs instead, so both are accepted.
    /// </remarks>
    private static string[] ParsePaths(string text)
    {
        List<string> paths = [];
        foreach (var line in text.Split('\n'))
        {
            var entry = line.Trim();
            if (entry.Length == 0)
            {
                continue;
            }

            if (!Uri.TryCreate(entry, UriKind.Absolute, out var uri))
            {
                paths.Add(entry);
            }
            else if (uri.IsFile)
            {
                paths.Add(uri.LocalPath);
            }
        }

        return [.. paths];
    }

    /// <summary>Reads the modifier keys that are down right now, as the drag controller sees them.</summary>
    private static XPlatModifiers CurrentModifiers(Gtk.DropTarget sender) =>
        Gtk4KeyMap.MapModifiers(sender.GetCurrentEventState());

    /// <summary>Translates the effect the UI accepted into the action GTK shows for it.</summary>
    private static Gdk.DragAction ToAction(XPlatDragDropEffects effect)
    {
        if (effect.HasFlag(XPlatDragDropEffects.Move))
        {
            return Gdk.DragAction.Move;
        }

        return effect.HasFlag(XPlatDragDropEffects.Copy) ? Gdk.DragAction.Copy : Gdk.DragAction.None;
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
