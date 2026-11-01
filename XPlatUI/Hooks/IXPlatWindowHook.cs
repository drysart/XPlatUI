namespace XPlatUI;

/// <summary>
/// Per-window work that has to run at a point in a window's life that no event covers, such as
/// registering a native drop target once the handle exists and revoking it before the handle dies.
/// </summary>
/// <remarks>
/// <para>
/// A window hook is a window-scoped service, so each window gets its own instance and per-window
/// state lives on the hook instead of in a table keyed by window. It is disposed with the window's
/// scope, after the window's own <see cref="XPlatWindowPhase.Disposed"/> phase.
/// </para>
/// <para>
/// Register one with <see cref="XPlatServiceCollectionExtensions.AddXPlatWindowHook"/>, or by name
/// through <see cref="XPlatApplicationOptions.ConfigureServices"/> using
/// <see cref="XPlatServiceCollectionExtensions.AddWindowScoped{TService}"/>. Either way the
/// registration is the only thing that names the concrete type, which is what keeps a feature out
/// of an application that never opts in.
/// </para>
/// <para>
/// Hooks run on the UI thread. A hook that throws reaches
/// <see cref="XPlatApplication.UnhandledException"/> rather than unwinding into a platform
/// callback.
/// </para>
/// </remarks>
public interface IXPlatWindowHook
{
    /// <summary>Called once per phase.</summary>
    /// <param name="phase">The phase being entered.</param>
    /// <param name="window">The window it belongs to.</param>
    void OnPhase(XPlatWindowPhase phase, XPlatWindow window);
}
