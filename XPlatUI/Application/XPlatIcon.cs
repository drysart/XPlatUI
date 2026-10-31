using SkiaSharp;

namespace XPlatUI;

/// <summary>
/// An icon: a bitmap the platform scales to whatever sizes it needs, and optionally a name from the
/// desktop's icon theme.
/// </summary>
/// <remarks>
/// <para>
/// One type covers three platforms whose icon APIs have nothing in common. Windows wants a pair of
/// <c>HICON</c> handles built at the title bar's and the taskbar's pixel sizes, macOS has a single
/// application icon that every window shares, and GTK4 only accepts the <em>name</em> of an icon in
/// the current theme. A pixel icon therefore reaches Windows and macOS and is ignored by GTK, and
/// <see cref="Name"/> is the GTK half; set both to have an icon everywhere.
/// </para>
/// <para>
/// Scaling is cached per requested size, so a backend that asks again after a DPI change pays for
/// the resize once. The bitmaps the icon hands out belong to it and must not be disposed.
/// </para>
/// <para>
/// An icon can be loaded before an <see cref="XPlatApplication"/> exists and shared by several
/// windows, but it must not be disposed while a window still refers to it.
/// </para>
/// </remarks>
public sealed class XPlatIcon : IDisposable
{
    private readonly Dictionary<int, SKBitmap> _scaled = [];
    private readonly SKBitmap? _source;
    private bool _disposed;

    private XPlatIcon(SKBitmap? source, string? name)
    {
        _source = source;
        Name = name;
    }

    /// <summary>
    /// A name from the desktop's icon theme, which is how GTK4 takes an icon. Null when the icon is
    /// pixels only.
    /// </summary>
    /// <remarks>
    /// Settable so a pixel icon can also carry a theme name: Windows and macOS use the pixels and
    /// ignore the name, GTK uses the name and ignores the pixels.
    /// </remarks>
    public string? Name { get; set; }

    /// <summary>True when the icon has a bitmap; false for a theme-name-only icon.</summary>
    public bool HasPixels => _source is not null;

    /// <summary>The source bitmap's size in pixels, or <see cref="SKSizeI.Empty"/> for a name-only icon.</summary>
    public SKSizeI PixelSize => _source is null ? SKSizeI.Empty : new SKSizeI(_source.Width, _source.Height);

    /// <summary>Loads an icon from an image file (PNG, JPEG, BMP, ICO...).</summary>
    /// <param name="path">Path to the image.</param>
    /// <exception cref="ArgumentException">The file does not contain an image the decoder knows.</exception>
    /// <exception cref="IOException">The file cannot be read.</exception>
    public static XPlatIcon FromFile(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        using var stream = File.OpenRead(path);
        return FromStream(stream);
    }

    /// <summary>Decodes an icon from a stream.</summary>
    /// <param name="stream">A readable stream holding the encoded image.</param>
    /// <exception cref="ArgumentException">The stream does not contain an image the decoder knows.</exception>
    public static XPlatIcon FromStream(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var bitmap = SKBitmap.Decode(stream)
            ?? throw new ArgumentException("The stream does not contain an image the decoder recognises.", nameof(stream));

        using (bitmap)
        {
            return FromBitmap(bitmap);
        }
    }

    /// <summary>
    /// Copies <paramref name="bitmap"/> into an icon, converting it to the premultiplied BGRA layout
    /// the platform backends need. The caller keeps ownership of <paramref name="bitmap"/>.
    /// </summary>
    /// <param name="bitmap">The image to use.</param>
    /// <exception cref="ArgumentException">The bitmap has no pixels.</exception>
    public static XPlatIcon FromBitmap(SKBitmap bitmap)
    {
        ArgumentNullException.ThrowIfNull(bitmap);

        if (bitmap.Width <= 0 || bitmap.Height <= 0)
        {
            throw new ArgumentException("The bitmap is empty.", nameof(bitmap));
        }

        return new XPlatIcon(Copy(bitmap), null);
    }

    /// <summary>Creates an icon that is only a theme name, for a desktop that takes no bitmap.</summary>
    /// <param name="name">The icon theme name, for example <c>"folder"</c>.</param>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty.</exception>
    public static XPlatIcon FromName(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        return new XPlatIcon(null, name);
    }

    /// <summary>
    /// Returns the icon as a square bitmap of <paramref name="size"/> pixels, scaled down to fit and
    /// centred if it is not square already. Null for a name-only icon.
    /// </summary>
    /// <param name="size">The requested edge length in physical pixels.</param>
    /// <returns>A bitmap owned by this icon; do not dispose it.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="size"/> is not positive.</exception>
    public SKBitmap? GetBitmap(int size)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(size);

        if (_source is null)
        {
            return null;
        }

        if (_source.Width == size && _source.Height == size)
        {
            return _source;
        }

        if (_scaled.TryGetValue(size, out var cached))
        {
            return cached;
        }

        var scaled = ScaleTo(_source, size);
        _scaled[size] = scaled;
        return scaled;
    }

    /// <summary>Releases the source bitmap and every cached size.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        foreach (var bitmap in _scaled.Values)
        {
            bitmap.Dispose();
        }

        _scaled.Clear();
        _source?.Dispose();
    }

    /// <summary>Normalises any colour type to the premultiplied BGRA the backends read pixels from.</summary>
    private static SKBitmap Copy(SKBitmap source)
    {
        var copy = new SKBitmap(new SKImageInfo(source.Width, source.Height, SKColorType.Bgra8888, SKAlphaType.Premul));

        using var canvas = new SKCanvas(copy);
        using var image = SKImage.FromBitmap(source);

        canvas.Clear(SKColors.Transparent);
        canvas.DrawImage(
            image,
            new SKRect(0f, 0f, source.Width, source.Height),
            new SKSamplingOptions(SKFilterMode.Nearest),
            paint: null);

        return copy;
    }

    private static SKBitmap ScaleTo(SKBitmap source, int size)
    {
        var scaled = new SKBitmap(new SKImageInfo(size, size, SKColorType.Bgra8888, SKAlphaType.Premul));

        // Preserve the aspect ratio: a 32x16 bitmap becomes a 32x16 strip centred in the square,
        // not a stretched square, so the icon keeps its shape in the title bar.
        var factor = MathF.Min(size / (float)source.Width, size / (float)source.Height);
        var width = MathF.Max(1f, MathF.Round(source.Width * factor));
        var height = MathF.Max(1f, MathF.Round(source.Height * factor));
        var destination = SKRect.Create((size - width) / 2f, (size - height) / 2f, width, height);

        using var canvas = new SKCanvas(scaled);
        using var image = SKImage.FromBitmap(source);

        canvas.Clear(SKColors.Transparent);
        canvas.DrawImage(
            image,
            destination,
            new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear),
            paint: null);

        return scaled;
    }
}
