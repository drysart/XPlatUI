using SkiaSharp;

namespace XPlatUI;

/// <summary>Low level drawing helpers used by the built-in controls.</summary>
public static class XPlatPainter
{
    public static void Fill(SKCanvas canvas, SKRect rect, SKColor color, float cornerRadius = 0f)
    {
        if (color.Alpha == 0 || rect.Width <= 0f || rect.Height <= 0f)
        {
            return;
        }

        using var paint = new SKPaint
        {
            Color = color,
            IsAntialias = true,
            Style = SKPaintStyle.Fill,
        };

        if (cornerRadius > 0f)
        {
            canvas.DrawRoundRect(rect, cornerRadius, cornerRadius, paint);
        }
        else
        {
            canvas.DrawRect(rect, paint);
        }
    }

    public static void DrawBorder(SKCanvas canvas, SKRect rect, SKColor color, float width, float cornerRadius = 0f)
    {
        if (width <= 0f || color.Alpha == 0 || rect.Width <= 0f || rect.Height <= 0f)
        {
            return;
        }

        using var paint = new SKPaint
        {
            Color = color,
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = width,
        };

        // Stroke is centered on the path, so inset by half the stroke width to keep it inside the bounds.
        var strokeRect = XPlatGeometry.Deflate(rect, width / 2f);
        if (cornerRadius > 0f)
        {
            canvas.DrawRoundRect(strokeRect, MathF.Max(0f, cornerRadius - (width / 2f)), MathF.Max(0f, cornerRadius - (width / 2f)), paint);
        }
        else
        {
            canvas.DrawRect(strokeRect, paint);
        }
    }

    /// <summary>Draws a focus rectangle just inside <paramref name="rect"/>.</summary>
    public static void DrawFocusRing(SKCanvas canvas, SKRect rect, SKColor color, float width = 2f, float cornerRadius = 0f)
    {
        var ring = XPlatGeometry.Deflate(rect, width + 1f);
        DrawBorder(canvas, ring, color, width, MathF.Max(0f, cornerRadius - width));
    }
}
