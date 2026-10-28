using SkiaSharp;

namespace XPlatUI;

/// <summary>
/// Base class for every XPlat UI element. It fills the same role as
/// <c>System.Windows.Forms.Control</c> - bounds, parent/child relationships, layout,
/// painting, hit testing, focus and input - but it has no platform dependency:
/// it paints into an <see cref="SKCanvas"/> and receives input from an <see cref="XPlatUiHost"/>.
/// </summary>
/// <remarks>
/// All coordinates are logical pixels (96 DPI). The host adapter is responsible for
/// scaling the canvas and input coordinates to the device DPI.
/// </remarks>
public class XPlatControl : IDisposable
{
    private SKRect _bounds;
    private SKSize _desiredSize;
    private SKSize _minimumSize;
    private SKSize _maximumSize = new(float.PositiveInfinity, float.PositiveInfinity);
    private XPlatThickness _margin;
    private XPlatThickness _padding;
    private string _text = string.Empty;
    private bool _visible = true;
    private bool _enabled = true;
    private bool _autoSize;
    private bool _selectable;
    private XPlatSelectionStyle _selectionStyle = XPlatSelectionStyle.Whole;
    private bool _disposed;
    private XPlatUiHost? _host;
    private XPlatControlCollection? _controls;
    private List<SKRect>? _selectionRects;

    public XPlatControl()
    {
        Name = GetType().Name;
        ForeColor = XPlatTheme.Foreground;
        BorderColor = XPlatTheme.Border;
    }

    public event EventHandler? Click;

    public event EventHandler<XPlatMouseEventArgs>? MouseDown;

    public event EventHandler<XPlatMouseEventArgs>? MouseUp;

    public event EventHandler<XPlatMouseEventArgs>? MouseMove;

    public event EventHandler? MouseEnter;

    public event EventHandler? MouseLeave;

    public event EventHandler<XPlatKeyEventArgs>? KeyDown;

    public event EventHandler<XPlatKeyEventArgs>? KeyUp;

    public event EventHandler<XPlatTextInputEventArgs>? TextInput;

    public event EventHandler? GotFocus;

    public event EventHandler? LostFocus;

    public event EventHandler? BoundsChanged;

    public event EventHandler? TextChanged;

    public event EventHandler? VisibleChanged;

    public event EventHandler? EnabledChanged;

    /// <summary>Diagnostic name; defaults to the type name.</summary>
    public string Name { get; set; }

    /// <summary>Arbitrary user data associated with the control.</summary>
    public object? Tag { get; set; }

    /// <summary>
    /// Child controls in z-order. Containers such as <see cref="XPlatPanel"/> arrange
    /// them; leaf controls leave the collection empty.
    /// </summary>
    public XPlatControlCollection Controls => _controls ??= new XPlatControlCollection(this);

    /// <summary>True when at least one child is attached. Avoids creating the collection for leaves.</summary>
    internal bool HasChildren => _controls is { Count: > 0 };

    public XPlatControl? Parent { get; internal set; }

    /// <summary>The host this control is attached to, if any (resolved through the parent chain).</summary>
    public XPlatUiHost? Host => _host ?? Parent?.Host;

    /// <summary>Text shown by the control. Simple controls use it directly.</summary>
    public virtual string Text
    {
        get => _text;
        set
        {
            value ??= string.Empty;
            if (_text == value)
            {
                return;
            }

            _text = value;
            OnTextChanged(EventArgs.Empty);
            Invalidate();
        }
    }

    /// <summary>Bounds relative to the parent control's client area.</summary>
    public SKRect Bounds
    {
        get => _bounds;
        set
        {
            var normalized = XPlatGeometry.Normalize(value);
            if (_bounds.Equals(normalized))
            {
                return;
            }

            _bounds = normalized;
            OnBoundsChanged(EventArgs.Empty);
        }
    }

    public SKPoint Location
    {
        get => new(_bounds.Left, _bounds.Top);
        set => Bounds = new SKRect(value.X, value.Y, value.X + _bounds.Width, value.Y + _bounds.Height);
    }

    public SKSize Size
    {
        get => new(_bounds.Width, _bounds.Height);
        set => Bounds = new SKRect(_bounds.Left, _bounds.Top, _bounds.Left + value.Width, _bounds.Top + value.Height);
    }

    public float Left
    {
        get => _bounds.Left;
        set => Bounds = new SKRect(value, _bounds.Top, value + _bounds.Width, _bounds.Bottom);
    }

    public float Top
    {
        get => _bounds.Top;
        set => Bounds = new SKRect(_bounds.Left, value, _bounds.Right, value + _bounds.Height);
    }

    public float Width
    {
        get => _bounds.Width;
        set => Bounds = new SKRect(_bounds.Left, _bounds.Top, _bounds.Left + MathF.Max(0f, value), _bounds.Bottom);
    }

    public float Height
    {
        get => _bounds.Height;
        set => Bounds = new SKRect(_bounds.Left, _bounds.Top, _bounds.Right, _bounds.Top + MathF.Max(0f, value));
    }

    public float Right => _bounds.Right;

    public float Bottom => _bounds.Bottom;

    /// <summary>The control's own coordinate space: (0, 0, Width, Height).</summary>
    public SKRect ClientRect => new(0f, 0f, _bounds.Width, _bounds.Height);

    /// <summary>The area available to content, i.e. <see cref="ClientRect"/> minus padding.</summary>
    public SKRect ContentRect => XPlatGeometry.Deflate(ClientRect, Padding);

    public XPlatThickness Margin
    {
        get => _margin;
        set
        {
            if (_margin == value)
            {
                return;
            }

            _margin = value;
            Invalidate();
        }
    }

    public XPlatThickness Padding
    {
        get => _padding;
        set
        {
            if (_padding == value)
            {
                return;
            }

            _padding = value;
            Invalidate();
        }
    }

    public bool Visible
    {
        get => _visible;
        set
        {
            if (_visible == value)
            {
                return;
            }

            _visible = value;
            if (!value && IsFocused)
            {
                Host?.SetFocus(null);
            }

            OnVisibleChanged(EventArgs.Empty);
        }
    }

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value)
            {
                return;
            }

            _enabled = value;
            if (!value && IsFocused)
            {
                Host?.SetFocus(null);
            }

            OnEnabledChanged(EventArgs.Empty);
        }
    }

    /// <summary>When true, containers size the control to its <see cref="DesiredSize"/>.</summary>
    public bool AutoSize
    {
        get => _autoSize;
        set
        {
            if (_autoSize == value)
            {
                return;
            }

            _autoSize = value;
            Invalidate();
        }
    }

    /// <summary>Size computed by the last call to <see cref="Measure"/>.</summary>
    public SKSize DesiredSize
    {
        get => _desiredSize;
        protected set => _desiredSize = value;
    }

    public SKSize MinimumSize
    {
        get => _minimumSize;
        set => _minimumSize = new SKSize(MathF.Max(0f, value.Width), MathF.Max(0f, value.Height));
    }

    public SKSize MaximumSize
    {
        get => _maximumSize;
        set => _maximumSize = new SKSize(
            value.Width <= 0f ? float.PositiveInfinity : value.Width,
            value.Height <= 0f ? float.PositiveInfinity : value.Height);
    }

    public DockStyle Dock { get; set; }

    public XPlatCursor Cursor { get; set; }

    /// <summary>
    /// Returns the cursor to show while the pointer is at <paramref name="pointInLocal"/>
    /// (a point in this control's coordinate space, the same space as
    /// <see cref="XPlatMouseEventArgs.Location"/>). Controls with sub-elements - such as the
    /// links inside an <see cref="XPlatRichLabel"/> - override this to vary the cursor.
    /// </summary>
    public virtual XPlatCursor GetCursorAt(SKPoint pointInLocal) => Cursor;

    /// <summary>True when the control participates in Tab navigation.</summary>
    public bool TabStop { get; set; }

    public int TabIndex { get; set; }

    public bool IsFocused { get; private set; }

    public bool IsMouseOver { get; private set; }

    /// <summary>True while a mouse button is held down on the control.</summary>
    public bool IsPressed { get; internal set; }

    /// <summary>
    /// True when the control takes part in pointer-driven selections: pressing on it and
    /// dragging selects it, and it contributes its text to the clipboard. Controls opt in, so
    /// the default is false.
    /// </summary>
    public bool Selectable
    {
        get => _selectable;
        set
        {
            if (_selectable == value)
            {
                return;
            }

            _selectable = value;
            if (!value)
            {
                Host?.Selection.Refresh();
            }

            Invalidate();
        }
    }

    /// <summary>
    /// How the control takes part in a selection. Only meaningful when <see cref="Selectable"/> is
    /// true; the default, <see cref="XPlatSelectionStyle.Whole"/>, selects the control as a unit.
    /// </summary>
    public XPlatSelectionStyle SelectionStyle
    {
        get => _selectionStyle;
        set
        {
            if (_selectionStyle == value)
            {
                return;
            }

            _selectionStyle = value;
            Host?.Selection.Refresh();
            Invalidate();
        }
    }

    /// <summary>
    /// Number of selectable units in the control: 0 when it is not selectable, 1 for a whole
    /// control and the content length for a partial one.
    /// </summary>
    public virtual int SelectionUnitCount => !_selectable
        ? 0
        : _selectionStyle == XPlatSelectionStyle.Whole ? 1 : Text.Length;

    /// <summary>The slice of this control covered by the host's selection, or an empty range.</summary>
    public XPlatSelectionRange SelectionRange => Host?.Selection.RangeOf(this) ?? default;

    /// <summary>True when at least part of the control is selected.</summary>
    public bool IsSelected => !SelectionRange.IsEmpty;

    /// <summary>
    /// Maps a point in this control's coordinate space to the nearest selection offset, in the
    /// range 0..<see cref="SelectionUnitCount"/>. A whole control returns the middle of its single
    /// unit (0.5) for any point inside it. Controls with internal content override this.
    /// </summary>
    public virtual float GetSelectionOffset(SKPoint pointInLocal)
    {
        int units = SelectionUnitCount;
        if (units == 0)
        {
            return 0f;
        }

        if (_selectionStyle == XPlatSelectionStyle.Whole)
        {
            return 0.5f;
        }

        // Fallback for partial controls that do not map their own content: proportional.
        var content = ContentRect;
        if (content.Width <= 0f)
        {
            return 0f;
        }

        var ratio = (pointInLocal.X - content.Left) / content.Width;
        return Math.Clamp(ratio * units, 0f, units);
    }

    /// <summary>
    /// Adds the rectangles (in this control's coordinate space) that cover the given selection
    /// slice. Multi-line controls add one rectangle per line.
    /// </summary>
    public virtual void GetSelectionRects(int start, int length, List<SKRect> output)
    {
        ArgumentNullException.ThrowIfNull(output);

        if (length <= 0)
        {
            return;
        }

        if (_selectionStyle == XPlatSelectionStyle.Whole || (start <= 0 && length >= SelectionUnitCount))
        {
            output.Add(ClientRect);
        }
    }

    /// <summary>
    /// The text this control contributes to the clipboard for the given selection slice.
    /// </summary>
    public virtual string GetSelectionText(int start, int length)
    {
        if (length <= 0)
        {
            return string.Empty;
        }

        var text = Text;
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        if (_selectionStyle == XPlatSelectionStyle.Whole)
        {
            return text;
        }

        start = Math.Clamp(start, 0, text.Length);
        length = Math.Clamp(length, 0, text.Length - start);
        return length <= 0 ? string.Empty : text.Substring(start, length);
    }

    /// <summary>Background fill. Null means transparent.</summary>
    public SKColor? BackColor { get; set; }

    public SKColor ForeColor { get; set; }

    public SKColor BorderColor { get; set; }

    public float BorderWidth { get; set; }

    public float CornerRadius { get; set; }

    /// <summary>
    /// Computes the preferred size of the control for the given available space.
    /// The result includes padding but excludes <see cref="Margin"/>.
    /// </summary>
    public virtual SKSize Measure(SKSize availableSize)
    {
        var contentAvailable = new SKSize(
            MathF.Max(0f, availableSize.Width - Padding.Horizontal),
            MathF.Max(0f, availableSize.Height - Padding.Vertical));

        var content = MeasureContent(contentAvailable);
        var size = new SKSize(
            Clamp(content.Width + Padding.Horizontal, MinimumSize.Width, MaximumSize.Width),
            Clamp(content.Height + Padding.Vertical, MinimumSize.Height, MaximumSize.Height));

        DesiredSize = size;
        return size;
    }

    /// <summary>
    /// Measures the content area (padding already removed). Override in derived controls.
    /// </summary>
    protected virtual SKSize MeasureContent(SKSize availableSize) => SKSize.Empty;

    /// <summary>Arranges this control's children. Containers override this.</summary>
    public virtual void PerformLayout()
    {
    }

    /// <summary>Returns the deepest visible, enabled control containing the point.</summary>
    /// <param name="pointInParent">Point expressed in the parent's coordinate space.</param>
    public virtual XPlatControl? HitTest(SKPoint pointInParent)
    {
        if (!Visible)
        {
            return null;
        }

        var local = new SKPoint(pointInParent.X - _bounds.Left, pointInParent.Y - _bounds.Top);
        if (!XPlatGeometry.Contains(ClientRect, local))
        {
            return null;
        }

        if (_controls is not null)
        {
            for (int i = _controls.Count - 1; i >= 0; i--)
            {
                var hit = _controls[i].HitTest(local);
                if (hit is not null)
                {
                    return hit;
                }
            }
        }

        return Enabled ? this : null;
    }

    /// <summary>Paints the control and, for containers, its children.</summary>
    public void Render(SKCanvas canvas)
    {
        ArgumentNullException.ThrowIfNull(canvas);

        if (!Visible || _bounds.Width <= 0f || _bounds.Height <= 0f)
        {
            return;
        }

        canvas.Save();
        canvas.Translate(_bounds.Left, _bounds.Top);

        var local = ClientRect;
        if (CornerRadius > 0f)
        {
            canvas.ClipRoundRect(new SKRoundRect(local, CornerRadius), SKClipOperation.Intersect, antialias: true);
        }
        else
        {
            canvas.ClipRect(local);
        }

        OnPaintBackground(canvas, local);
        PaintSelection(canvas);
        OnPaint(canvas, local);
        RenderChildren(canvas);
        OnPaintBorder(canvas, local);

        canvas.Restore();
    }

    /// <summary>Moves keyboard focus to this control.</summary>
    public void Focus() => Host?.SetFocus(this);

    /// <summary>Routes all mouse input to this control until the capture is released.</summary>
    public void CaptureMouse() => Host?.CaptureMouse(this);

    public void ReleaseMouseCapture() => Host?.ReleaseMouseCapture(this);

    /// <summary>Requests a repaint of the surface hosting this control.</summary>
    public void Invalidate()
    {
        var host = Host;
        if (host is not null)
        {
            host.Invalidate();
        }
        else
        {
            Parent?.Invalidate();
        }
    }

    internal void AttachHost(XPlatUiHost? host) => _host = host;

    /// <summary>Called after a child is added. Re-runs layout and repaints.</summary>
    internal virtual void OnControlAdded(XPlatControl control)
    {
        PerformLayout();
        Invalidate();
    }

    /// <summary>Called after a child is removed. Re-runs layout and repaints.</summary>
    internal virtual void OnControlRemoved(XPlatControl control)
    {
        PerformLayout();
        Invalidate();
    }

    internal void RaiseMouseDown(XPlatMouseEventArgs e)
    {
        if (Enabled)
        {
            OnMouseDown(e);
        }
    }

    internal void RaiseMouseUp(XPlatMouseEventArgs e)
    {
        if (Enabled)
        {
            OnMouseUp(e);
        }
    }

    internal void RaiseMouseMove(XPlatMouseEventArgs e)
    {
        if (Enabled)
        {
            OnMouseMove(e);
        }
    }

    internal void RaiseMouseEnter()
    {
        if (IsMouseOver)
        {
            return;
        }

        IsMouseOver = true;
        OnMouseEnter(EventArgs.Empty);
        Invalidate();
    }

    internal void RaiseMouseLeave()
    {
        if (!IsMouseOver)
        {
            return;
        }

        IsMouseOver = false;
        OnMouseLeave(EventArgs.Empty);
        Invalidate();
    }

    /// <summary>Delivers a key press. Returns true when the control consumed the key.</summary>
    internal bool RaiseKeyDown(XPlatKeyEventArgs e)
    {
        if (Enabled)
        {
            OnKeyDown(e);
        }

        return e.Handled;
    }

    internal void RaiseKeyUp(XPlatKeyEventArgs e)
    {
        if (Enabled)
        {
            OnKeyUp(e);
        }
    }

    internal void RaiseTextInput(XPlatTextInputEventArgs e)
    {
        if (Enabled)
        {
            OnTextInput(e);
        }
    }

    internal void RaiseTick(TimeSpan elapsed)
    {
        if (Enabled)
        {
            OnTick(elapsed);
        }
    }

    internal void RaiseGotFocus()
    {
        IsFocused = true;
        OnGotFocus(EventArgs.Empty);
        Invalidate();
    }

    internal void RaiseLostFocus()
    {
        IsFocused = false;
        IsPressed = false;
        OnLostFocus(EventArgs.Empty);
        Invalidate();
    }

    protected virtual void OnPaintBackground(SKCanvas canvas, SKRect rect)
    {
        if (BackColor is { } backColor)
        {
            XPlatPainter.Fill(canvas, rect, backColor, CornerRadius);
        }
    }

    protected virtual void OnPaint(SKCanvas canvas, SKRect rect)
    {
    }

    protected virtual void OnPaintBorder(SKCanvas canvas, SKRect rect)
    {
        if (BorderWidth > 0f)
        {
            XPlatPainter.DrawBorder(canvas, rect, BorderColor, BorderWidth, CornerRadius);
        }
    }

    /// <summary>
    /// Fills the part of the host selection that covers this control, between the background and
    /// the content, so selected text stays readable.
    /// </summary>
    private void PaintSelection(SKCanvas canvas)
    {
        var range = SelectionRange;
        if (range.IsEmpty)
        {
            return;
        }

        var rects = _selectionRects ??= new List<SKRect>(4);
        rects.Clear();
        GetSelectionRects(range.Start, range.Length, rects);

        foreach (var rect in rects)
        {
            XPlatPainter.Fill(canvas, rect, XPlatTheme.Selection);
        }
    }

    /// <summary>Paints child controls in z-order.</summary>
    protected virtual void RenderChildren(SKCanvas canvas)
    {
        if (_controls is null)
        {
            return;
        }

        foreach (var child in _controls)
        {
            child.Render(canvas);
        }
    }

    protected virtual void OnMouseDown(XPlatMouseEventArgs e) => MouseDown?.Invoke(this, e);

    protected virtual void OnMouseUp(XPlatMouseEventArgs e) => MouseUp?.Invoke(this, e);

    protected virtual void OnMouseMove(XPlatMouseEventArgs e) => MouseMove?.Invoke(this, e);

    protected virtual void OnMouseEnter(EventArgs e) => MouseEnter?.Invoke(this, e);

    protected virtual void OnMouseLeave(EventArgs e) => MouseLeave?.Invoke(this, e);

    protected virtual void OnClick(EventArgs e) => Click?.Invoke(this, e);

    protected virtual void OnKeyDown(XPlatKeyEventArgs e) => KeyDown?.Invoke(this, e);

    protected virtual void OnKeyUp(XPlatKeyEventArgs e) => KeyUp?.Invoke(this, e);

    protected virtual void OnTextInput(XPlatTextInputEventArgs e) => TextInput?.Invoke(this, e);

    /// <summary>
    /// Called periodically by the host while this control has focus. Controls that animate
    /// (for example the caret of an <see cref="XPlatTextBox"/>) override this.
    /// </summary>
    protected virtual void OnTick(TimeSpan elapsed)
    {
    }

    protected virtual void OnGotFocus(EventArgs e) => GotFocus?.Invoke(this, e);

    protected virtual void OnLostFocus(EventArgs e) => LostFocus?.Invoke(this, e);

    protected virtual void OnTextChanged(EventArgs e)
    {
        TextChanged?.Invoke(this, e);
        Host?.Selection.Refresh();
        PerformLayout();
    }

    protected virtual void OnVisibleChanged(EventArgs e)
    {
        VisibleChanged?.Invoke(this, e);
        Invalidate();
    }

    protected virtual void OnEnabledChanged(EventArgs e)
    {
        EnabledChanged?.Invoke(this, e);
        Invalidate();
    }

    protected virtual void OnBoundsChanged(EventArgs e)
    {
        BoundsChanged?.Invoke(this, e);
        PerformLayout();
        Invalidate();
    }

    private static float Clamp(float value, float min, float max)
    {
        if (max < min)
        {
            max = min;
        }

        return MathF.Min(MathF.Max(value, min), max);
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (disposing && _controls is not null)
        {
            var children = _controls.ToArray();
            _controls.Clear();
            foreach (var child in children)
            {
                child.Parent = null;
                child.Dispose();
            }

            _controls = null;
        }
    }
}
