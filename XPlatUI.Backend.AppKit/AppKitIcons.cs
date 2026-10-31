using AppKit;
using SkiaSharp;

namespace XPlatUI.Backend.AppKit;

/// <summary>
/// Turns an <see cref="XPlatIcon"/> into the <see cref="NSImage"/> AppKit wants.
/// </summary>
/// <remarks>
/// AppKit has no per-window icon: what a Mac shows is the application icon in the Dock and the app
/// switcher, so the application and its windows both funnel through here.
/// </remarks>
internal static class AppKitIcons
{
    /// <summary>
    /// The size the icon is rasterized to. 512 pixels covers a 256pt Dock tile at 2x, and AppKit
    /// downscales from there for the smaller places it shows the icon.
    /// </summary>
    private const int RasterSize = 512;

    /// <summary>Returns <paramref name="icon"/> as an <see cref="NSImage"/>, or null when it has no pixels.</summary>
    internal static NSImage? Create(XPlatIcon? icon)
    {
        // GTK-style theme-name icons carry no pixels, and a Mac has nothing to look the name up in.
        if (icon?.GetBitmap(RasterSize) is not { } bitmap)
        {
            return null;
        }

        // NSImage decodes PNG itself, which keeps this free of a hand-built bitmap rep.
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        using var stream = encoded.AsStream();

        return NSImage.FromStream(stream);
    }
}
