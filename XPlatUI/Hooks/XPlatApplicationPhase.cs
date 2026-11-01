namespace XPlatUI;

/// <summary>
/// The moments an <see cref="IXPlatApplicationHook"/> is called at. Each one is a different answer
/// to "what exists right now", which is why there is more than one.
/// </summary>
/// <remarks>
/// <para>
/// The phases are split where they carry information rather than where it was convenient.
/// <see cref="Creating"/> is the last moment before any window exists; <see cref="Created"/> is the
/// first moment the windows shown before <see cref="XPlatApplication.Run"/> are visible.
/// <see cref="Stopping"/> is the first moment after the loop returns, with the whole application
/// still intact; <see cref="Stopped"/> is the last moment a singleton service can be resolved;
/// <see cref="Disposed"/> is after the container and the backend are gone, and exists so a hook can
/// observe that rather than infer it.
/// </para>
/// <para>
/// <b><see cref="Creating"/> pairs with <see cref="Disposed"/>, not with <see cref="Stopping"/>.</b>
/// A launch that was forwarded to another instance returns from <see cref="XPlatApplication.Run"/>
/// without entering a loop, and a headless render never runs one, so neither sees anything between
/// them. Work that has to happen for every application belongs in <see cref="Disposed"/>.
/// </para>
/// </remarks>
public enum XPlatApplicationPhase
{
    /// <summary>
    /// The container is built, the process services are installed and no window exists yet. The
    /// last point at which process-wide setup can precede the first window.
    /// </summary>
    Creating,

    /// <summary>
    /// Every window shown before <see cref="XPlatApplication.Run"/> is visible and the loop is
    /// about to start.
    /// </summary>
    Created,

    /// <summary>
    /// The loop has returned. Every window, the backend and every service are still alive.
    /// </summary>
    Stopping,

    /// <summary>Every window is gone and the container is about to be disposed.</summary>
    Stopped,

    /// <summary>
    /// The container and the backend are gone. Raised for every application, including one that
    /// never ran a loop, so it is the only teardown a hook is guaranteed to see.
    /// </summary>
    Disposed,
}
