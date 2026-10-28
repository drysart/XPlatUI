using SkiaSharp;

namespace XPlatUI;

/// <summary>
/// Draws an <see cref="SKImage"/>. The control owns the image when it was loaded
/// through one of the <c>Load</c> helpers or when <see cref="DisposeImageWithControl"/> is set.
/// </summary>
public class XPlatImage : XPlatControl
{
    private SKImage? _image;
    private bool _ownsImage;
    private XPlatSizeMode _sizeMode = XPlatSizeMode.Zoom;

    public XPlatImage()
    {
        AutoSize = true;
    }

    /// <summary>Raised when the image fails to load (for example a missing file).</summary>
    public event EventHandler<Exception>? LoadFailed;

    /// <summary>The image to display. Not disposed unless ownership was transferred.</summary>
    public SKImage? Image
    {
        get => _image;
        set
        {
            if (ReferenceEquals(_image, value))
            {
                return;
            }

            ReleaseImage();
            _image = value;
            Invalidate();
        }
    }

    public XPlatSizeMode SizeMode
    {
        get => _sizeMode;
        set
        {
            if (_sizeMode == value)
            {
                return;
            }

            _sizeMode = value;
            Invalidate();
        }
    }

    public XPlatContentAlignment ImageAlign { get; set; } = XPlatContentAlignment.MiddleCenter;

    /// <summary>When true, the image is disposed together with the control.</summary>
    public bool DisposeImageWithControl { get; set; }

    /// <summary>Loads an image from disk, replacing the current one. Returns false when loading fails.</summary>
    public bool Load(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return Load(stream);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            LoadFailed?.Invoke(this, ex);
            return false;
        }
    }

    /// <summary>Decodes an image from a stream, replacing the current one.</summary>
    public bool Load(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        SKImage? decoded = null;
        try
        {
            using var bitmap = SKBitmap.Decode(stream);
            if (bitmap is null)
            {
                return false;
            }

            decoded = SKImage.FromBitmap(bitmap);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            LoadFailed?.Invoke(this, ex);
            return false;
        }

        ReleaseImage();
        _image = decoded;
        _ownsImage = true;
        PerformLayout();
        Invalidate();
        return true;
    }

    /// <summary>Draws a solid color placeholder instead of an image.</summary>
    public SKColor? PlaceholderColor { get; set; }

    protected override SKSize MeasureContent(SKSize availableSize)
    {
        if (_image is null)
        {
            return SKSize.Empty;
        }

        return new SKSize(_image.Width, _image.Height);
    }

    protected override void OnPaintBackground(SKCanvas canvas, SKRect rect)
    {
        base.OnPaintBackground(canvas, rect);

        if (PlaceholderColor is { } placeholder && _image is null)
        {
            XPlatPainter.Fill(canvas, rect, placeholder, CornerRadius);
        }
    }

    protected override void OnPaint(SKCanvas canvas, SKRect rect)
    {
        if (_image is null)
        {
            return;
        }

        var content = XPlatGeometry.Deflate(rect, Padding);
        if (content.Width <= 0f || content.Height <= 0f)
        {
            return;
        }

        var destination = ComputeDestination(content, _image.Width, _image.Height, _sizeMode, ImageAlign);
        if (destination.Width <= 0f || destination.Height <= 0f)
        {
            return;
        }

        using var paint = new SKPaint
        {
            IsAntialias = true,
        };

        canvas.DrawImage(_image, destination, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear), paint);
    }

    private static SKRect ComputeDestination(SKRect content, int imageWidth, int imageHeight, XPlatSizeMode sizeMode, XPlatContentAlignment alignment)
    {
        if (imageWidth <= 0 || imageHeight <= 0)
        {
            return SKRect.Empty;
        }

        var natural = new SKSize(imageWidth, imageHeight);

        return sizeMode switch
        {
            XPlatSizeMode.StretchImage => content,
            XPlatSizeMode.CenterImage => XPlatGeometry.Align(natural, content, alignment),
            XPlatSizeMode.Zoom => XPlatGeometry.Align(ScaleToFit(natural, content.Size), content, alignment),
            _ => XPlatGeometry.Align(natural, content, alignment),
        };
    }

    private static SKSize ScaleToFit(SKSize source, SKSize bounds)
    {
        if (source.Width <= 0f || source.Height <= 0f)
        {
            return SKSize.Empty;
        }

        float scale = MathF.Min(bounds.Width / source.Width, bounds.Height / source.Height);
        return new SKSize(source.Width * scale, source.Height * scale);
    }

    private void ReleaseImage()
    {
        if (_ownsImage || DisposeImageWithControl)
        {
            _image?.Dispose();
        }

        _image = null;
        _ownsImage = false;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            ReleaseImage();
        }

        base.Dispose(disposing);
    }
}
