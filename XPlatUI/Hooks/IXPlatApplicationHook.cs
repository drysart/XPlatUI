namespace XPlatUI;

/// <summary>
/// Process-wide work that has to run at a point in the application's life that no event covers.
/// </summary>
/// <remarks>
/// <para>
/// A hook is a singleton resolved from the root provider once, immediately after the container is
/// built, so it sees every phase with the same instance in the same order. It is the mechanism a
/// feature uses to do platform setup before the first window exists, and teardown after the last
/// one is gone, without the always-on path naming the feature: an application that never registers
/// a hook publishes without the hook's type, because <c>GetServices&lt;T&gt;()</c> on a service
/// nobody registered returns an empty sequence and only this interface is retained.
/// </para>
/// <para>
/// Register one with <see cref="XPlatServiceCollectionExtensions.AddXPlatApplicationHook"/> for a
/// single callback, or by name through
/// <see cref="XPlatApplicationOptions.ConfigureServices"/> for a class. A hook that also implements
/// <see cref="IDisposable"/> is disposed by the container, after every window is gone and before
/// the backend shuts down.
/// </para>
/// <para>
/// Hooks run on the thread that constructs and runs the application, and a hook that throws reaches
/// <see cref="XPlatApplication.UnhandledException"/> rather than aborting startup silently.
/// </para>
/// </remarks>
public interface IXPlatApplicationHook
{
    /// <summary>Called once per phase.</summary>
    /// <param name="phase">The phase being entered.</param>
    /// <param name="application">The application it belongs to.</param>
    void OnPhase(XPlatApplicationPhase phase, XPlatApplication application);
}
