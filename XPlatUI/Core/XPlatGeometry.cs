using SkiaSharp;

namespace XPlatUI;

/// <summary>Small geometry helpers shared by the layout engine.</summary>
public static class XPlatGeometry
{
    /// <summary>Returns a rectangle with non-negative width and height.</summary>
    public static SKRect Normalize(SKRect rect)
    {
        float left = MathF.Min(rect.Left, rect.Right);
        float top = MathF.Min(rect.Top, rect.Bottom);
        float right = MathF.Max(rect.Left, rect.Right);
        float bottom = MathF.Max(rect.Top, rect.Bottom);
        return new SKRect(left, top, right, bottom);
    }

    public static SKRect Deflate(SKRect rect, XPlatThickness thickness) =>
        Normalize(new SKRect(
            rect.Left + thickness.Left,
            rect.Top + thickness.Top,
            rect.Right - thickness.Right,
            rect.Bottom - thickness.Bottom));

    public static SKRect Deflate(SKRect rect, float amount) =>
        Deflate(rect, new XPlatThickness(amount));

    public static SKRect Inflate(SKRect rect, float amount) =>
        Normalize(new SKRect(rect.Left - amount, rect.Top - amount, rect.Right + amount, rect.Bottom + amount));

    public static bool Contains(SKRect rect, SKPoint point) =>
        point.X >= rect.Left && point.X < rect.Right && point.Y >= rect.Top && point.Y < rect.Bottom;

    public static SKPoint Center(SKRect rect) =>
        new(rect.Left + (rect.Width / 2f), rect.Top + (rect.Height / 2f));

    /// <summary>Places a box of <paramref name="size"/> inside <paramref name="container"/>.</summary>
    public static SKRect Align(SKSize size, SKRect container, XPlatContentAlignment alignment)
    {
        float x = alignment switch
        {
            XPlatContentAlignment.TopCenter or XPlatContentAlignment.MiddleCenter or XPlatContentAlignment.BottomCenter
                => container.Left + ((container.Width - size.Width) / 2f),
            XPlatContentAlignment.TopRight or XPlatContentAlignment.MiddleRight or XPlatContentAlignment.BottomRight
                => container.Right - size.Width,
            _ => container.Left,
        };

        float y = alignment switch
        {
            XPlatContentAlignment.MiddleLeft or XPlatContentAlignment.MiddleCenter or XPlatContentAlignment.MiddleRight
                => container.Top + ((container.Height - size.Height) / 2f),
            XPlatContentAlignment.BottomLeft or XPlatContentAlignment.BottomCenter or XPlatContentAlignment.BottomRight
                => container.Bottom - size.Height,
            _ => container.Top,
        };

        return new SKRect(x, y, x + size.Width, y + size.Height);
    }
}
