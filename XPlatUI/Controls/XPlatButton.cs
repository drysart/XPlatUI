using SkiaSharp;

namespace XPlatUI;

/// <summary>Visual style of an <see cref="XPlatButton"/>.</summary>
public enum XPlatButtonStyle
{
    /// <summary>Filled with the accent color. Use for the primary action on a view.</summary>
    Primary,

    /// <summary>Muted fill with a border. Use for secondary actions.</summary>
    Secondary,

    /// <summary>No fill or border; text only.</summary>
    Ghost,
}

/// <summary>
/// A push button with hover, pressed, focused and disabled states. It raises
/// <see cref="XPlatControl.Click"/> on mouse release inside the button or on
/// Enter/Space while focused.
/// </summary>
public class XPlatButton : XPlatControl
{
    private XPlatFont _font = XPlatTheme.EmphasizedFont;
    private XPlatButtonStyle _style = XPlatButtonStyle.Primary;

    public XPlatButton()
    {
        TabStop = true;
        Cursor = XPlatCursor.Hand;
        CornerRadius = 6f;
        Padding = new XPlatThickness(14f, 8f);
        MinimumSize = new SKSize(0f, 32f);
    }

    public XPlatButton(string text)
        : this()
    {
        Text = text;
    }

    public XPlatFont Font
    {
        get => _font;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            _font = value;
            Invalidate();
        }
    }

    public XPlatButtonStyle Style
    {
        get => _style;
        set
        {
            if (_style == value)
            {
                return;
            }

            _style = value;
            Invalidate();
        }
    }

    protected override SKSize MeasureContent(SKSize availableSize)
    {
        var measured = XPlatTextRenderer.Measure(Text, _font, float.PositiveInfinity, wrap: false);
        return new SKSize(MathF.Ceiling(measured.Width), MathF.Ceiling(measured.Height));
    }

    protected override void OnPaintBackground(SKCanvas canvas, SKRect rect)
    {
        var (fill, _) = ResolveColors();
        if (fill is { } color)
        {
            XPlatPainter.Fill(canvas, rect, color, CornerRadius);
        }

        if (_style == XPlatButtonStyle.Secondary && Enabled)
        {
            XPlatPainter.DrawBorder(canvas, rect, XPlatTheme.Border, 1f, CornerRadius);
        }

        if (IsFocused && Enabled)
        {
            XPlatPainter.DrawFocusRing(canvas, rect, XPlatTheme.FocusRing, 2f, CornerRadius);
        }
    }

    protected override void OnPaint(SKCanvas canvas, SKRect rect)
    {
        if (string.IsNullOrEmpty(Text))
        {
            return;
        }

        var (_, textColor) = ResolveColors();
        var content = XPlatGeometry.Deflate(rect, Padding);
        XPlatTextRenderer.Draw(canvas, Text, _font, textColor, content, XPlatContentAlignment.MiddleCenter);
    }

    protected override void OnPaintBorder(SKCanvas canvas, SKRect rect)
    {
        // Borders and focus rings are drawn in OnPaintBackground so they stay behind the text.
    }

    protected override void OnMouseDown(XPlatMouseEventArgs e)
    {
        base.OnMouseDown(e);

        if (e.Button == XPlatMouseButtons.Left)
        {
            IsPressed = true;
            CaptureMouse();
            Invalidate();
        }
    }

    protected override void OnMouseUp(XPlatMouseEventArgs e)
    {
        base.OnMouseUp(e);

        if (e.Button != XPlatMouseButtons.Left)
        {
            return;
        }

        bool wasPressed = IsPressed;
        IsPressed = false;
        ReleaseMouseCapture();
        Invalidate();

        if (wasPressed && XPlatGeometry.Contains(ClientRect, e.Location))
        {
            OnClick(EventArgs.Empty);
        }
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);

        if (IsPressed)
        {
            IsPressed = false;
            ReleaseMouseCapture();
        }

        Invalidate();
    }

    protected override void OnKeyDown(XPlatKeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (e.Key is XPlatKey.Enter or XPlatKey.Space)
        {
            IsPressed = true;
            e.Handled = true;
            Invalidate();
        }
    }

    protected override void OnKeyUp(XPlatKeyEventArgs e)
    {
        base.OnKeyUp(e);

        if (e.Key is XPlatKey.Enter or XPlatKey.Space && IsPressed)
        {
            IsPressed = false;
            e.Handled = true;
            Invalidate();
            OnClick(EventArgs.Empty);
        }
    }

    protected override void OnGotFocus(EventArgs e)
    {
        base.OnGotFocus(e);
        Invalidate();
    }

    protected override void OnLostFocus(EventArgs e)
    {
        base.OnLostFocus(e);
        IsPressed = false;
        Invalidate();
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        if (!Enabled)
        {
            IsPressed = false;
            ReleaseMouseCapture();
        }

        base.OnEnabledChanged(e);
    }

    private (SKColor? Fill, SKColor Text) ResolveColors()
    {
        if (!Enabled)
        {
            return _style switch
            {
                XPlatButtonStyle.Ghost => (null, XPlatTheme.DisabledForeground),
                _ => (XPlatTheme.DisabledBackground, XPlatTheme.DisabledForeground),
            };
        }

        return _style switch
        {
            XPlatButtonStyle.Primary => (
                IsPressed ? XPlatTheme.AccentPressed : IsMouseOver ? XPlatTheme.AccentHover : XPlatTheme.Accent,
                XPlatTheme.AccentForeground),
            XPlatButtonStyle.Secondary => (
                IsPressed ? XPlatTheme.SecondaryPressed : IsMouseOver ? XPlatTheme.SecondaryHover : XPlatTheme.Secondary,
                XPlatTheme.Foreground),
            _ => (
                IsPressed ? XPlatTheme.SecondaryPressed : IsMouseOver ? XPlatTheme.SecondaryHover : (SKColor?)null,
                XPlatTheme.Accent),
        };
    }
}
