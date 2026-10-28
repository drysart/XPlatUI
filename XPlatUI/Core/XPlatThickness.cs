using SkiaSharp;

namespace XPlatUI;

/// <summary>
/// Describes the thickness of a frame around a rectangle. Used for both
/// <see cref="XPlatControl.Margin"/> and <see cref="XPlatControl.Padding"/>.
/// </summary>
public readonly struct XPlatThickness : IEquatable<XPlatThickness>
{
    public static readonly XPlatThickness Empty = new(0f);

    public XPlatThickness(float uniform)
        : this(uniform, uniform, uniform, uniform)
    {
    }

    public XPlatThickness(float horizontal, float vertical)
        : this(horizontal, vertical, horizontal, vertical)
    {
    }

    public XPlatThickness(float left, float top, float right, float bottom)
    {
        Left = left;
        Top = top;
        Right = right;
        Bottom = bottom;
    }

    public float Left { get; }

    public float Top { get; }

    public float Right { get; }

    public float Bottom { get; }

    public float Horizontal => Left + Right;

    public float Vertical => Top + Bottom;

    public bool IsEmpty => Left == 0f && Top == 0f && Right == 0f && Bottom == 0f;

    public bool Equals(XPlatThickness other) =>
        Left == other.Left && Top == other.Top && Right == other.Right && Bottom == other.Bottom;

    public override bool Equals(object? obj) => obj is XPlatThickness other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Left, Top, Right, Bottom);

    public static bool operator ==(XPlatThickness left, XPlatThickness right) => left.Equals(right);

    public static bool operator !=(XPlatThickness left, XPlatThickness right) => !left.Equals(right);

    public override string ToString() => $"{Left},{Top},{Right},{Bottom}";
}
