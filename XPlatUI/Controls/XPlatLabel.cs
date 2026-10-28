using SkiaSharp;

namespace XPlatUI;

/// <summary>
/// Displays read-only text. Supports single- or multi-line content, alignment,
/// word wrapping and auto-sizing.
/// </summary>
public class XPlatLabel : XPlatControl
{
    private XPlatFont _font = XPlatTheme.DefaultFont;

    public XPlatLabel()
    {
        AutoSize = true;
        ForeColor = XPlatTheme.Foreground;
    }

    public XPlatLabel(string text)
        : this()
    {
        Text = text;
    }

    /// <summary>Font used to draw the text. The label does not take ownership of it.</summary>
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
            Invalidate();
        }
    }

    public XPlatContentAlignment TextAlign { get; set; } = XPlatContentAlignment.MiddleLeft;

    /// <summary>When true the text wraps at the control's width.</summary>
    public bool WrapText { get; set; }

    protected override SKSize MeasureContent(SKSize availableSize)
    {
        float maxWidth = WrapText
            ? MathF.Max(0f, availableSize.Width)
            : float.PositiveInfinity;

        var measured = XPlatTextRenderer.Measure(Text, _font, maxWidth, WrapText && !float.IsInfinity(maxWidth));
        return new SKSize(MathF.Ceiling(measured.Width), MathF.Ceiling(measured.Height));
    }

    protected override void OnPaint(SKCanvas canvas, SKRect rect)
    {
        if (string.IsNullOrEmpty(Text))
        {
            return;
        }

        var content = XPlatGeometry.Deflate(rect, Padding);
        XPlatTextRenderer.Draw(canvas, Text, _font, ForeColor, content, TextAlign, WrapText);
    }
}
