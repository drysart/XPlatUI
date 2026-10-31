namespace XPlatUI;

/// <summary>Which buttons a message box shows.</summary>
public enum XPlatMessageBoxButtons
{
    /// <summary>A single OK button.</summary>
    Ok,

    /// <summary>OK and Cancel.</summary>
    OkCancel,

    /// <summary>Yes and No.</summary>
    YesNo,

    /// <summary>Yes, No and Cancel.</summary>
    YesNoCancel,

    /// <summary>Retry and Cancel.</summary>
    RetryCancel,

    /// <summary>Abort, Retry and Ignore.</summary>
    AbortRetryIgnore,
}

/// <summary>The icon a message box shows.</summary>
public enum XPlatMessageBoxIcon
{
    /// <summary>No icon.</summary>
    None,

    /// <summary>The information icon.</summary>
    Information,

    /// <summary>The warning icon.</summary>
    Warning,

    /// <summary>The error icon.</summary>
    Error,

    /// <summary>The question icon. Windows renders it as text only; macOS and GTK have a real one.</summary>
    Question,
}

/// <summary>
/// What the user chose in a message box. The set is the union of what the three platforms can
/// return, so a backend maps onto it rather than inventing a result of its own.
/// </summary>
public enum XPlatDialogResult
{
    /// <summary>OK.</summary>
    Ok,

    /// <summary>Cancel, or the dialog dismissed with Escape or its close button.</summary>
    Cancel,

    /// <summary>Yes.</summary>
    Yes,

    /// <summary>No.</summary>
    No,

    /// <summary>Retry.</summary>
    Retry,

    /// <summary>Abort.</summary>
    Abort,

    /// <summary>Ignore.</summary>
    Ignore,
}

/// <summary>
/// The text, buttons and icon of a message box, passed to
/// <see cref="IXPlatDialogService.ShowMessage"/>.
/// </summary>
public sealed class XPlatMessageBoxOptions
{
    /// <summary>The text to show.</summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>Title bar text. Null lets the platform use its default, which is usually the application name.</summary>
    public string? Title { get; set; }

    /// <summary>Which buttons to show.</summary>
    public XPlatMessageBoxButtons Buttons { get; set; } = XPlatMessageBoxButtons.Ok;

    /// <summary>The icon to show.</summary>
    public XPlatMessageBoxIcon Icon { get; set; } = XPlatMessageBoxIcon.None;

    /// <summary>
    /// The button that has focus and that Enter chooses. A backend falls back to the first button
    /// <see cref="Buttons"/> shows when this is not one of them.
    /// </summary>
    public XPlatDialogResult DefaultResult { get; set; } = XPlatDialogResult.Ok;
}

/// <summary>
/// The starting point and filtering of a file or folder dialog, passed to
/// <see cref="IXPlatDialogService.OpenFile"/>, <see cref="IXPlatDialogService.SaveFile"/> and
/// <see cref="IXPlatDialogService.PickFolder"/>.
/// </summary>
public sealed class XPlatFileDialogOptions
{
    /// <summary>Title bar text. Null lets the platform use its default.</summary>
    public string? Title { get; set; }

    /// <summary>Folder the dialog opens in. Null lets the platform remember the last one.</summary>
    public string? InitialDirectory { get; set; }

    /// <summary>
    /// The name offered in the filename box, without a path. For a save dialog it is the suggested
    /// file name; for an open dialog it is the selection to start with.
    /// </summary>
    public string? FileName { get; set; }

    /// <summary>
    /// The filter drop-down, in order. Empty shows all files. Ignored by
    /// <see cref="IXPlatDialogService.PickFolder"/>, which has no filters.
    /// </summary>
    public IList<XPlatFileFilter> Filters { get; } = new List<XPlatFileFilter>();

    /// <summary>True to ask before a save dialog overwrites an existing file.</summary>
    public bool OverwritePrompt { get; set; } = true;
}

/// <summary>
/// One entry in a file dialog's filter drop-down, such as "Image files" matching <c>*.png</c> and
/// <c>*.jpg</c>.
/// </summary>
public sealed class XPlatFileFilter
{
    /// <summary>Creates a filter entry.</summary>
    /// <param name="name">The label shown in the drop-down.</param>
    /// <param name="patterns">One or more glob patterns, such as <c>*.png</c>.</param>
    public XPlatFileFilter(string name, params string[] patterns)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(patterns);
        ArgumentOutOfRangeException.ThrowIfZero(patterns.Length);

        Name = name;
        Patterns = Array.AsReadOnly((string[])patterns.Clone());
    }

    /// <summary>The label shown in the filter drop-down.</summary>
    public string Name { get; }

    /// <summary>
    /// Glob patterns such as <c>*.png</c>. Backends translate them to their own syntax: Windows
    /// joins them into a <c>GetOpenFileNameW</c> filter, macOS makes <c>UTType</c>s of them.
    /// </summary>
    public IReadOnlyList<string> Patterns { get; }
}
