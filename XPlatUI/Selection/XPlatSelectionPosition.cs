namespace XPlatUI;

/// <summary>
/// A point inside a control's selectable content: the control plus an offset in its selection
/// units. The offset is fractional on purpose - a point inside a whole control maps to the middle
/// of its single unit (0.5), which is what makes a whole control selectable from either drag
/// direction.
/// </summary>
public readonly struct XPlatSelectionPosition : IEquatable<XPlatSelectionPosition>
{
    public XPlatSelectionPosition(XPlatControl control, float offset)
    {
        ArgumentNullException.ThrowIfNull(control);
        Control = control;
        Offset = MathF.Max(0f, offset);
    }

    /// <summary>The control the position belongs to; null for the default value.</summary>
    public XPlatControl? Control { get; }

    /// <summary>Offset into the control's <see cref="XPlatControl.SelectionUnitCount"/> units.</summary>
    public float Offset { get; }

    public bool IsEmpty => Control is null;

    public bool Equals(XPlatSelectionPosition other) =>
        ReferenceEquals(Control, other.Control) && Offset == other.Offset;

    public override bool Equals(object? obj) => obj is XPlatSelectionPosition other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Control, Offset);

    public static bool operator ==(XPlatSelectionPosition left, XPlatSelectionPosition right) => left.Equals(right);

    public static bool operator !=(XPlatSelectionPosition left, XPlatSelectionPosition right) => !left.Equals(right);

    public override string ToString() => IsEmpty ? "(none)" : $"{Control!.Name}@{Offset:0.##}";
}
