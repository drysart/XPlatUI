using SkiaSharp;
using XPlatUI;

namespace XPlatUI.Demo;

/// <summary>
/// The sample application's icon, drawn in code so the repository needs no binary asset.
/// </summary>
/// <remarks>
/// It is drawn rather than loaded on purpose: the same icon then reaches all three platforms from
/// one source, and the demo can show a pixel icon (Windows, macOS) and a theme name (GTK) coming out
/// of the same <see cref="XPlatIcon"/>.
/// </remarks>
public static class DemoIcon
{
    /// <summary>
    /// The icon theme name GTK looks up. GNOME's default Adwaita theme ships it, and a desktop
    /// without it simply shows no window icon, which is what any unresolvable name does.
    /// </summary>
    public const string ThemeName = "applications-development";

    /// <summary>The edge length the icon is drawn at, in pixels.</summary>
    private const int Size = 256;

    /// <summary>Creates the sample icon.</summary>
    /// <returns>An icon carrying both the drawn bitmap and <see cref="ThemeName"/>.</returns>
    public static XPlatIcon Create()
    {
        using var bitmap = new SKBitmap(new SKImageInfo(Size, Size, SKColorType.Bgra8888, SKAlphaType.Premul));

        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Transparent);

            // A rounded square with a light-to-dark vertical gradient, the shape most desktops use.
            var bounds = SKRect.Create(8f, 8f, Size - 16f, Size - 16f);
            using var background = new SKPaint
            {
                IsAntialias = true,
                Shader = SKShader.CreateLinearGradient(
                    new SKPoint(0f, bounds.Top),
                    new SKPoint(0f, bounds.Bottom),
                    [new SKColor(0x4C, 0x8D, 0xFF), new SKColor(0x1B, 0x4F, 0xD8)],
                    SKShaderTileMode.Clamp),
            };
            canvas.DrawRoundRect(bounds, 48f, 48f, background);

            // A white "X", stroked rather than typed so it needs no font and renders identically
            // everywhere.
            using var glyph = new SKPaint
            {
                IsAntialias = true,
                Color = SKColors.White,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 26f,
                StrokeCap = SKStrokeCap.Round,
            };
            canvas.DrawLine(78f, 78f, Size - 78f, Size - 78f, glyph);
            canvas.DrawLine(Size - 78f, 78f, 78f, Size - 78f, glyph);
        }

        var icon = XPlatIcon.FromBitmap(bitmap);
        icon.Name = ThemeName;
        return icon;
    }
}
