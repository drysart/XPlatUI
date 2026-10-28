namespace XPlatUI;

/// <summary>
/// Describes how a single row or column of a <see cref="XPlatTableLayoutPanel"/> is sized.
/// Mirrors the semantics of WinForms' <c>RowStyle</c>/<c>ColumnStyle</c>.
/// </summary>
public class XPlatRowOrColumnStyle
{
    private float _value;

    public XPlatRowOrColumnStyle()
        : this(XPlatSizeType.AutoSize, 0f)
    {
    }

    public XPlatRowOrColumnStyle(XPlatSizeType sizeType, float value)
    {
        SizeType = sizeType;
        Value = value;
    }

    public XPlatSizeType SizeType { get; set; }

    /// <summary>
    /// Absolute height/width in pixels when <see cref="SizeType"/> is
    /// <see cref="XPlatSizeType.Absolute"/>, or a percentage (0-100) when it is
    /// <see cref="XPlatSizeType.Percent"/>. Ignored for <see cref="XPlatSizeType.AutoSize"/>.
    /// </summary>
    public float Value
    {
        get => _value;
        set => _value = float.IsNaN(value) ? 0f : MathF.Max(0f, value);
    }

    public static XPlatRowOrColumnStyle Absolute(float pixels) => new(XPlatSizeType.Absolute, pixels);

    public static XPlatRowOrColumnStyle Percent(float percent) => new(XPlatSizeType.Percent, percent);

    public static XPlatRowOrColumnStyle AutoSize() => new(XPlatSizeType.AutoSize, 0f);
}

/// <summary>A row definition for <see cref="XPlatTableLayoutPanel"/>.</summary>
public sealed class XPlatRowStyle : XPlatRowOrColumnStyle
{
    public XPlatRowStyle()
    {
    }

    public XPlatRowStyle(XPlatSizeType sizeType, float value)
        : base(sizeType, value)
    {
    }

    public static new XPlatRowStyle Absolute(float pixels) => new(XPlatSizeType.Absolute, pixels);

    public static new XPlatRowStyle Percent(float percent) => new(XPlatSizeType.Percent, percent);

    public static new XPlatRowStyle AutoSize() => new(XPlatSizeType.AutoSize, 0f);
}

/// <summary>A column definition for <see cref="XPlatTableLayoutPanel"/>.</summary>
public sealed class XPlatColumnStyle : XPlatRowOrColumnStyle
{
    public XPlatColumnStyle()
    {
    }

    public XPlatColumnStyle(XPlatSizeType sizeType, float value)
        : base(sizeType, value)
    {
    }

    public static new XPlatColumnStyle Absolute(float pixels) => new(XPlatSizeType.Absolute, pixels);

    public static new XPlatColumnStyle Percent(float percent) => new(XPlatSizeType.Percent, percent);

    public static new XPlatColumnStyle AutoSize() => new(XPlatSizeType.AutoSize, 0f);
}
