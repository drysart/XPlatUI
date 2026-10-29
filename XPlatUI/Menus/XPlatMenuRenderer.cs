using SkiaSharp;

namespace XPlatUI;

/// <summary>
/// Measurements of a popup menu, in logical pixels at 100% scale. The defaults follow the Windows
/// 11 context menu (32 px rows, 8 px corners, 6 px padding) so that a menu drawn with
/// <see cref="XPlatMenuRenderer"/> is indistinguishable from the native one.
/// </summary>
public readonly record struct XPlatMenuMetrics
{
    /// <summary>Height of a normal item row.</summary>
    public float ItemHeight { get; init; }

    /// <summary>Height of a separator row.</summary>
    public float SeparatorHeight { get; init; }

    /// <summary>Padding between the menu edge and the first and last row, and left of the check column.</summary>
    public float Padding { get; init; }

    /// <summary>Width reserved for the check mark to the left of every label.</summary>
    public float CheckColumnWidth { get; init; }

    /// <summary>Minimum space between a label and its shortcut hint.</summary>
    public float ShortcutGap { get; init; }

    /// <summary>Width a menu never goes below.</summary>
    public float MinWidth { get; init; }

    /// <summary>Corner radius of the menu itself.</summary>
    public float CornerRadius { get; init; }

    /// <summary>Corner radius of the highlight drawn behind the row under the pointer.</summary>
    public float ItemCornerRadius { get; init; }

    /// <summary>How far the drop shadow reaches past the menu on every side.</summary>
    public float ShadowExtent { get; init; }

    public static XPlatMenuMetrics Default => new()
    {
        ItemHeight = 32f,
        SeparatorHeight = 9f,
        Padding = 6f,
        CheckColumnWidth = 22f,
        ShortcutGap = 24f,
        MinWidth = 132f,
        CornerRadius = 8f,
        ItemCornerRadius = 4f,
        ShadowExtent = 12f,
    };
}

/// <summary>
/// One laid-out row of a menu: the item plus the rectangle the pointer has to be inside to hit it.
/// <see cref="Bounds"/> is in the drawing surface's coordinates, so it already includes any shadow
/// margin the layout reserved.
/// </summary>
public readonly record struct XPlatMenuRow(XPlatMenuItem Item, SKRect Bounds)
{
    /// <summary>True when picking this row should do something: a real item that is enabled.</summary>
    public bool IsPickable => !Item.IsSeparator && Item.Enabled;
}

/// <summary>
/// The result of measuring a menu: how big it is and where every row sits. Layout is separate from
/// painting so a platform layer can measure once, then repaint on every hover change.
/// </summary>
public sealed class XPlatMenuLayout
{
    private readonly XPlatMenuRow[] _rows;

    internal XPlatMenuLayout(SKSize surfaceSize, float shadowExtent, XPlatMenuRow[] rows)
    {
        SurfaceSize = surfaceSize;
        ShadowExtent = shadowExtent;
        _rows = rows;
        FirstPickable = -1;

        for (var i = 0; i < rows.Length; i++)
        {
            if (!rows[i].IsPickable)
            {
                continue;
            }

            FirstPickable = i;
            break;
        }
    }

    /// <summary>Size of the menu itself, without the shadow margin.</summary>
    public SKSize SurfaceSize { get; }

    /// <summary>How far the shadow reaches past the menu. Zero when the layout has no shadow.</summary>
    public float ShadowExtent { get; }

    /// <summary>True when the drawing surface reserves room for a drop shadow.</summary>
    public bool HasShadow => ShadowExtent > 0f;

    /// <summary>Size of the surface to draw into, shadow margin included.</summary>
    public SKSize WindowSize => new(SurfaceSize.Width + (ShadowExtent * 2f), SurfaceSize.Height + (ShadowExtent * 2f));

    /// <summary>Where the menu itself sits inside the drawing surface.</summary>
    public SKRect SurfaceBounds => SKRect.Create(ShadowExtent, ShadowExtent, SurfaceSize.Width, SurfaceSize.Height);

    /// <summary>The rows in the order they are drawn.</summary>
    public IReadOnlyList<XPlatMenuRow> Rows => _rows;

    /// <summary>Index of the first enabled item, or -1 when the menu has none.</summary>
    public int FirstPickable { get; }

    /// <summary>Index of the row under <paramref name="point"/>, or -1 when it is over no row.</summary>
    public int HitTest(SKPoint point)
    {
        for (var i = 0; i < _rows.Length; i++)
        {
            if (_rows[i].Bounds.Contains(point))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// The next enabled item in <paramref name="direction"/> (+1 down, -1 up), wrapping around the
    /// ends the way a native menu does. Returns <paramref name="from"/> when nothing is pickable.
    /// </summary>
    public int NextPickable(int from, int direction)
    {
        if (FirstPickable < 0 || _rows.Length == 0)
        {
            return from;
        }

        var step = direction >= 0 ? 1 : -1;
        var index = from;

        for (var guard = 0; guard < _rows.Length; guard++)
        {
            index += step;

            if (index >= _rows.Length)
            {
                index = 0;
            }
            else if (index < 0)
            {
                index = _rows.Length - 1;
            }

            if (_rows[index].IsPickable)
            {
                return index;
            }
        }

        return from;
    }
}

/// <summary>
/// Draws an <see cref="XPlatContextMenu"/> with Skia in the modern Windows 11 style: a rounded
/// surface with a drop shadow, a pill behind the hovered row, a check column and right aligned
/// shortcut hints. It is deliberately free of platform code, so a backend that does not want a
/// native menu can hand it an <c>SKCanvas</c> and reuse this on macOS or Linux.
/// </summary>
/// <remarks>
/// Measuring and painting are separate so a platform layer can cache the layout and only repaint
/// when the hovered row changes. All sizes are logical pixels; the caller scales the canvas for
/// the display.
/// </remarks>
public static class XPlatMenuRenderer
{
    /// <summary>Opacity of the line between groups of items, applied to the text colour.</summary>
    private const byte SeparatorAlpha = 0x2C;

    /// <summary>Measures <paramref name="menu"/> and works out where each row sits.</summary>
    /// <param name="menu">The menu to measure.</param>
    /// <param name="metrics">Row metrics, or null for <see cref="XPlatMenuMetrics.Default"/>.</param>
    /// <param name="font">Label font, or null for <see cref="XPlatTheme.DefaultFont"/>.</param>
    /// <param name="shadow">True to reserve room for a drop shadow around the surface.</param>
    public static XPlatMenuLayout Measure(
        XPlatContextMenu menu,
        XPlatMenuMetrics? metrics = null,
        XPlatFont? font = null,
        bool shadow = true)
    {
        ArgumentNullException.ThrowIfNull(menu);

        var m = metrics ?? XPlatMenuMetrics.Default;
        var f = font ?? XPlatTheme.DefaultFont;
        var items = menu.TrimmedItems;

        var width = m.MinWidth;
        var height = m.Padding * 2f;

        foreach (var item in items)
        {
            if (item.IsSeparator)
            {
                height += m.SeparatorHeight;
                continue;
            }

            height += m.ItemHeight;

            var needed = (m.Padding * 2f) + m.CheckColumnWidth + FontFor(item, f).MeasureWidth(item.Text);
            if (!string.IsNullOrEmpty(item.ShortcutText))
            {
                needed += m.ShortcutGap + f.MeasureWidth(item.ShortcutText);
            }

            width = MathF.Max(width, needed);
        }

        width = MathF.Ceiling(width);
        height = MathF.Ceiling(height);

        var extent = shadow ? m.ShadowExtent : 0f;
        var rows = new XPlatMenuRow[items.Count];
        var y = extent + m.Padding;

        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var rowHeight = item.IsSeparator ? m.SeparatorHeight : m.ItemHeight;
            rows[i] = new XPlatMenuRow(item, SKRect.Create(extent, y, width, rowHeight));
            y += rowHeight;
        }

        return new XPlatMenuLayout(new SKSize(width, height), extent, rows);
    }

    /// <summary>Paints a measured menu onto <paramref name="canvas"/>.</summary>
    /// <param name="canvas">Target canvas, already scaled to the display's DPI.</param>
    /// <param name="layout">Layout from <see cref="Measure"/>.</param>
    /// <param name="hoveredIndex">Row to highlight, or -1.</param>
    /// <param name="surfaceAlpha">
    /// Opacity of the surface, 0 to 1. Below 1 the menu is translucent so a compositor-supplied
    /// backdrop (the Windows 11 acrylic blur) shows through; 1 paints it solid.
    /// </param>
    /// <param name="metrics">Row metrics, or null for <see cref="XPlatMenuMetrics.Default"/>.</param>
    /// <param name="font">Label font, or null for <see cref="XPlatTheme.DefaultFont"/>.</param>
    /// <remarks>
    /// The canvas is not cleared, so whatever the caller has already painted stays visible through a
    /// translucent surface.
    /// </remarks>
    public static void Paint(
        SKCanvas canvas,
        XPlatMenuLayout layout,
        int hoveredIndex = -1,
        float surfaceAlpha = 1f,
        XPlatMenuMetrics? metrics = null,
        XPlatFont? font = null)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(layout);

        var m = metrics ?? XPlatMenuMetrics.Default;
        var f = font ?? XPlatTheme.DefaultFont;
        var alpha = Math.Clamp(surfaceAlpha, 0f, 1f);
        var bounds = layout.SurfaceBounds;

        if (layout.HasShadow)
        {
            using var shadow = new SKPaint
            {
                IsAntialias = true,
                Color = new SKColor(0, 0, 0, XPlatTheme.IsDarkMode ? (byte)0x8C : (byte)0x3D),
                MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, layout.ShadowExtent * 0.32f),
            };

            // Offset downwards the way a raised surface casts a shadow, and keep the blur inside
            // the reserved margin so it never has a hard edge.
            var shadowBounds = bounds;
            shadowBounds.Offset(0f, 2f);
            shadowBounds.Inflate(-2f, -2f);
            canvas.DrawRoundRect(shadowBounds, m.CornerRadius, m.CornerRadius, shadow);
        }

        using (var fill = new SKPaint { IsAntialias = true, Color = Scale(XPlatTheme.Surface, alpha) })
        {
            canvas.DrawRoundRect(bounds, m.CornerRadius, m.CornerRadius, fill);
        }

        using (var edge = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1f,
            Color = Scale(XPlatTheme.Border, MathF.Max(alpha, 0.6f)),
        })
        {
            var edgeBounds = bounds;
            edgeBounds.Inflate(-0.5f, -0.5f);
            canvas.DrawRoundRect(edgeBounds, m.CornerRadius, m.CornerRadius, edge);
        }

        DrawHover(canvas, layout, hoveredIndex, m, alpha);

        var textLeft = bounds.Left + m.Padding + m.CheckColumnWidth;
        var textRight = bounds.Right - m.Padding;
        var checkCenter = bounds.Left + m.Padding + (m.CheckColumnWidth * 0.5f);

        using var text = new SKPaint { IsAntialias = true };

        // A separator tinted from the text colour keeps the same visual weight in both palettes;
        // the border colour is too close to a dark surface to read as a line.
        var foreground = XPlatTheme.Foreground;
        using var separator = new SKPaint
        {
            IsAntialias = false,
            StrokeWidth = 1f,
            Color = new SKColor(foreground.Red, foreground.Green, foreground.Blue, (byte)(SeparatorAlpha * alpha)),
        };

        foreach (var row in layout.Rows)
        {
            var item = row.Item;

            if (item.IsSeparator)
            {
                var y = MathF.Round(row.Bounds.MidY) + 0.5f;
                canvas.DrawLine(bounds.Left + m.Padding, y, bounds.Right - m.Padding, y, separator);
                continue;
            }

            var rowFont = FontFor(item, f);
            var baseline = row.Bounds.MidY + (rowFont.BaselineOffset - (rowFont.LineHeight * 0.5f));

            if (item.Checked)
            {
                DrawCheckMark(canvas, checkCenter, row.Bounds.MidY, Scale(XPlatTheme.Accent, MathF.Max(alpha, 0.85f)));
            }

            text.Color = Scale(item.Enabled ? XPlatTheme.Foreground : XPlatTheme.DisabledForeground, MathF.Max(alpha, 0.85f));
            canvas.DrawText(item.Text, textLeft, baseline, SKTextAlign.Left, rowFont.SkFont, text);

            if (!string.IsNullOrEmpty(item.ShortcutText))
            {
                text.Color = Scale(item.Enabled ? XPlatTheme.MutedForeground : XPlatTheme.DisabledForeground, MathF.Max(alpha, 0.85f));
                canvas.DrawText(item.ShortcutText, textRight, baseline, SKTextAlign.Right, f.SkFont, text);
            }
        }
    }

    private static void DrawHover(SKCanvas canvas, XPlatMenuLayout layout, int hoveredIndex, XPlatMenuMetrics m, float alpha)
    {
        if (hoveredIndex < 0 || hoveredIndex >= layout.Rows.Count || !layout.Rows[hoveredIndex].IsPickable)
        {
            return;
        }

        var bounds = layout.SurfaceBounds;
        var row = layout.Rows[hoveredIndex].Bounds;
        var inset = m.Padding * 0.5f;

        var pill = SKRect.Create(
            bounds.Left + inset,
            row.Top + 2f,
            bounds.Width - (inset * 2f),
            row.Height - 4f);

        using var paint = new SKPaint { IsAntialias = true, Color = Scale(XPlatTheme.Secondary, MathF.Max(alpha, 0.9f)) };
        canvas.DrawRoundRect(pill, m.ItemCornerRadius, m.ItemCornerRadius, paint);
    }

    private static void DrawCheckMark(SKCanvas canvas, float centerX, float centerY, SKColor color)
    {
        using var paint = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1.6f,
            StrokeCap = SKStrokeCap.Round,
            Color = color,
        };

        canvas.DrawLine(centerX - 4.5f, centerY, centerX - 1.5f, centerY + 3.2f, paint);
        canvas.DrawLine(centerX - 1.5f, centerY + 3.2f, centerX + 5f, centerY - 3.6f, paint);
    }

    private static XPlatFont FontFor(XPlatMenuItem item, XPlatFont font) =>
        item.IsDefault ? XPlatTheme.EmphasizedFont : font;

    private static SKColor Scale(SKColor color, float alpha) =>
        color.WithAlpha((byte)Math.Clamp(MathF.Round(alpha * 255f), 0f, 255f));
}
