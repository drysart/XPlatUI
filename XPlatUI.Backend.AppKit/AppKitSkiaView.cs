using AppKit;
using CoreGraphics;
using Foundation;
using ObjCRuntime;
using SkiaSharp;

namespace XPlatUI.Backend.AppKit;

/// <summary>
/// The view an <see cref="AppKitWindowBackend"/> draws into: an AppKit <see cref="NSView"/> that
/// rasterizes the control tree of the <see cref="XPlatUiHost"/> it is given with SkiaSharp into a
/// BGRA bitmap and shows it as the contents of its layer. Input arrives as AppKit events and is
/// forwarded to the host in the same form the Win32 and GTK backends use.
/// <para>
/// The bitmap route is deliberate: there is no SkiaSharp view for AppKit in SkiaSharp 4.x. What
/// the bitmap is presented *through* is not a matter of taste, though. Drawing it into the
/// window's own CoreGraphics context with <c>DrawImage</c> makes CoreGraphics keep a full-size
/// backing store per frame while the window is being resized, and a drag to near full screen on a
/// Retina display then peaked at 1.8 GB of physical footprint - the same drag through a layer's
/// <c>contents</c> stays near 150 MB, and a plain view that fills itself with a solid colour stays
/// at 123 MB. So the view is layer backed and overrides <see cref="WantsUpdateLayer"/> rather than
/// <see cref="DrawRect"/>: AppKit then asks for a layer update and never allocates a backing store
/// of its own.
/// </para>
/// </summary>
internal sealed class AppKitSkiaView : NSView
{
    /// <summary>Set <c>XPLATUI_DEBUG=1</c> to trace host work to stderr, which helps on odd setups.</summary>
    private static readonly bool DebugTrace =
        Environment.GetEnvironmentVariable("XPLATUI_DEBUG") is "1" or "true";

    /// <summary>How many frames in a row may be queued by an invalidation raised while painting.</summary>
    private const int MaxDeferredRepaints = 8;

    /// <summary>A drawing buffer is only given back once the window needs under half its area.</summary>
    private const int OversizedAreaFactor = 2;

    /// <summary>How long the window has to hold its size before an oversized buffer is given back.</summary>
    private const long ShrinkDelayMs = 750;

    /// <summary>How often the tracing memory report is printed.</summary>
    private const long MemoryReportIntervalMs = 5000;

    private const long Mib = 1024 * 1024;

    /// <summary>The pasteboard type of a dragged file.</summary>
    private const string FileUrlType = "public.file-url";

    /// <summary>The pasteboard type of dragged plain text.</summary>
    private const string PlainTextType = "public.utf8-plain-text";

    /// <summary>The effects a drop offers the drag source.</summary>
    private const XPlatDragDropEffects DragEffects =
        XPlatDragDropEffects.Copy | XPlatDragDropEffects.Move;

    /// <summary>The pasteboard types this view is registered to receive as a drag destination.</summary>
    private static readonly string[] DragTypes = [FileUrlType, PlainTextType];

    private readonly XPlatUiHost _host;

    private NSTrackingArea? _trackingArea;
    private SKBitmap? _bitmap;
    private SKSurface? _skia;
    private CGDataProvider? _provider;
    private CGColorSpace? _colorSpace;
    private CGImage? _presented;
    private string? _diagnostic;
    private string? _bufferError;
    private int _width;
    private int _height;
    private int _scale;
    private long _lastSizeChange;
    private bool _disposed;

    private bool _inDraw;
    private bool _invalidationDuringDraw;
    private int _deferredRepaints;
    private XPlatDataObject? _dragData;

    private long _frames;
    private long _buffersAllocated;
    private long _reportedAt;
    private long _reportedFrames;

    /// <summary>Creates a view that draws and drives <paramref name="host"/>.</summary>
    public AppKitSkiaView(XPlatUiHost host)
    {
        ArgumentNullException.ThrowIfNull(host);

        _host = host;
        _host.Invalidated += OnHostInvalidated;
        _host.CursorChanged += OnHostCursorChanged;
        XPlatTheme.Changed += OnThemeChanged;
        _reportedAt = Environment.TickCount64;

        // Layer backed from the start, so AppKit never creates a backing store for this view and
        // the first frame already goes through the layer instead of the window's context.
        WantsLayer = true;

        // AppKit only sends a drag here when the pasteboard offers one of these types.
        RegisterForDraggedTypes(DragTypes);
    }

    /// <summary>
    /// A layer backed view is not marked dirty when it is resized: AppKit's default is to leave the
    /// layer showing what it already has and let the compositor stretch it to the new bounds. That
    /// is why the layer path has to ask for a frame itself, or a whole drag shows the frame from
    /// before the drag started, scaled, and the window only paints at its new size once the mouse
    /// is released.
    /// </summary>
    public override void SetFrameSize(CGSize newSize)
    {
        var previous = Frame.Size;
        base.SetFrameSize(newSize);

        if (previous.Width != newSize.Width || previous.Height != newSize.Height)
        {
            NeedsDisplay = true;
        }
    }

    /// <summary>The platform independent UI host this view paints.</summary>
    public XPlatUiHost Host => _host;

    /// <summary>Top-left origin, y down: the coordinate space the UI library draws in.</summary>
    public override bool IsFlipped => true;

    /// <summary>
    /// The view paints its layer's contents itself, so AppKit must call <see cref="UpdateLayer"/>
    /// instead of <see cref="DrawRect"/>; drawing in <c>DrawRect</c> would give the view a backing
    /// store of its own, which is the allocation this exists to avoid.
    /// </summary>
    public override bool WantsUpdateLayer => true;

    public override void UpdateLayer()
    {
        _frames++;

        // AppKit does not re-enter a draw, and doing so would paint the same buffer twice, so a
        // nested call can only mean something asked for a frame while painting.
        if (_inDraw)
        {
            Trace("draw skipped: already drawing");
            return;
        }

        _inDraw = true;
        try
        {
            DrawFrame();
        }
        finally
        {
            _inDraw = false;
        }

        // A control that invalidates while it paints needs one more frame to show that state, but
        // one that does it every frame would spin the run loop as fast as the machine allows and
        // grow memory with it, so the deferral is bounded and says so when it gives up.
        if (!_invalidationDuringDraw)
        {
            _deferredRepaints = 0;
            return;
        }

        _invalidationDuringDraw = false;
        _deferredRepaints++;

        if (_deferredRepaints > MaxDeferredRepaints)
        {
            if (_deferredRepaints == MaxDeferredRepaints + 1)
            {
                Trace($"repaint loop: the UI asked for a frame from inside {_deferredRepaints} draws in a row; " +
                      "repainting only when something else asks from now on");
            }

            return;
        }

        NeedsDisplay = true;
    }

    private void DrawFrame()
    {
        var bounds = Bounds;

        // This should not happen, and it looks exactly like "the window is empty" from the
        // outside, so it says so when tracing is on.
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            Trace($"draw skipped: empty bounds {bounds.Width}x{bounds.Height}");
            return;
        }

        var scale = Math.Max((int)Math.Round(Window is null ? 1d : (double)Window.BackingScaleFactor), 1);
        SyncHostSize();

        // Resizing the host lays its children out, which asks for a repaint - but this frame is
        // about to paint at exactly the size that was just set, so that invalidation is already
        // satisfied. Asking for another frame on its behalf would double the work of every step of
        // a live resize.
        _invalidationDuringDraw = false;

        EnsureBuffer((int)Math.Round(bounds.Width), (int)Math.Round(bounds.Height), scale);

        if (_skia is null || _bitmap is null || _provider is null || _colorSpace is null)
        {
            DrawDiagnostic(_bufferError ?? "the SkiaSharp drawing buffer could not be created");
            return;
        }

        if (DebugTrace && (_frames <= 3 || _frames % 100 == 0))
        {
            Trace($"draw #{_frames} {_width}x{_height} at {_scale}x");
        }

        try
        {
            var canvas = _skia.Canvas;
            canvas.Save();
            canvas.Scale(scale);

            // The buffer is larger than the window so that a drag does not reallocate it, and the
            // clear that starts every frame honours the clip, so without this each frame would also
            // wipe all the headroom. This is after the scale because the clip is in the same
            // logical units the UI paints in, not in pixels.
            canvas.ClipRect(new SKRect(0, 0, _width, _height));
            _host.Paint(canvas);
            canvas.Restore();
            canvas.Flush();
        }
        catch (Exception ex)
        {
            DrawDiagnostic($"painting the UI failed: {ex.GetType().Name}: {ex.Message}");
            return;
        }

        Present();

        // A window that painted is a window whose last complaint is out of date.
        _diagnostic = null;
    }

    /// <summary>
    /// Hands the pixels that were just painted to the layer. CoreGraphics treats a
    /// <see cref="CGImage"/> as immutable and is free to keep the decode it made of the provider's
    /// memory, so one cached image would pin the window to whatever the buffer held when it was
    /// first drawn - and the first frame is drawn before the control tree exists, which is an
    /// empty window. A fresh image over the pixels that were just painted is what makes each frame
    /// show the current UI; the bitmap, its Skia surface and the data provider over its pixels
    /// stay cached, so a frame allocates nothing but the image itself.
    /// </summary>
    private void Present()
    {
        if (_bitmap is null || _provider is null || _colorSpace is null || Layer is not { } layer)
        {
            return;
        }

        CGImage? image = null;
        try
        {
            image = new CGImage(
                _width * _scale,
                _height * _scale,
                8,
                32,
                _bitmap.RowBytes,
                _colorSpace,
                CGBitmapFlags.PremultipliedFirst | CGBitmapFlags.ByteOrder32Little,
                _provider,
                null,
                false,
                CGColorRenderingIntent.Default);

            if (((INativeObject)image).Handle == IntPtr.Zero)
            {
                image.Dispose();
                image = null;
                Trace("present failed: CoreGraphics built no image from the SkiaSharp bitmap");
                return;
            }

            // Resize rather than the default centring, because the image is exactly the size of the
            // window and only the headroom around it has to be cropped off; the scale is what
            // keeps a Retina window from being shown at half size.
            layer.ContentsGravity = CoreAnimation.CALayer.GravityResize;
            layer.ContentsScale = _scale;
            layer.Contents = image;

            // The layer retained the image, so the view's own reference goes; dropping it here
            // rather than at the next frame keeps exactly one frame's image alive.
            _presented?.Dispose();
            _presented = image;
            image = null;
        }
        catch (Exception ex)
        {
            image?.Dispose();
            Trace($"present failed: {ex.GetType().Name}: {ex.Message}");
        }
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
        var key = AppKitKeyMap.ToKey(theEvent.KeyCode);

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
        var text = AppKitKeyMap.ToText(theEvent);
        if (text.Length > 0)
        {
            _host.ProcessTextInput(text);
        }
    }

    public override void KeyUp(NSEvent theEvent)
    {
        var key = AppKitKeyMap.ToKey(theEvent.KeyCode);
        if (key != XPlatKey.None)
        {
            _host.ProcessKeyUp(key, ModifiersFrom(theEvent));
        }
    }

    /// <summary>Called when a drag enters the view, and says whether the view would take the drop.</summary>
    public override NSDragOperation DraggingEntered(INSDraggingInfo sender)
    {
        var data = ReadDragData(sender);
        if (data is null)
        {
            _dragData = null;
            return NSDragOperation.None;
        }

        _dragData = data;
        return ToOperation(_host.ProcessDragEnter(PointFrom(sender), data, DragEffects, DragModifiers()));
    }

    /// <summary>Called as a drag moves over the view, and says what a drop there would do.</summary>
    public override NSDragOperation DraggingUpdated(INSDraggingInfo sender)
    {
        if (_dragData is null)
        {
            return NSDragOperation.None;
        }

        return ToOperation(_host.ProcessDragOver(PointFrom(sender), _dragData, DragEffects, DragModifiers()));
    }

    /// <summary>Called when the drag leaves the view without being dropped.</summary>
    public override void DraggingExited(INSDraggingInfo? sender)
    {
        if (_dragData is null)
        {
            return;
        }

        _dragData = null;
        _host.ProcessDragLeave();
    }

    /// <summary>Called when the drag is released over the view; true says the drop will be taken.</summary>
    public override bool PrepareForDragOperation(INSDraggingInfo sender) => _dragData is not null;

    /// <summary>Called when the drag is released over the view, and routes the drop.</summary>
    public override bool PerformDragOperation(INSDraggingInfo sender)
    {
        var data = ReadDragData(sender) ?? _dragData;
        _dragData = null;

        if (data is null)
        {
            return false;
        }

        return _host.ProcessDragDrop(PointFrom(sender), data, DragEffects, DragModifiers()) !=
            XPlatDragDropEffects.None;
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

            if (_trackingArea is not null)
            {
                RemoveTrackingArea(_trackingArea);
                _trackingArea.Dispose();
                _trackingArea = null;
            }

            ReleaseBuffer();

            _colorSpace?.Dispose();
            _colorSpace = null;
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

    private void OnHostInvalidated(object? sender, EventArgs e)
    {
        if (_inDraw)
        {
            _invalidationDuringDraw = true;
            return;
        }

        NeedsDisplay = true;
    }

    /// <summary>
    /// Prints frame rate and memory use every few seconds when tracing is on. A UI that grows
    /// without bound is either drawing far more often than it should (watch the frame rate) or
    /// holding on to something for every frame (watch private memory against the frame count).
    /// The application backend calls this from the timer that polls the system appearance.
    /// </summary>
    internal void ReportStatistics()
    {
        if (!DebugTrace)
        {
            return;
        }

        var now = Environment.TickCount64;
        var since = now - _reportedAt;
        if (since < MemoryReportIntervalMs)
        {
            return;
        }

        var frames = _frames - _reportedFrames;
        _reportedAt = now;
        _reportedFrames = _frames;

        using var process = System.Diagnostics.Process.GetCurrentProcess();
        Trace(
            $"stats: {frames * 1000d / since:F1} fps, {_frames} frames, {_buffersAllocated} buffers, " +
            $"managed {GC.GetTotalMemory(false) / Mib} MB, allocated {GC.GetTotalAllocatedBytes() / Mib} MB, " +
            $"resident {process.WorkingSet64 / Mib} MB, " +
            $"buffer {_width}x{_height} at {_scale}x");
    }

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

    private SKPoint PointFrom(INSDraggingInfo sender)
    {
        var point = ConvertPointFromView(sender.DraggingLocation, null!);
        return new SKPoint((float)point.X, (float)point.Y);
    }

    /// <summary>
    /// Reads whatever the dragged pasteboard offers that the library understands. Files come first
    /// because a file drag also carries its name as text, and the file is the more useful of the two.
    /// </summary>
    /// <remarks>
    /// Only file URLs count as files. A web URL is also an <see cref="NSUrl"/>, and its
    /// <see cref="NSUrl.Path"/> is the URL's path rather than anything on disk, so an image dragged
    /// out of a browser would otherwise arrive as a "file" called <c>/images/logo.png</c>.
    /// </remarks>
    private static XPlatDataObject? ReadDragData(INSDraggingInfo sender)
    {
        try
        {
            var pasteboard = sender.DraggingPasteboard;
            var objects = pasteboard.ReadObjectsForClasses([new Class(typeof(NSUrl))], null!);
            string[] paths = objects is null
                ? []
                :
                [
                    .. objects
                        .OfType<NSUrl>()
                        .Where(url => url.IsFileUrl)
                        .Select(url => url.Path ?? string.Empty)
                        .Where(path => path.Length > 0),
                ];

            if (paths.Length > 0)
            {
                XPlatDataObject fromFiles = new();
                fromFiles.SetFiles(paths);
                return fromFiles;
            }

            return pasteboard.GetStringForType(PlainTextType) is { Length: > 0 } text
                ? XPlatDataObject.FromText(text)
                : null;
        }
        catch (Exception ex)
        {
            Trace($"reading the dragged data failed: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Reads the modifier keys that are down right now, which AppKit keeps on the event.</summary>
    private static XPlatModifiers DragModifiers() =>
        NSApplication.SharedApplication.CurrentEvent is { } current
            ? AppKitKeyMap.ToModifiers(current.ModifierFlags)
            : XPlatModifiers.None;

    /// <summary>Translates the effect the UI accepted into the operation AppKit shows for it.</summary>
    private static NSDragOperation ToOperation(XPlatDragDropEffects effect)
    {
        if (effect.HasFlag(XPlatDragDropEffects.Move))
        {
            return NSDragOperation.Move;
        }

        return effect.HasFlag(XPlatDragDropEffects.Copy) ? NSDragOperation.Copy : NSDragOperation.None;
    }

    private static XPlatModifiers ModifiersFrom(NSEvent theEvent) => AppKitKeyMap.ToModifiers(theEvent.ModifierFlags);

    private static int ClickCountFrom(NSEvent theEvent) => Math.Max((int)theEvent.ClickCount, 1);

    internal void SyncHostSize()
    {
        var bounds = Bounds;
        if (bounds.Width > 0 && bounds.Height > 0)
        {
            _host.SetClientSize((float)bounds.Width, (float)bounds.Height);
        }
    }

    private void EnsureBuffer(int width, int height, int scale)
    {
        var now = Environment.TickCount64;
        if (width != _width || height != _height || scale != _scale)
        {
            _lastSizeChange = now;
        }

        var pixelWidth = width * scale;
        var pixelHeight = height * scale;

        // Dragging a window edge asks for a frame at every intermediate size, and a fresh
        // multi-megabyte bitmap per frame is native memory the garbage collector never sees the
        // pressure from: one drag across a Retina screen reallocated twenty times, and the
        // allocator was in no hurry to give any of it back. So the buffer is made with headroom
        // and a whole drag fits in the allocation it already has. Giving an oversized buffer back
        // waits until the window has held its size for a moment, because an in-and-out drag is
        // otherwise one reallocation per step, and every one of them leaves a freed multi-megabyte
        // block in the allocator's hands.
        if (_bitmap is not null && _scale == scale &&
            _bitmap.Width >= pixelWidth && _bitmap.Height >= pixelHeight)
        {
            var oversized = (long)_bitmap.Width * _bitmap.Height >
                            (long)pixelWidth * pixelHeight * OversizedAreaFactor;

            if (!oversized || now - _lastSizeChange < ShrinkDelayMs)
            {
                _width = width;
                _height = height;
                return;
            }
        }

        // A quarter again the size that is actually needed, so the next few sizes of the same drag
        // fit the buffer that was just made. It is measured from the needed size rather than from
        // the buffer being replaced, which is what keeps a shrinking window from growing its buffer
        // and bounds the buffer to 1.25x the window instead of letting it compound.
        var allocWidth = pixelWidth + pixelWidth / 4;
        var allocHeight = pixelHeight + pixelHeight / 4;

        ReleaseBuffer();

        if (DebugTrace && _buffersAllocated > 0)
        {
            Trace($"buffer reallocated for {width}x{height} at {scale}x " +
                  $"(#{_buffersAllocated + 1}, {allocWidth}x{allocHeight} pixels)");
        }

        try
        {
            // Premultiplied BGRA, which is what CoreGraphics wants on a little endian machine with
            // ByteOrder32Little, so the same buffer can be handed to both Skia and CoreGraphics.
            var info = new SKImageInfo(allocWidth, allocHeight, SKColorType.Bgra8888, SKAlphaType.Premul);
            _bitmap = new SKBitmap(info);

            var pixels = _bitmap.GetPixels();
            if (pixels == IntPtr.Zero)
            {
                throw new InvalidOperationException($"SkiaSharp allocated no pixels for {allocWidth}x{allocHeight}.");
            }

            _skia = SKSurface.Create(info, pixels, _bitmap.RowBytes)
                ?? throw new InvalidOperationException($"SkiaSharp could not wrap {allocWidth}x{allocHeight} pixels in a surface.");

            // The (memory, size, bool) overload reads that bool as "owns the buffer" and, when it is
            // false, copies the whole block - which would memcpy every pixel of every frame, about
            // 16 MB on a Retina window. The callback overload wraps the pixels where they are; the
            // callback does nothing because SkiaSharp owns them and outlives the provider.
            _provider = new CGDataProvider(pixels, _bitmap.ByteCount, static _ => { });

            _colorSpace ??= CGColorSpace.CreateDeviceRGB()
                ?? throw new InvalidOperationException("CoreGraphics has no device RGB colour space.");

            _width = width;
            _height = height;
            _scale = scale;
            _bufferError = null;
            _buffersAllocated++;
        }
        catch (Exception ex)
        {
            // Failing here silently is a window that never draws anything, which is the hardest
            // kind of bug to report, so the reason is kept for DrawRect to put on screen.
            _bufferError = $"{ex.GetType().Name}: {ex.Message}";
            Console.Error.WriteLine($"[xplat] {pixelWidth}x{pixelHeight} drawing buffer failed: {_bufferError}");
            ReleaseBuffer();
        }
    }

    private void ReleaseBuffer()
    {
        // The layer shows an image over these pixels, so it has to let go of that image before the
        // pixels go: a layer holding an image whose provider has been freed draws garbage.
        if (Layer is { } layer)
        {
            layer.Contents = null;
        }

        _presented?.Dispose();
        _presented = null;

        // The surface writes into the bitmap's memory, so it has to go first.
        _skia?.Dispose();
        _skia = null;

        // The provider points at the bitmap's pixels, so it goes before the bitmap does.
        _provider?.Dispose();
        _provider = null;

        _bitmap?.Dispose();
        _bitmap = null;

        // The colour space is not tied to the pixels, so it outlives the buffer and is disposed
        // with the view instead of being rebuilt on every reallocation.
        _width = 0;
        _height = 0;
        _scale = 0;
    }

    /// <summary>
    /// Paints the reason the UI cannot be drawn over the whole view. A window that draws nothing
    /// says nothing, so every failure ends up here rather than on a console nobody is watching.
    /// <para>
    /// The message is drawn with Skia into the same bitmap every other frame uses, because by the
    /// time a failure is being reported there may be no usable buffer, and text drawn with AppKit
    /// would need the drawing context this view no longer takes part in.
    /// </para>
    /// </summary>
    private void DrawDiagnostic(string message)
    {
        if (_diagnostic != message)
        {
            _diagnostic = message;
            Console.Error.WriteLine($"[xplat] {message}");
        }

        if (Layer is not { } layer)
        {
            return;
        }

        if (_skia is null || _bitmap is null)
        {
            // Nothing to paint into, so the window is at least made a colour that says something
            // went wrong rather than left showing whatever was there before.
            layer.Contents = null;
            layer.BackgroundColor = new CGColor(0.6f, 0f, 0f);
            return;
        }

        try
        {
            var canvas = _skia.Canvas;
            canvas.Save();
            canvas.ResetMatrix();
            canvas.Clear(new SKColor(0x99, 0x00, 0x00));
            canvas.Restore();

            using var font = new XPlatFont(13f);
            XPlatTextRenderer.Draw(
                canvas,
                message,
                font,
                SKColors.White,
                new SKRect(16, 16, _width - 16, _height - 16),
                XPlatContentAlignment.TopLeft,
                true);

            canvas.Flush();
            Present();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[xplat] drawing the diagnostic failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void Trace(string message)
    {
        if (DebugTrace)
        {
            Console.Error.WriteLine($"[xplat] {message}");
        }
    }
}