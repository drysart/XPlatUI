namespace XPlatUI;

/// <summary>
/// The platform's message box and file dialogs. Every backend, headless included, registers one
/// through <see cref="IXPlatApplicationBackend.RegisterServices"/>; an application reaches it
/// through <c>XPlatApplication.Dialogs</c> or
/// <c>Services.GetService&lt;IXPlatDialogService&gt;()</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Implementations must tolerate a nested loop.</b> Every method here is modal: it re-enters the
/// platform loop that <c>XPlatApplication.Run</c> started and returns only when the modal session
/// ends. A backend therefore cannot assume its loop is entered once and left once, and
/// <see cref="IXPlatApplicationBackend.Invoke"/> callbacks must keep dispatching to the same UI
/// thread while a dialog is open. That requirement is part of the backend contract from the first
/// migration step on purpose: it is the first feature that makes "the backend owns the loop"
/// observable, and retrofitting it once three backends exist is how modal bugs get baked in.
/// </para>
/// <para>
/// Call these on the UI thread. <c>owner</c> is null when the application has no window to be
/// modal to; a backend then centres the dialog on the primary screen instead of parenting it.
/// </para>
/// </remarks>
public interface IXPlatDialogService
{
    /// <summary>
    /// Shows a message box and returns the button the user chose. Blocks in a nested loop until
    /// the user dismisses it.
    /// </summary>
    XPlatDialogResult ShowMessage(XPlatMessageBoxOptions options, XPlatWindow? owner);

    /// <summary>
    /// Asks for a file to open and returns its full path, or null when the user cancelled.
    /// Blocks in a nested loop.
    /// </summary>
    string? OpenFile(XPlatFileDialogOptions options, XPlatWindow? owner);

    /// <summary>
    /// Asks for a file to save and returns its full path, or null when the user cancelled.
    /// Blocks in a nested loop.
    /// </summary>
    string? SaveFile(XPlatFileDialogOptions options, XPlatWindow? owner);

    /// <summary>
    /// Asks for a folder and returns its full path, or null when the user cancelled. Blocks in a
    /// nested loop.
    /// </summary>
    string? PickFolder(XPlatFileDialogOptions options, XPlatWindow? owner);
}
