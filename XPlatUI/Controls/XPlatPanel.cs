using SkiaSharp;

namespace XPlatUI;

/// <summary>
/// A container control that hosts child <see cref="XPlatControl"/> instances.
/// Children can be docked or positioned absolutely; docked children are arranged
/// first (in reverse z-order, matching WinForms), then remaining space is left for
/// absolutely positioned children.
/// </summary>
public class XPlatPanel : XPlatControl
{
    public XPlatPanel()
    {
        BackColor = XPlatTheme.Surface;
        BorderWidth = 1f;
        CornerRadius = 8f;
        Padding = new XPlatThickness(8f);
    }

    /// <summary>When true the panel paints its background and border. Set false for a plain grouping container.</summary>
    public bool ShowFrame { get; set; } = true;

    /// <summary>When true the panel paints itself like a card: filled surface plus a 1px border.</summary>
    public bool IsCard { get; set; } = true;

    /// <summary>When true, docked children are arranged and the panel grows to fit them (requires AutoSize).</summary>
    public bool AutoScroll { get; set; }

    protected override SKSize MeasureContent(SKSize availableSize)
    {
        float width = 0f;
        float height = 0f;

        foreach (var child in Controls)
        {
            if (!child.Visible || child.Dock == DockStyle.Fill)
            {
                continue;
            }

            var childSize = child.Measure(availableSize);
            width = MathF.Max(width, childSize.Width + child.Margin.Horizontal);
            height += childSize.Height + child.Margin.Vertical;
        }

        return new SKSize(width, height);
    }

    /// <summary>Arranges all children inside the content rectangle.</summary>
    public override void PerformLayout()
    {
        var content = ContentRect;
        var remaining = content;

        // Docking is applied in declaration order: the first docked child gets the edge,
        // subsequent ones fill the space that is left over. Fill children are applied last
        // so their position in the list does not matter.
        List<XPlatControl>? fillChildren = null;
        foreach (var child in Controls)
        {
            if (!child.Visible || child.Dock == DockStyle.None)
            {
                continue;
            }

            if (child.Dock == DockStyle.Fill)
            {
                (fillChildren ??= new List<XPlatControl>()).Add(child);
                continue;
            }

            remaining = DockChild(child, remaining);
        }

        if (fillChildren is not null)
        {
            foreach (var child in fillChildren)
            {
                child.Bounds = XPlatGeometry.Deflate(remaining, child.Margin);
            }
        }

        foreach (var child in Controls)
        {
            if (!child.Visible || child.Dock != DockStyle.None)
            {
                continue;
            }

            if (child.AutoSize)
            {
                var desired = child.Measure(new SKSize(content.Width, content.Height));
                child.Size = new SKSize(desired.Width, desired.Height);
            }
        }
    }

    private static SKRect DockChild(XPlatControl child, SKRect remaining)
    {
        var margin = child.Margin;
        var available = new SKSize(
            MathF.Max(0f, remaining.Width - margin.Horizontal),
            MathF.Max(0f, remaining.Height - margin.Vertical));

        var desired = child.AutoSize ? child.Measure(available) : child.Size;

        switch (child.Dock)
        {
            case DockStyle.Top:
            {
                float height = child.AutoSize ? desired.Height : MathF.Max(desired.Height, 0f);
                var bounds = new SKRect(
                    remaining.Left + margin.Left,
                    remaining.Top + margin.Top,
                    remaining.Right - margin.Right,
                    remaining.Top + margin.Top + height);
                child.Bounds = bounds;
                return new SKRect(remaining.Left, bounds.Bottom + margin.Bottom, remaining.Right, remaining.Bottom);
            }

            case DockStyle.Bottom:
            {
                float height = child.AutoSize ? desired.Height : MathF.Max(desired.Height, 0f);
                var bounds = new SKRect(
                    remaining.Left + margin.Left,
                    remaining.Bottom - margin.Bottom - height,
                    remaining.Right - margin.Right,
                    remaining.Bottom - margin.Bottom);
                child.Bounds = bounds;
                return new SKRect(remaining.Left, remaining.Top, remaining.Right, bounds.Top - margin.Top);
            }

            case DockStyle.Left:
            {
                float width = child.AutoSize ? desired.Width : MathF.Max(desired.Width, 0f);
                var bounds = new SKRect(
                    remaining.Left + margin.Left,
                    remaining.Top + margin.Top,
                    remaining.Left + margin.Left + width,
                    remaining.Bottom - margin.Bottom);
                child.Bounds = bounds;
                return new SKRect(bounds.Right + margin.Right, remaining.Top, remaining.Right, remaining.Bottom);
            }

            case DockStyle.Right:
            {
                float width = child.AutoSize ? desired.Width : MathF.Max(desired.Width, 0f);
                var bounds = new SKRect(
                    remaining.Right - margin.Right - width,
                    remaining.Top + margin.Top,
                    remaining.Right - margin.Right,
                    remaining.Bottom - margin.Bottom);
                child.Bounds = bounds;
                return new SKRect(remaining.Left, remaining.Top, bounds.Left - margin.Left, remaining.Bottom);
            }

            case DockStyle.Fill:
                child.Bounds = new SKRect(
                    remaining.Left + margin.Left,
                    remaining.Top + margin.Top,
                    remaining.Right - margin.Right,
                    remaining.Bottom - margin.Bottom);
                return SKRect.Empty;

            default:
                return remaining;
        }
    }

    protected override void OnPaintBackground(SKCanvas canvas, SKRect rect)
    {
        if (ShowFrame && BackColor is { } backColor)
        {
            XPlatPainter.Fill(canvas, rect, backColor, CornerRadius);
            return;
        }

        base.OnPaintBackground(canvas, rect);
    }

    protected override void OnPaintBorder(SKCanvas canvas, SKRect rect)
    {
        if (ShowFrame && IsCard)
        {
            XPlatPainter.DrawBorder(canvas, rect, BorderColor, BorderWidth <= 0f ? 1f : BorderWidth, CornerRadius);
            return;
        }

        base.OnPaintBorder(canvas, rect);
    }
}
