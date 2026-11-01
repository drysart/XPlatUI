namespace XPlatUI;

/// <summary>
/// Names one representation of the content being dragged or dropped.
/// </summary>
/// <remarks>
/// A format is a name plus, for the formats the library understands, a shape every backend knows
/// how to move between applications. <see cref="XPlatDataFormats"/> holds the well-known ones;
/// <see cref="XPlatDataFormats.Custom"/> names anything else, which stays inside the process
/// because no backend can describe it to another application.
/// </remarks>
public readonly struct XPlatDataFormat : IEquatable<XPlatDataFormat>
{
    private readonly string? _name;

    /// <summary>Names a format. Two formats are the same when their names match.</summary>
    /// <param name="name">The format name. Compared case-insensitively.</param>
    public XPlatDataFormat(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        _name = name;
    }

    /// <summary>The format's name, or the empty string for an uninitialized value.</summary>
    public string Name => _name ?? string.Empty;

    /// <inheritdoc />
    public bool Equals(XPlatDataFormat other) =>
        string.Equals(Name, other.Name, StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is XPlatDataFormat other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => StringComparer.OrdinalIgnoreCase.GetHashCode(Name);

    /// <inheritdoc />
    public override string ToString() => Name;

    public static bool operator ==(XPlatDataFormat left, XPlatDataFormat right) => left.Equals(right);

    public static bool operator !=(XPlatDataFormat left, XPlatDataFormat right) => !left.Equals(right);
}

/// <summary>The data formats the library itself understands.</summary>
/// <remarks>
/// The three well-known formats are the ones a backend can carry between applications: plain text,
/// an HTML fragment and a list of file paths. Anything else named with
/// <see cref="Custom"/> is only readable inside the process.
/// </remarks>
public static class XPlatDataFormats
{
    /// <summary>Plain text. The value is a <see cref="string"/>.</summary>
    public static XPlatDataFormat Text { get; } = new("Text");

    /// <summary>An HTML fragment. The value is a <see cref="string"/>.</summary>
    public static XPlatDataFormat Html { get; } = new("Html");

    /// <summary>
    /// Dragged or dropped files. The value is a <see cref="string"/> array of full paths.
    /// </summary>
    public static XPlatDataFormat FileDrop { get; } = new("FileDrop");

    /// <summary>Names a format the library does not understand, for in-process drags.</summary>
    /// <param name="name">The format name.</param>
    public static XPlatDataFormat Custom(string name) => new(name);
}
