namespace XPlatUI;

/// <summary>
/// The payload of a drag: one or more representations of the same content, so that a target can
/// read the one it understands best.
/// </summary>
/// <remarks>
/// <para>
/// Values are typed by format: <see cref="XPlatDataFormats.Text"/> and
/// <see cref="XPlatDataFormats.Html"/> hold a <see cref="string"/>,
/// <see cref="XPlatDataFormats.FileDrop"/> holds a <see cref="string"/> array of full paths, and a
/// custom format holds whatever the application stored there.
/// </para>
/// <para>
/// Only the well-known formats survive a trip to or from another application; the rest are
/// readable only inside the process that created them.
/// </para>
/// </remarks>
public sealed class XPlatDataObject
{
    private readonly Dictionary<XPlatDataFormat, object> _data = [];

    /// <summary>Creates an empty data object.</summary>
    public XPlatDataObject()
    {
    }

    /// <summary>Creates a data object that carries plain text.</summary>
    /// <param name="text">The text.</param>
    public XPlatDataObject(string text) => SetText(text);

    /// <summary>The formats this object can produce, in no particular order.</summary>
    public IReadOnlyCollection<XPlatDataFormat> Formats => _data.Keys;

    /// <summary>True when there is nothing to drop.</summary>
    public bool IsEmpty => _data.Count == 0;

    /// <summary>True when <paramref name="format"/> can be read.</summary>
    /// <param name="format">The format to look for.</param>
    public bool Contains(XPlatDataFormat format) => _data.ContainsKey(format);

    /// <summary>Reads the raw value of a format, or null when the format is absent.</summary>
    /// <param name="format">The format to read.</param>
    public object? GetData(XPlatDataFormat format) => _data.GetValueOrDefault(format);

    /// <summary>Adds or replaces one representation. A null value removes it.</summary>
    /// <param name="format">The format to write.</param>
    /// <param name="value">The value, or null to remove the format.</param>
    public void SetData(XPlatDataFormat format, object? value)
    {
        if (value is null)
        {
            _data.Remove(format);
        }
        else
        {
            _data[format] = value;
        }
    }

    /// <summary>The text, or null when the object carries none.</summary>
    public string? GetText() => GetData(XPlatDataFormats.Text) as string;

    /// <summary>Sets the text. A null value removes it.</summary>
    /// <param name="text">The text.</param>
    public void SetText(string? text) => SetData(XPlatDataFormats.Text, text);

    /// <summary>The HTML fragment, or null when the object carries none.</summary>
    public string? GetHtml() => GetData(XPlatDataFormats.Html) as string;

    /// <summary>Sets the HTML fragment. A null value removes it.</summary>
    /// <param name="html">The fragment.</param>
    public void SetHtml(string? html) => SetData(XPlatDataFormats.Html, html);

    /// <summary>The files being dragged, or an empty array when there are none.</summary>
    public string[] GetFiles() => GetData(XPlatDataFormats.FileDrop) as string[] ?? [];

    /// <summary>Sets the files being dragged. An empty sequence removes the format.</summary>
    /// <param name="paths">Full paths of the files.</param>
    public void SetFiles(IEnumerable<string>? paths)
    {
        string[] files = paths is null
            ? []
            : [.. paths.Where(path => !string.IsNullOrEmpty(path))];
        SetData(XPlatDataFormats.FileDrop, files.Length == 0 ? null : files);
    }

    /// <summary>Creates a data object that carries only text.</summary>
    /// <param name="text">The text.</param>
    public static XPlatDataObject FromText(string text) => new(text);

    /// <summary>Creates a data object that carries only files.</summary>
    /// <param name="paths">Full paths of the files.</param>
    public static XPlatDataObject FromFiles(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        XPlatDataObject data = new();
        data.SetFiles(paths);
        return data;
    }
}
