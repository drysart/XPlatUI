using SkiaSharp;

namespace XPlatUI;

/// <summary>
/// Splits its client area into two <see cref="XPlatPanel"/> instances separated by a
/// draggable splitter. Naming follows <c>System.Windows.Forms.SplitContainer</c>:
/// with <see cref="XPlatOrientation.Vertical"/> the splitter runs vertically, so
/// <see cref="Panel1"/> is on the left, <see cref="Panel2"/> on the right and
/// <see cref="SplitterDistance"/> is a width; with <see cref="XPlatOrientation.Horizontal"/>
/// the splitter runs horizontally, <see cref="Panel1"/> is on top, <see cref="Panel2"/> below
/// and <see cref="SplitterDistance"/> is a height.
/// </summary>
/// <remarks>
/// <see cref="Panel1"/> is the panel whose size is specified; <see cref="Panel2"/> always
/// fills the remaining space. Dragging the splitter (or pressing the arrow keys while the
/// container has focus) adjusts the distance within <see cref="Panel1MinSize"/> and the
/// smaller of <see cref="Panel1MaxSize"/> and the space left over by
/// <see cref="Panel2MinSize"/>.
/// </remarks>
public class XPlatSplitContainer : XPlatControl
{
    private const float KeyboardStep = 8f;

    private XPlatOrientation _orientation = XPlatOrientation.Vertical;
    private float _splitterDistance = 200f;
    private float _splitterWidth = 6f;
    private float _panel1MinSize = 25f;
    private float _panel2MinSize = 25f;
    private float _panel1MaxSize = float.PositiveInfinity;
    private bool _isSplitterFixed;
    private bool _isDragging;
    private float _dragOrigin;
    private float _dragOriginDistance;
    private SKRect _splitterRect;

    public XPlatSplitContainer()
    {
        Panel1 = new XPlatPanel { Name = "Panel1" };
        Panel2 = new XPlatPanel { Name = "Panel2" };
        Controls.Add(Panel1);
        Controls.Add(Panel2);
        TabStop = true;
        UpdateCursor();
    }

    /// <summary>Raised while the splitter is being dragged.</summary>
    public event EventHandler? SplitterMoving;

    /// <summary>Raised when the user finishes dragging the splitter.</summary>
    public event EventHandler? SplitterMoved;

    /// <summary>Raised whenever <see cref="SplitterDistance"/> changes programmatically or through the splitter.</summary>
    public event EventHandler? SplitterDistanceChanged;

    /// <summary>The panel with the specified size. It sits left (vertical) or top (horizontal).</summary>
    public XPlatPanel Panel1 { get; }

    /// <summary>The panel that fills the remaining space. It sits right (vertical) or bottom (horizontal).</summary>
    public XPlatPanel Panel2 { get; }

    public XPlatOrientation Orientation
    {
        get => _orientation;
        set
        {
            if (_orientation == value)
            {
                return;
            }

            _orientation = value;
            UpdateCursor();
            PerformLayout();
            Invalidate();
        }
    }

    /// <summary>
    /// Size of <see cref="Panel1"/> along the split axis: a width for
    /// <see cref="XPlatOrientation.Vertical"/>, a height for <see cref="XPlatOrientation.Horizontal"/>.
    /// The value is clamped to the allowed range when it is set.
    /// </summary>
    public float SplitterDistance
    {
        get => _splitterDistance;
        set
        {
            var clamped = ClampDistance(value, AxisLength(ContentRect));
            if (MathF.Abs(clamped - _splitterDistance) < 0.01f)
            {
                return;
            }

            _splitterDistance = clamped;
            PerformLayout();
            Invalidate();
            OnSplitterDistanceChanged();
        }
    }

    /// <summary>Thickness of the draggable band between the panels.</summary>
    public float SplitterWidth
    {
        get => _splitterWidth;
        set
        {
            var normalized = MathF.Max(0f, value);
            if (MathF.Abs(normalized - _splitterWidth) < 0.01f)
            {
                return;
            }

            _splitterWidth = normalized;
            PerformLayout();
            Invalidate();
        }
    }

    /// <summary>Smallest size <see cref="Panel1"/> may be shrunk to.</summary>
    public float Panel1MinSize
    {
        get => _panel1MinSize;
        set => SetRange(ref _panel1MinSize, value);
    }

    /// <summary>Largest size <see cref="Panel1"/> may be grown to. Defaults to unlimited.</summary>
    public float Panel1MaxSize
    {
        get => _panel1MaxSize;
        set => SetRange(ref _panel1MaxSize, value <= 0f ? float.PositiveInfinity : value);
    }

    /// <summary>Smallest size <see cref="Panel2"/> may be shrunk to; it lowers the effective maximum.</summary>
    public float Panel2MinSize
    {
        get => _panel2MinSize;
        set => SetRange(ref _panel2MinSize, value);
    }

    /// <summary>When true the splitter cannot be dragged or moved with the keyboard.</summary>
    public bool IsSplitterFixed
    {
        get => _isSplitterFixed;
        set
        {
            if (_isSplitterFixed == value)
            {
                return;
            }

            _isSplitterFixed = value;
            if (value)
            {
                EndDrag();
            }

            UpdateCursor();
            Invalidate();
        }
    }

    /// <summary>True while the user is dragging the splitter.</summary>
    public bool IsDragging => _isDragging;

    /// <summary>Fill colour of the splitter. Null uses <see cref="XPlatTheme.Splitter"/>.</summary>
    public SKColor? SplitterColor { get; set; }

    /// <summary>Draws a small grip in the middle of the splitter.</summary>
    public bool ShowSplitterGrip { get; set; } = true;

    /// <summary>The splitter band in the control's own coordinates.</summary>
    public SKRect SplitterRect => _splitterRect;

    protected override SKSize MeasureContent(SKSize availableSize)
    {
        var first = Panel1.Measure(availableSize);
        var second = Panel2.Measure(availableSize);

        return _orientation == XPlatOrientation.Vertical
            ? new SKSize(_splitterDistance + _splitterWidth + second.Width, MathF.Max(first.Height, second.Height))
            : new SKSize(MathF.Max(first.Width, second.Width), _splitterDistance + _splitterWidth + second.Height);
    }

    public override void PerformLayout()
    {
        var content = ContentRect;
        var available = AxisLength(content);

        // Only collapse the requested distance when there is genuinely no room for it;
        // this keeps the value intact through transient zero/small layout passes.
        if (available >= _splitterWidth + _panel1MinSize + _panel2MinSize)
        {
            _splitterDistance = ClampDistance(_splitterDistance, available);
        }

        var splitterWidth = MathF.Min(MathF.Max(0f, _splitterWidth), available);
        var distance = Math.Clamp(_splitterDistance, 0f, MathF.Max(0f, available - splitterWidth));

        if (_orientation == XPlatOrientation.Vertical)
        {
            _splitterRect = new SKRect(content.Left + distance, content.Top, content.Left + distance + splitterWidth, content.Bottom);
            Panel1.Bounds = new SKRect(content.Left, content.Top, _splitterRect.Left, content.Bottom);
            Panel2.Bounds = new SKRect(_splitterRect.Right, content.Top, content.Right, content.Bottom);
        }
        else
        {
            _splitterRect = new SKRect(content.Left, content.Top + distance, content.Right, content.Top + distance + splitterWidth);
            Panel1.Bounds = new SKRect(content.Left, content.Top, content.Right, _splitterRect.Top);
            Panel2.Bounds = new SKRect(content.Left, _splitterRect.Bottom, content.Right, content.Bottom);
        }
    }

    protected override void OnPaint(SKCanvas canvas, SKRect rect)
    {
        if (_splitterRect.Width <= 0f || _splitterRect.Height <= 0f)
        {
            return;
        }

        var highlight = Enabled && !_isSplitterFixed && (IsMouseOver || _isDragging || IsFocused);
        var color = !Enabled
            ? XPlatTheme.DisabledBackground
            : _isDragging
                ? XPlatTheme.Accent
                : highlight
                    ? XPlatTheme.SplitterHover
                    : SplitterColor ?? XPlatTheme.Splitter;

        XPlatPainter.Fill(canvas, _splitterRect, color);

        if (ShowSplitterGrip && Enabled)
        {
            DrawGrip(canvas, _isDragging || highlight);
        }
    }

    protected override void OnMouseDown(XPlatMouseEventArgs e)
    {
        base.OnMouseDown(e);

        if (e.Button != XPlatMouseButtons.Left || !Enabled || _isSplitterFixed)
        {
            return;
        }

        if (!XPlatGeometry.Contains(_splitterRect, e.Location))
        {
            return;
        }

        _isDragging = true;
        _dragOrigin = AxisCoordinate(e.Location);
        _dragOriginDistance = _splitterDistance;
        CaptureMouse();
        Invalidate();
    }

    protected override void OnMouseMove(XPlatMouseEventArgs e)
    {
        base.OnMouseMove(e);

        if (!_isDragging)
        {
            return;
        }

        SplitterDistance = _dragOriginDistance + (AxisCoordinate(e.Location) - _dragOrigin);
        SplitterMoving?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnMouseUp(XPlatMouseEventArgs e)
    {
        base.OnMouseUp(e);

        if (!_isDragging)
        {
            return;
        }

        EndDrag();
        SplitterMoved?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnKeyDown(XPlatKeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (e.Handled || !Enabled || _isSplitterFixed)
        {
            return;
        }

        float delta = e.Key switch
        {
            XPlatKey.Left when _orientation == XPlatOrientation.Vertical => -KeyboardStep,
            XPlatKey.Right when _orientation == XPlatOrientation.Vertical => KeyboardStep,
            XPlatKey.Up when _orientation == XPlatOrientation.Horizontal => -KeyboardStep,
            XPlatKey.Down when _orientation == XPlatOrientation.Horizontal => KeyboardStep,
            _ => 0f,
        };

        if (delta == 0f)
        {
            return;
        }

        SplitterDistance += delta;
        e.Handled = true;
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        Invalidate();
    }

    protected override void OnLostFocus(EventArgs e)
    {
        base.OnLostFocus(e);
        EndDrag();
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        if (!Enabled)
        {
            EndDrag();
        }

        base.OnEnabledChanged(e);
    }

    protected virtual void OnSplitterDistanceChanged() => SplitterDistanceChanged?.Invoke(this, EventArgs.Empty);

    private void EndDrag()
    {
        if (!_isDragging)
        {
            return;
        }

        _isDragging = false;
        ReleaseMouseCapture();
        Invalidate();
    }

    private void DrawGrip(SKCanvas canvas, bool highlight)
    {
        const float radius = 1.5f;
        const float spacing = 5f;

        var center = XPlatGeometry.Center(_splitterRect);
        var color = highlight ? XPlatTheme.AccentForeground : XPlatTheme.MutedForeground.WithAlpha(0x60);
        for (int i = -1; i <= 1; i++)
        {
            float offset = i * spacing;
            float x = _orientation == XPlatOrientation.Vertical ? center.X : center.X + offset;
            float y = _orientation == XPlatOrientation.Vertical ? center.Y + offset : center.Y;
            XPlatPainter.Fill(canvas, new SKRect(x - radius, y - radius, x + radius, y + radius), color, radius);
        }
    }

    private void SetRange(ref float field, float value)
    {
        var normalized = MathF.Max(0f, value);
        if (MathF.Abs(normalized - field) < 0.01f)
        {
            return;
        }

        field = normalized;
        PerformLayout();
        Invalidate();
    }

    private void UpdateCursor() =>
        Cursor = _isSplitterFixed
            ? XPlatCursor.Default
            : _orientation == XPlatOrientation.Vertical ? XPlatCursor.SizeWE : XPlatCursor.SizeNS;

    /// <summary>Clamps a requested distance to the range allowed for the given available space.</summary>
    private float ClampDistance(float value, float available)
    {
        if (available <= 0f)
        {
            return MathF.Max(0f, value);
        }

        var min = MathF.Max(0f, _panel1MinSize);
        var max = MathF.Min(_panel1MaxSize, available - _splitterWidth - MathF.Max(0f, _panel2MinSize));
        if (max < min)
        {
            max = min;
        }

        return Math.Clamp(value, min, max);
    }

    private float AxisLength(SKRect rect) => _orientation == XPlatOrientation.Vertical ? rect.Width : rect.Height;

    private float AxisCoordinate(SKPoint point) => _orientation == XPlatOrientation.Vertical ? point.X : point.Y;
}
