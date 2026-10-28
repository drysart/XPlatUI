using SkiaSharp;

namespace XPlatUI;

/// <summary>
/// A label that renders rich text: a sequence of <see cref="XPlatTextSpan"/> items that each
/// carry their own formatting (bold, italic, underline, strikethrough, colour, size) and may
/// also place a live control in the text flow - the equivalent of CSS <c>inline-block</c>.
/// </summary>
/// <remarks>
/// <para>
/// Text wraps at the control's width (see <see cref="WrapText"/>) using a greedy line breaker
/// that mixes formatted runs and inline controls. All runs on a line share one baseline, so
/// differently sized text stays aligned; inline controls are aligned to that line's text box
/// using <see cref="XPlatTextSpan.InlineAlignment"/>.
/// </para>
/// <para>
/// Spans are not controls: they are lightweight objects owned by the label. A span raises
/// mouse events, which is what makes hyperlinks possible.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// var label = new XPlatRichLabel { Dock = DockStyle.Top, WrapText = true };
/// label.AddText("Read the ");
/// label.AddLink("documentation", span => OpenDocs()).Tag = "docs";
/// label.AddText(" for details.");
/// label.AddInlineControl(new XPlatButton("Retry"), XPlatInlineAlignment.Center);
/// </code>
/// </example>
public class XPlatRichLabel : XPlatControl
{
    private enum FragmentKind
    {
        Word,
        Space,
        Break,
        Control,
    }

    private const float HighlightRadius = 2f;

    private readonly XPlatTextSpanCollection _spans;
    private readonly Dictionary<string, XPlatFont> _derivedFonts = new(StringComparer.Ordinal);
    private XPlatFont _font = XPlatTheme.DefaultFont;
    private Layout? _layout;
    private XPlatTextSpan? _hoverSpan;
    private XPlatTextSpan? _pressedSpan;
    private bool _wrapText = true;
    private float _lineSpacing;

    public XPlatRichLabel()
    {
        _spans = new XPlatTextSpanCollection(this);
        AutoSize = true;
        ForeColor = XPlatTheme.Foreground;
    }

    /// <summary>Creates a label from a set of spans.</summary>
    public XPlatRichLabel(params XPlatTextSpan[] spans)
        : this()
    {
        _spans.AddRange(spans);
    }

    /// <summary>Raised when a span is clicked (pressed and released over the same span).</summary>
    public event EventHandler<XPlatSpanEventArgs>? SpanClick;

    /// <summary>The formatted runs of this label, in flow order.</summary>
    public XPlatTextSpanCollection Spans => _spans;

    /// <summary>Base font. Spans derive their own font from it. The label does not take ownership of it.</summary>
    public XPlatFont Font
    {
        get => _font;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (ReferenceEquals(_font, value))
            {
                return;
            }

            _font = value;
            ClearDerivedFonts();
            InvalidateLayout();
        }
    }

    /// <summary>When true (the default) lines break at the control's width.</summary>
    public bool WrapText
    {
        get => _wrapText;
        set
        {
            if (_wrapText == value)
            {
                return;
            }

            _wrapText = value;
            InvalidateLayout();
        }
    }

    /// <summary>Extra vertical space between two lines, in logical pixels.</summary>
    public float LineSpacing
    {
        get => _lineSpacing;
        set
        {
            value = MathF.Max(0f, value);
            if (MathF.Abs(_lineSpacing - value) < 0.01f)
            {
                return;
            }

            _lineSpacing = value;
            InvalidateLayout();
        }
    }

    /// <summary>Placement of the text block inside the content area.</summary>
    public XPlatContentAlignment TextAlign { get; set; } = XPlatContentAlignment.TopLeft;

    /// <summary>
    /// Concatenation of the span texts. Assigning replaces the spans with a single plain span.
    /// </summary>
    public override string Text
    {
        get
        {
            if (_spans.Count == 0)
            {
                return string.Empty;
            }

            if (_spans.Count == 1)
            {
                return _spans[0].Text;
            }

            var builder = new System.Text.StringBuilder();
            foreach (var span in _spans)
            {
                if (span.Control is null)
                {
                    builder.Append(span.Text);
                }
            }

            return builder.ToString();
        }

        set
        {
            var text = value ?? string.Empty;
            if (string.Equals(text, Text, StringComparison.Ordinal))
            {
                return;
            }

            _spans.Clear();
            if (text.Length > 0)
            {
                _spans.Add(new XPlatTextSpan(text));
            }

            OnTextChanged(EventArgs.Empty);
        }
    }

    /// <summary>Adds a plain span and returns it.</summary>
    public XPlatTextSpan AddText(string? text) => AddSpan(new XPlatTextSpan(text));

    /// <summary>
    /// Adds a span styled as a hyperlink: accent colour, underlined and a hand cursor.
    /// </summary>
    public XPlatTextSpan AddLink(string? text, Action<XPlatTextSpan>? onClick = null)
    {
        var span = new XPlatTextSpan(text)
        {
            ForeColor = XPlatTheme.Accent,
            Underline = true,
            HoverBackColor = new SKColor(0x2F, 0x6F, 0xED, 0x1A),
        };

        if (onClick is not null)
        {
            span.Click += (_, e) => onClick(e.Span);
        }

        return AddSpan(span);
    }

    /// <summary>
    /// Adds a control that takes part in the text flow. The control becomes a child of this
    /// label and is positioned by the layout engine.
    /// </summary>
    public XPlatTextSpan AddInlineControl(XPlatControl control, XPlatInlineAlignment alignment = XPlatInlineAlignment.Bottom) =>
        AddSpan(XPlatTextSpan.ForControl(control, alignment));

    /// <summary>Adds an existing span and returns it.</summary>
    public XPlatTextSpan AddSpan(XPlatTextSpan span)
    {
        ArgumentNullException.ThrowIfNull(span);
        _spans.Add(span);
        return span;
    }

    /// <summary>Removes every span (and the inline controls they contributed).</summary>
    public void ClearSpans() => _spans.Clear();

    protected override SKSize MeasureContent(SKSize availableSize)
    {
        var layout = BuildLayout(NormalizeWrapWidth(availableSize.Width));
        return new SKSize(MathF.Ceiling(layout.Width), MathF.Ceiling(layout.Height));
    }

    public override void PerformLayout()
    {
        var content = ContentRect;
        if (content.Width <= 0f)
        {
            _layout = null;
            return;
        }

        var layout = BuildLayout(NormalizeWrapWidth(content.Width));
        Place(layout, content);
        ApplyInlineBounds(layout);
    }

    public override XPlatCursor GetCursorAt(SKPoint pointInLocal)
    {
        var span = SpanAt(pointInLocal);
        if (span is not null && span.Cursor != XPlatCursor.Default)
        {
            return span.Cursor;
        }

        return base.GetCursorAt(pointInLocal);
    }

    protected override void OnPaint(SKCanvas canvas, SKRect rect)
    {
        var content = XPlatGeometry.Deflate(rect, Padding);
        if (content.Width <= 0f || content.Height <= 0f)
        {
            return;
        }

        var layout = EnsureLayout(content.Width);
        if (layout is null)
        {
            return;
        }

        Place(layout, content);

        using var fill = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };
        using var textPaint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };

        foreach (var line in layout.Lines)
        {
            DrawSpanHighlights(canvas, line, fill);

            foreach (var fragment in line.Fragments)
            {
                if (fragment.Kind is FragmentKind.Control or FragmentKind.Space || fragment.Text.Length == 0)
                {
                    continue;
                }

                float baseline = fragment.Bounds.Top + line.Ascent;
                var color = fragment.Span.ForeColor ?? (Enabled ? ForeColor : XPlatTheme.DisabledForeground);
                textPaint.Color = color;
                canvas.DrawText(fragment.Text, fragment.Bounds.Left, baseline, SKTextAlign.Left, fragment.Font.SkFont, textPaint);
            }

            DrawSpanDecorations(canvas, line, fill);
        }
    }

    protected override void OnMouseMove(XPlatMouseEventArgs e)
    {
        var span = SpanAt(e.Location);
        if (!ReferenceEquals(span, _hoverSpan))
        {
            SetHoverSpan(span);
        }

        span?.RaiseMouseMove(new XPlatSpanEventArgs(span, e.Location, e.Button, e.Modifiers, e.ClickCount));
        base.OnMouseMove(e);
    }

    protected override void OnMouseDown(XPlatMouseEventArgs e)
    {
        var span = SpanAt(e.Location);
        if (span is not null)
        {
            span.IsPressed = true;
            _pressedSpan = span;
            span.RaiseMouseDown(new XPlatSpanEventArgs(span, e.Location, e.Button, e.Modifiers, e.ClickCount));
            Invalidate();
        }

        base.OnMouseDown(e);
    }

    protected override void OnMouseUp(XPlatMouseEventArgs e)
    {
        var pressed = _pressedSpan;
        _pressedSpan = null;

        if (pressed is not null)
        {
            pressed.IsPressed = false;
            pressed.RaiseMouseUp(new XPlatSpanEventArgs(pressed, e.Location, e.Button, e.Modifiers, e.ClickCount));

            if (ReferenceEquals(pressed, SpanAt(e.Location)))
            {
                var args = new XPlatSpanEventArgs(pressed, e.Location, e.Button, e.Modifiers, e.ClickCount);
                pressed.RaiseClick(args);
                SpanClick?.Invoke(this, args);
            }

            Invalidate();
        }

        base.OnMouseUp(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        SetHoverSpan(null);
        base.OnMouseLeave(e);
    }

    internal override void OnControlAdded(XPlatControl control)
    {
        _layout = null;
        base.OnControlAdded(control);
    }

    internal override void OnControlRemoved(XPlatControl control)
    {
        _layout = null;
        base.OnControlRemoved(control);
    }

    /// <summary>Called when one of this label's spans changed and the layout must be rebuilt.</summary>
    internal void OnSpanChanged(XPlatTextSpan span)
    {
        if (ReferenceEquals(span, _hoverSpan) || ReferenceEquals(span, _pressedSpan))
        {
            SetHoverSpan(null);
        }

        InvalidateLayout();
    }

    internal void OnSpanAdded(XPlatTextSpan span)
    {
        if (span.Control is { } control && !ReferenceEquals(control.Parent, this))
        {
            Controls.Add(control);
        }

        InvalidateLayout();
    }

    internal void OnSpanRemoved(XPlatTextSpan span)
    {
        if (ReferenceEquals(span, _hoverSpan) || ReferenceEquals(span, _pressedSpan))
        {
            span.IsMouseOver = false;
            span.IsPressed = false;
            _hoverSpan = null;
            _pressedSpan = null;
        }

        if (span.Control is { } control && ReferenceEquals(control.Parent, this))
        {
            Controls.Remove(control);
        }

        InvalidateLayout();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _spans.Clear();
            ClearDerivedFonts();
            _layout = null;
        }

        base.Dispose(disposing);
    }

    private void InvalidateLayout()
    {
        _layout = null;
        PerformLayout();
        Invalidate();
    }

    private float NormalizeWrapWidth(float width) =>
        _wrapText && width > 0f && float.IsFinite(width) ? width : float.PositiveInfinity;

    private Layout? EnsureLayout(float width)
    {
        var wrapWidth = NormalizeWrapWidth(width);
        if (_layout is not null && _layout.WrapWidth.Equals(wrapWidth))
        {
            return _layout;
        }

        _layout = BuildLayout(wrapWidth);
        return _layout;
    }

    /// <summary>
    /// Returns the span whose glyphs contain the point (control spans are excluded, because the
    /// control itself receives the mouse), or null when the point is over empty space.
    /// </summary>
    public XPlatTextSpan? SpanAt(SKPoint pointInLocal)
    {
        var content = ContentRect;
        if (content.Width <= 0f || content.Height <= 0f)
        {
            return null;
        }

        var layout = EnsureLayout(content.Width);
        if (layout is null)
        {
            return null;
        }

        Place(layout, content);

        for (int i = layout.Lines.Count - 1; i >= 0; i--)
        {
            var line = layout.Lines[i];
            for (int j = line.Fragments.Count - 1; j >= 0; j--)
            {
                var fragment = line.Fragments[j];
                if (fragment.Kind != FragmentKind.Control && XPlatGeometry.Contains(fragment.Bounds, pointInLocal))
                {
                    return fragment.Span;
                }
            }
        }

        return null;
    }

    private void SetHoverSpan(XPlatTextSpan? span)
    {
        if (ReferenceEquals(span, _hoverSpan))
        {
            return;
        }

        if (_hoverSpan is not null)
        {
            _hoverSpan.IsMouseOver = false;
            _hoverSpan.RaiseMouseLeave();
        }

        _hoverSpan = span;

        if (_hoverSpan is not null)
        {
            _hoverSpan.IsMouseOver = true;
            _hoverSpan.RaiseMouseEnter();
        }

        Invalidate();
    }

    private void ApplyInlineBounds(Layout layout)
    {
        foreach (var line in layout.Lines)
        {
            foreach (var fragment in line.Fragments)
            {
                if (fragment.Kind == FragmentKind.Control && fragment.Span.Control is { } control)
                {
                    control.Bounds = fragment.Bounds;
                }
            }
        }
    }

    /// <summary>Assigns absolute (label-local) bounds to every fragment.</summary>
    private void Place(Layout layout, SKRect content)
    {
        float blockTop = content.Top + (TextAlign switch
        {
            XPlatContentAlignment.MiddleLeft or XPlatContentAlignment.MiddleCenter or XPlatContentAlignment.MiddleRight
                => (content.Height - layout.Height) / 2f,
            XPlatContentAlignment.BottomLeft or XPlatContentAlignment.BottomCenter or XPlatContentAlignment.BottomRight
                => content.Height - layout.Height,
            _ => 0f,
        });

        foreach (var line in layout.Lines)
        {
            float lineTop = blockTop + line.Top;
            float x = content.Left + (TextAlign switch
            {
                XPlatContentAlignment.TopCenter or XPlatContentAlignment.MiddleCenter or XPlatContentAlignment.BottomCenter
                    => (content.Width - line.Width) / 2f,
                XPlatContentAlignment.TopRight or XPlatContentAlignment.MiddleRight or XPlatContentAlignment.BottomRight
                    => content.Width - line.Width,
                _ => 0f,
            });

            foreach (var fragment in line.Fragments)
            {
                fragment.Bounds = fragment.Kind == FragmentKind.Control
                    ? new SKRect(x, lineTop + line.Lead + fragment.OffsetY, x + fragment.Width, lineTop + line.Lead + fragment.OffsetY + fragment.Height)
                    : new SKRect(x, lineTop, x + fragment.Width, lineTop + line.Height);
                x += fragment.Width;
            }
        }
    }

    /// <summary>
    /// Fills the hover/press background once per run of adjacent fragments of the same span, so a
    /// highlighted span reads as one continuous shape instead of one rounded box per word and space.
    /// </summary>
    private void DrawSpanHighlights(SKCanvas canvas, Line line, SKPaint paint)
    {
        var fragments = line.Fragments;
        for (int i = 0; i < fragments.Count; i++)
        {
            var first = fragments[i];
            var span = first.Span;
            if (first.Kind is FragmentKind.Break or FragmentKind.Control ||
                (!span.IsMouseOver && !span.IsPressed) ||
                span.HoverBackColor is not { } hoverBack)
            {
                continue;
            }

            i = FindRunEnd(fragments, i, out float left, out float right);

            paint.Color = span.IsPressed ? WithAlpha(hoverBack, 0x33) : hoverBack;
            var rect = new SKRect(left, first.Bounds.Top, right, first.Bounds.Bottom);
            canvas.DrawRoundRect(rect, HighlightRadius, HighlightRadius, paint);
        }
    }

    /// <summary>
    /// Draws underlines and strike-throughs once per run of adjacent fragments of the same span,
    /// so a decoration continues across the spaces between words instead of restarting per word.
    /// </summary>
    private void DrawSpanDecorations(SKCanvas canvas, Line line, SKPaint paint)
    {
        var fragments = line.Fragments;
        for (int i = 0; i < fragments.Count; i++)
        {
            var first = fragments[i];
            var span = first.Span;
            if (first.Kind is FragmentKind.Break or FragmentKind.Control || (!span.Underline && !span.Strikethrough))
            {
                continue;
            }

            i = FindRunEnd(fragments, i, out float left, out float right);

            var color = span.ForeColor ?? (Enabled ? ForeColor : XPlatTheme.DisabledForeground);
            DrawDecorations(canvas, left, right, first.Bounds.Top + line.Ascent, first.Font, span, color, paint);
        }
    }

    /// <summary>
    /// Returns the index of the last fragment in the run of adjacent fragments belonging to the same
    /// span as <paramref name="start"/>, along with the run's horizontal extent. Spaces inside a run
    /// are part of it, so decorations and highlights continue across the gaps between words.
    /// </summary>
    private static int FindRunEnd(List<Fragment> fragments, int start, out float left, out float right)
    {
        var first = fragments[start];
        left = first.Bounds.Left;
        right = first.Bounds.Right;

        int end = start;
        while (end + 1 < fragments.Count)
        {
            var next = fragments[end + 1];
            if (!ReferenceEquals(next.Span, first.Span) ||
                next.Kind is FragmentKind.Break or FragmentKind.Control ||
                next.Bounds.Left > right + 0.5f)
            {
                break;
            }

            right = MathF.Max(right, next.Bounds.Right);
            end++;
        }

        return end;
    }

    private static void DrawDecorations(SKCanvas canvas, float left, float right, float baseline, XPlatFont font, XPlatTextSpan span, SKColor color, SKPaint paint)
    {
        if (right - left <= 0f)
        {
            return;
        }

        var metrics = font.SkFont.Metrics;
        float size = font.Size;
        float capHeight = metrics.CapHeight > 0f ? metrics.CapHeight : -metrics.Ascent * 0.7f;
        float xHeight = metrics.XHeight > 0f ? metrics.XHeight : capHeight * 0.7f;
        float thickness = MathF.Max(1f, metrics.UnderlineThickness ?? 1f);
        paint.Color = color;

        if (span.Underline)
        {
            // Only the magnitude is meaningful: an underline always sits below the baseline.
            float offset = MathF.Abs(metrics.UnderlinePosition.GetValueOrDefault());
            if (offset < 0.01f || offset > size * 0.35f)
            {
                offset = MathF.Max(1.5f, size * 0.12f);
            }

            float y = baseline + offset;
            canvas.DrawRect(new SKRect(left, y - (thickness / 2f), right, y + (thickness / 2f)), paint);
        }

        if (span.Strikethrough)
        {
            // Only the magnitude is meaningful: a strike-through always sits above the baseline,
            // roughly half an x-height up.
            float offset = MathF.Abs(metrics.StrikeoutPosition.GetValueOrDefault());
            if (offset < size * 0.1f || offset > capHeight)
            {
                offset = xHeight * 0.5f;
            }

            float y = baseline - offset;
            canvas.DrawRect(new SKRect(left, y - (thickness / 2f), right, y + (thickness / 2f)), paint);
        }
    }

    private Layout BuildLayout(float wrapWidth)
    {
        if (wrapWidth <= 0f)
        {
            wrapWidth = float.PositiveInfinity;
        }

        var layout = new Layout { WrapWidth = wrapWidth };
        bool wrap = float.IsFinite(wrapWidth);

        var line = new Line();
        float x = 0f;

        void Commit()
        {
            while (line.Fragments.Count > 0 && line.Fragments[^1].Kind == FragmentKind.Space)
            {
                x -= line.Fragments[^1].Width;
                line.Fragments.RemoveAt(line.Fragments.Count - 1);
            }

            if (line.Fragments.Count == 0)
            {
                x = 0f;
                return;
            }

            line.Width = x;
            layout.Lines.Add(line);
            line = new Line();
            x = 0f;
        }

        foreach (var fragment in BuildFragments(wrapWidth))
        {
            switch (fragment.Kind)
            {
                case FragmentKind.Break:
                {
                    int before = layout.Lines.Count;
                    Commit();
                    if (layout.Lines.Count == before)
                    {
                        // A blank line keeps the paragraph spacing of an empty line.
                        layout.Lines.Add(new Line { Ascent = _font.BaselineOffset, Height = _font.LineHeight });
                    }

                    break;
                }

                case FragmentKind.Control:
                    if (wrap && x + fragment.Width > wrapWidth && line.Fragments.Count > 0)
                    {
                        Commit();
                    }

                    line.Fragments.Add(fragment);
                    x += fragment.Width;
                    break;

                case FragmentKind.Space:
                    if (line.Fragments.Count == 0)
                    {
                        break;
                    }

                    line.Fragments.Add(fragment);
                    x += fragment.Width;
                    break;

                default:
                    if (wrap && x + fragment.Width > wrapWidth)
                    {
                        if (line.Fragments.Count > 0)
                        {
                            Commit();
                        }

                        if (fragment.Width > wrapWidth)
                        {
                            var remaining = fragment.Text;
                            while (remaining.Length > 0)
                            {
                                int take = FitPrefix(remaining, fragment.Font, wrapWidth);
                                if (take <= 0)
                                {
                                    take = 1;
                                }

                                var piece = new Fragment
                                {
                                    Span = fragment.Span,
                                    Kind = FragmentKind.Word,
                                    Font = fragment.Font,
                                    Text = remaining[..take],
                                };
                                piece.Width = fragment.Font.MeasureWidth(piece.Text);

                                line.Fragments.Add(piece);
                                x += piece.Width;
                                remaining = remaining[take..];

                                if (remaining.Length > 0)
                                {
                                    Commit();
                                }
                            }

                            break;
                        }
                    }

                    line.Fragments.Add(fragment);
                    x += fragment.Width;
                    break;
            }
        }

        Commit();

        float y = 0f;
        foreach (var current in layout.Lines)
        {
            MeasureLine(current);
            current.Top = y;
            y += current.Height + _lineSpacing;
        }

        layout.Height = layout.Lines.Count > 0 ? y - _lineSpacing : 0f;

        float width = 0f;
        foreach (var current in layout.Lines)
        {
            width = MathF.Max(width, current.Width);
        }

        layout.Width = width;
        return layout;
    }

    private List<Fragment> BuildFragments(float wrapWidth)
    {
        var fragments = new List<Fragment>();
        foreach (var span in _spans)
        {
            if (span.Control is { } control)
            {
                var size = MeasureInlineControl(control, wrapWidth);
                fragments.Add(new Fragment
                {
                    Span = span,
                    Kind = FragmentKind.Control,
                    Width = size.Width,
                    Height = size.Height,
                });
                continue;
            }

            Tokenize(span, ResolveFont(span), fragments);
        }

        return fragments;
    }

    private static void Tokenize(XPlatTextSpan span, XPlatFont font, List<Fragment> output)
    {
        var text = span.Text;
        int index = 0;

        while (index < text.Length)
        {
            char c = text[index];
            if (c == '\r')
            {
                index++;
                continue;
            }

            if (c == '\n')
            {
                output.Add(new Fragment { Span = span, Kind = FragmentKind.Break, Font = font });
                index++;
                continue;
            }

            int start = index;
            if (char.IsWhiteSpace(c))
            {
                while (index < text.Length && text[index] != '\n' && char.IsWhiteSpace(text[index]))
                {
                    index++;
                }

                var run = text[start..index];
                output.Add(new Fragment
                {
                    Span = span,
                    Kind = FragmentKind.Space,
                    Font = font,
                    Text = run,
                    Width = font.MeasureWidth(run),
                });
                continue;
            }

            while (index < text.Length && !char.IsWhiteSpace(text[index]))
            {
                index++;
            }

            var word = text[start..index];
            output.Add(new Fragment
            {
                Span = span,
                Kind = FragmentKind.Word,
                Font = font,
                Text = word,
                Width = font.MeasureWidth(word),
            });
        }
    }

    private void MeasureLine(Line line)
    {
        float textAscent = 0f;
        float textDescent = 0f;

        foreach (var fragment in line.Fragments)
        {
            if (fragment.Kind is FragmentKind.Word or FragmentKind.Space)
            {
                textAscent = MathF.Max(textAscent, fragment.Font.BaselineOffset);
                textDescent = MathF.Max(textDescent, fragment.Font.LineHeight - fragment.Font.BaselineOffset);
            }
        }

        float textHeight = textAscent + textDescent;
        float top = 0f;
        float bottom = textHeight;

        foreach (var fragment in line.Fragments)
        {
            if (fragment.Kind != FragmentKind.Control)
            {
                continue;
            }

            fragment.OffsetY = fragment.Span.InlineAlignment switch
            {
                XPlatInlineAlignment.Top => 0f,
                XPlatInlineAlignment.Center => (textHeight - fragment.Height) / 2f,
                _ => textHeight - fragment.Height,
            };

            top = MathF.Min(top, fragment.OffsetY);
            bottom = MathF.Max(bottom, fragment.OffsetY + fragment.Height);
        }

        line.Ascent = textAscent - top;
        line.Lead = -top;
        line.Height = MathF.Max(bottom - top, textHeight);
    }

    private static SKSize MeasureInlineControl(XPlatControl control, float wrapWidth)
    {
        var available = new SKSize(
            float.IsFinite(wrapWidth) ? MathF.Max(0f, wrapWidth) : float.PositiveInfinity,
            float.PositiveInfinity);

        var desired = control.Measure(available);
        float width = control.AutoSize || control.Width <= 0f ? desired.Width : control.Width;
        float height = control.AutoSize || control.Height <= 0f ? desired.Height : control.Height;

        return new SKSize(MathF.Max(0f, width), MathF.Max(0f, height));
    }

    /// <summary>Largest prefix of <paramref name="text"/> that fits in <paramref name="maxWidth"/>.</summary>
    private static int FitPrefix(string text, XPlatFont font, float maxWidth)
    {
        int low = 0;
        int high = text.Length;

        while (low < high)
        {
            int mid = (low + high + 1) / 2;
            if (font.MeasureWidth(text[..mid]) <= maxWidth)
            {
                low = mid;
            }
            else
            {
                high = mid - 1;
            }
        }

        return low;
    }

    private XPlatFont ResolveFont(XPlatTextSpan span)
    {
        if (span.Font is not null)
        {
            return span.Font;
        }

        int weight = span.Bold ? Math.Max(700, _font.Weight) : _font.Weight;
        bool italic = span.Italic || _font.Italic;
        float size = span.FontSize is { } spanSize ? spanSize : _font.Size;
        string family = span.FontFamily ?? _font.FamilyName;

        if (weight == _font.Weight
            && italic == _font.Italic
            && MathF.Abs(size - _font.Size) < 0.01f
            && string.Equals(family, _font.FamilyName, StringComparison.Ordinal))
        {
            return _font;
        }

        string key = string.Concat(family, "|", size.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture), "|", weight.ToString(), "|", italic ? "1" : "0");
        if (!_derivedFonts.TryGetValue(key, out var font))
        {
            font = new XPlatFont(family, size, weight, italic);
            _derivedFonts[key] = font;
        }

        return font;
    }

    private void ClearDerivedFonts()
    {
        foreach (var font in _derivedFonts.Values)
        {
            font.Dispose();
        }

        _derivedFonts.Clear();
    }

    private static SKColor WithAlpha(SKColor color, byte alpha) => new(color.Red, color.Green, color.Blue, alpha);

    private sealed class Fragment
    {
        public XPlatTextSpan Span = null!;
        public FragmentKind Kind;
        public string Text = string.Empty;
        public XPlatFont Font = XPlatTheme.DefaultFont;
        public float Width;
        public float Height;
        public float OffsetY;
        public SKRect Bounds;
    }

    private sealed class Line
    {
        public List<Fragment> Fragments { get; } = new();
        public float Width;
        public float Ascent;
        public float Height;
        public float Top;

        /// <summary>How far the line box was pushed down by controls overhanging the text top.</summary>
        public float Lead;
    }

    private sealed class Layout
    {
        public List<Line> Lines { get; } = new();
        public float Width;
        public float Height;
        public float WrapWidth;
    }
}
