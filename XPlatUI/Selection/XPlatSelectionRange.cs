namespace XPlatUI;

/// <summary>A contiguous slice of a control's selectable content.</summary>
public readonly struct XPlatSelectionRange : IEquatable<XPlatSelectionRange>
{
    public XPlatSelectionRange(XPlatControl control, int start, int length)
    {
        ArgumentNullException.ThrowIfNull(control);
        Control = control;
        Start = Math.Max(0, start);
        Length = Math.Max(0, length);
    }

    /// <summary>The control the range belongs to; null for the default value.</summary>
    public XPlatControl? Control { get; }

    /// <summary>First selected unit, relative to the start of the control's content.</summary>
    public int Start { get; }

    /// <summary>Number of selected units.</summary>
    public int Length { get; }

    /// <summary>One past the last selected unit.</summary>
    public int End => Start + Length;

    public bool IsEmpty => Control is null || Length <= 0;

    public bool Equals(XPlatSelectionRange other) =>
        ReferenceEquals(Control, other.Control) && Start == other.Start && Length == other.Length;

    public override bool Equals(object? obj) => obj is XPlatSelectionRange other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Control, Start, Length);

    public static bool operator ==(XPlatSelectionRange left, XPlatSelectionRange right) => left.Equals(right);

    public static bool operator !=(XPlatSelectionRange left, XPlatSelectionRange right) => !left.Equals(right);

    public override string ToString() => IsEmpty ? "(none)" : $"{Control!.Name}[{Start}..{End})";
}
