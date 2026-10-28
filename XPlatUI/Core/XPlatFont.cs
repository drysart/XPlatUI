using SkiaSharp;

namespace XPlatUI;

/// <summary>
/// A lightweight, immutable font description backed by an <see cref="SKFont"/>.
/// Instances are owned by the caller; controls never dispose fonts they did not create.
/// </summary>
public sealed class XPlatFont : IDisposable
{
    private readonly SKTypeface? _typeface;
    private readonly SKFont _font;
    private readonly float _lineHeight;
    private readonly float _baselineOffset;
    private bool _disposed;

    public XPlatFont(float size, bool bold = false, bool italic = false)
        : this(null, size, bold, italic)
    {
    }

    public XPlatFont(string? familyName, float size, bool bold = false, bool italic = false)
        : this(familyName, size, bold ? 700 : 400, italic)
    {
    }

    public XPlatFont(float size, int weight, bool italic = false)
        : this(null, size, weight, italic)
    {
    }

    public XPlatFont(string? familyName, float size, int weight, bool italic = false)
    {
        Size = size;
        Weight = weight;
        Italic = italic;

        var style = new SKFontStyle(weight, (int)SKFontStyleWidth.Normal, italic ? SKFontStyleSlant.Italic : SKFontStyleSlant.Upright);
        var typeface = SKTypeface.FromFamilyName(familyName, style);
        if (typeface is null || typeface == SKTypeface.Empty)
        {
            typeface = SKTypeface.Default;
        }
        else
        {
            _typeface = typeface;
        }

        _font = new SKFont(typeface, size)
        {
            Subpixel = true,
            Edging = SKFontEdging.Antialias,
        };

        var metrics = _font.Metrics;
        _baselineOffset = -metrics.Ascent;
        _lineHeight = -metrics.Ascent + metrics.Descent + metrics.Leading;
    }

    public float Size { get; }

    public int Weight { get; }

    public bool Italic { get; }

    public string FamilyName => _font.Typeface?.FamilyName ?? string.Empty;

    /// <summary>Distance between two baselines, in logical pixels.</summary>
    public float LineHeight => _lineHeight;

    /// <summary>Distance from the top of a line box to the baseline.</summary>
    public float BaselineOffset => _baselineOffset;

    internal SKFont SkFont => _font;

    public float MeasureWidth(string? text) =>
        string.IsNullOrEmpty(text) ? 0f : _font.MeasureText(text);

    public XPlatFont WithSize(float size) => new(FamilyName, size, Weight, Italic);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _font.Dispose();
        _typeface?.Dispose();
    }
}
