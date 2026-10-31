namespace XPlatUI.Backend.Headless;

/// <summary>
/// The headless backend's <see cref="IXPlatDialogService"/>: it records every request and answers
/// from a script instead of showing anything, so a code path that asks the user a question can be
/// tested with no display and no human.
/// </summary>
/// <remarks>
/// Nothing blocks. The handlers are plain delegates, so a test can make an "are you sure" answer yes
/// or no and assert on what the application did next. The default answers are the ones a dismissed
/// dialog would give: <see cref="XPlatDialogResult.Cancel"/> and a null path.
/// </remarks>
public sealed class HeadlessDialogService : IXPlatDialogService
{
    private readonly List<XPlatMessageBoxOptions> _messages = [];
    private readonly List<XPlatFileDialogOptions> _openFiles = [];
    private readonly List<XPlatFileDialogOptions> _saveFiles = [];
    private readonly List<XPlatFileDialogOptions> _folders = [];

    /// <summary>Answers <see cref="ShowMessage"/>. Defaults to <see cref="XPlatDialogResult.Cancel"/>.</summary>
    public Func<XPlatMessageBoxOptions, XPlatWindow?, XPlatDialogResult> MessageHandler { get; set; } =
        static (_, _) => XPlatDialogResult.Cancel;

    /// <summary>Answers <see cref="OpenFile"/>. Defaults to null, meaning the user cancelled.</summary>
    public Func<XPlatFileDialogOptions, XPlatWindow?, string?> OpenFileHandler { get; set; } = static (_, _) => null;

    /// <summary>Answers <see cref="SaveFile"/>. Defaults to null, meaning the user cancelled.</summary>
    public Func<XPlatFileDialogOptions, XPlatWindow?, string?> SaveFileHandler { get; set; } = static (_, _) => null;

    /// <summary>Answers <see cref="PickFolder"/>. Defaults to null, meaning the user cancelled.</summary>
    public Func<XPlatFileDialogOptions, XPlatWindow?, string?> PickFolderHandler { get; set; } = static (_, _) => null;

    /// <summary>Every message box request, in order.</summary>
    public IReadOnlyList<XPlatMessageBoxOptions> Messages => _messages;

    /// <summary>Every open-file request, in order.</summary>
    public IReadOnlyList<XPlatFileDialogOptions> OpenFileRequests => _openFiles;

    /// <summary>Every save-file request, in order.</summary>
    public IReadOnlyList<XPlatFileDialogOptions> SaveFileRequests => _saveFiles;

    /// <summary>Every folder request, in order.</summary>
    public IReadOnlyList<XPlatFileDialogOptions> PickFolderRequests => _folders;

    /// <inheritdoc />
    public XPlatDialogResult ShowMessage(XPlatMessageBoxOptions options, XPlatWindow? owner)
    {
        ArgumentNullException.ThrowIfNull(options);

        _messages.Add(options);
        return MessageHandler(options, owner);
    }

    /// <inheritdoc />
    public string? OpenFile(XPlatFileDialogOptions options, XPlatWindow? owner)
    {
        ArgumentNullException.ThrowIfNull(options);

        _openFiles.Add(options);
        return OpenFileHandler(options, owner);
    }

    /// <inheritdoc />
    public string? SaveFile(XPlatFileDialogOptions options, XPlatWindow? owner)
    {
        ArgumentNullException.ThrowIfNull(options);

        _saveFiles.Add(options);
        return SaveFileHandler(options, owner);
    }

    /// <inheritdoc />
    public string? PickFolder(XPlatFileDialogOptions options, XPlatWindow? owner)
    {
        ArgumentNullException.ThrowIfNull(options);

        _folders.Add(options);
        return PickFolderHandler(options, owner);
    }
}
