namespace XPlatUI;

/// <summary>
/// The moments an <see cref="IXPlatWindowHook"/> is called at, in the order a window normally
/// passes through them.
/// </summary>
/// <remarks>
/// <para>
/// The phases that bring a window up ascend, and the phases that take it down descend, so a pair of
/// hooks nests: if <c>A</c> registers before <c>B</c>, <c>A</c> sets up before <c>B</c> and tears
/// down after it.
/// </para>
/// <para>
/// <see cref="Hiding"/> and <see cref="Hidden"/> are declared and raised by nothing, because
/// <see cref="XPlatWindow"/> has no <c>Visible</c> property and no <c>Hide</c> method yet. Declaring
/// them now costs two enum members and stops the hide and show feature from renumbering a public
/// enum later.
/// </para>
/// </remarks>
public enum XPlatWindowPhase
{
    /// <summary>
    /// The native window exists and is not visible yet. The point at which per-window native setup
    /// belongs.
    /// </summary>
    Created,

    /// <summary><c>Show</c> has been called; the native window is not ordered front yet.</summary>
    Showing,

    /// <summary>The window is visible.</summary>
    Shown,

    /// <summary>The window is about to become invisible. Reserved until a window can be hidden.</summary>
    Hiding,

    /// <summary>The window is invisible and still alive. Reserved until a window can be hidden.</summary>
    Hidden,

    /// <summary>
    /// The window is about to close and the native handle is still alive. The point at which
    /// native per-window teardown belongs.
    /// </summary>
    Closing,

    /// <summary>The native window is gone and the backend is disposed.</summary>
    Closed,

    /// <summary>
    /// The window object is being disposed and its service scope is still alive, so a hook can
    /// still reach other scoped services. Raised after <see cref="Closed"/>, which means the
    /// backend has already been disposed.
    /// </summary>
    Disposed,
}
