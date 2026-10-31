using SkiaSharp;

namespace XPlatUI;

/// <summary>
/// The platform half of an <see cref="XPlatWindow"/>: it owns the native window and the
/// <see cref="XPlatUiHost"/> that paints into it, and forwards the platform's notifications so the
/// model can raise its own events.
/// </summary>
/// <remarks>
/// <para>
/// A backend creates one of these per window and keeps it alive until the native window is gone.
/// <c>XPlatWindow.Backend</c> exposes it as the escape hatch for platform-specific work: cast it to
/// the backend's own type to reach an <c>NSWindow</c> or an <c>HWND</c>.
/// </para>
/// <para>
/// <b>Input does not pass through the model.</b> The backend forwards pointer, key and text events
/// straight into <c>Host.ProcessMouseMove</c>, <c>ProcessMouseDown</c>, <c>ProcessMouseUp</c>,
/// <c>ProcessMouseLeave</c>, <c>ProcessKeyDown</c>, <c>ProcessKeyUp</c>, <c>ProcessTextInput</c>,
/// <c>ProcessContextMenu</c> and <c>SetClientSize</c>, exactly as the harnesses do today. The model
/// adds lifecycle, services and the loop around that; it never interposes on input.
/// </para>
/// </remarks>
public interface IXPlatWindowBackend : IDisposable
{
    /// <summary>The host that paints this window's control tree. The backend creates and owns it.</summary>
    XPlatUiHost Host { get; }

    /// <summary>Logical-to-physical pixel ratio of the display the window is on.</summary>
    float ScaleFactor { get; }

    /// <summary>The display the window is on, or null before it has been shown.</summary>
    XPlatScreen? Screen { get; }

    /// <summary>Title bar text.</summary>
    string Title { get; set; }

    /// <summary>
    /// The window's icon, or null to keep the platform's generic application icon.
    /// </summary>
    /// <remarks>
    /// What an icon <em>is</em> differs per platform, so each backend applies it its own way: Windows
    /// sets the small icon for the title bar and the big one for the taskbar and Alt-Tab, both sized
    /// from the window's DPI; macOS has no per-window icon and sets the application icon instead;
    /// GTK4 uses <see cref="XPlatIcon.Name"/> because the theme, not the application, owns the pixels.
    /// </remarks>
    XPlatIcon? Icon { get; set; }

    /// <summary>Client area size, in logical pixels.</summary>
    SKSize ClientSize { get; set; }

    /// <summary>Smallest client area the user may drag the window to, in logical pixels.</summary>
    SKSize MinimumClientSize { get; set; }

    /// <summary>Largest client area the user may drag the window to, in logical pixels.</summary>
    SKSize MaximumClientSize { get; set; }

    /// <summary>Top-left corner in screen coordinates, in logical pixels.</summary>
    SKPoint Location { get; set; }

    /// <summary>False for a fixed-size window.</summary>
    bool Resizable { get; set; }

    /// <summary>True to keep the window above all others.</summary>
    bool TopMost { get; set; }

    /// <summary>The window's display state.</summary>
    XPlatWindowState State { get; set; }

    /// <summary>
    /// The window's menu bar, or null for none. Per window on Windows and Linux; the AppKit backend
    /// mirrors the active window's bar into the single global macOS menu bar, which is the only
    /// place the two presentations diverge.
    /// </summary>
    XPlatMenuBar? MenuBar { get; set; }

    /// <summary>
    /// Creates and shows the native window, owned by <paramref name="owner"/> when it is not null,
    /// and returns without waiting for it to close. The model raises its own events from the ones
    /// below.
    /// </summary>
    void Show(IXPlatWindowBackend? owner);

    /// <summary>
    /// Shows the window modally: disables <paramref name="owner"/> and runs a nested platform loop
    /// that returns once this window has closed. The backend must tolerate that nesting - see
    /// <see cref="IXPlatDialogService"/> for what it requires.
    /// </summary>
    void ShowDialog(IXPlatWindowBackend? owner);

    /// <summary>Brings the window to the front and gives it focus.</summary>
    void Activate();

    /// <summary>
    /// Requests a close. The backend reports it through <see cref="CloseRequested"/> rather than
    /// destroying the window, so the model can raise its cancellable closing event first.
    /// </summary>
    void Close();

    /// <summary>
    /// Applies dark or light system chrome: the caption on Windows, the title bar on macOS. A
    /// backend without system chrome ignores it.
    /// </summary>
    void SetTitleBarAppearance(bool dark);

    /// <summary>Raised once the native window is visible.</summary>
    event EventHandler? Shown;

    /// <summary>Raised when the user asks to close the window, including from the title bar's close button.</summary>
    event EventHandler? CloseRequested;

    /// <summary>Raised after the native window has been destroyed.</summary>
    event EventHandler? Closed;

    /// <summary>Raised when the window becomes the active window.</summary>
    event EventHandler? Activated;

    /// <summary>Raised when the window stops being the active window.</summary>
    event EventHandler? Deactivated;

    /// <summary>Raised when the client area changes size, including as a result of a DPI change.</summary>
    event EventHandler? ClientSizeChanged;

    /// <summary>Raised when the window moves to a display with a different scale factor.</summary>
    event EventHandler? ScaleChanged;

    /// <summary>Raised when <see cref="State"/> changes.</summary>
    event EventHandler? StateChanged;
}
