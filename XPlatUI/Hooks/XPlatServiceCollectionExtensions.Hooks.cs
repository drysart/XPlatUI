using Microsoft.Extensions.DependencyInjection;

namespace XPlatUI;

/// <summary>Registration helpers for the hook seam.</summary>
public static partial class XPlatServiceCollectionExtensions
{
    /// <summary>
    /// Registers a hook that runs a single callback at one application phase.
    /// </summary>
    /// <param name="services">The collection being built.</param>
    /// <param name="phase">The phase <paramref name="hookFunc"/> runs at.</param>
    /// <param name="hookFunc">The work to do. Receives the application.</param>
    /// <remarks>
    /// Registering twice registers two hooks, which run in registration order. For teardown that
    /// has to happen for every application, including one that never runs a loop, use
    /// <see cref="XPlatApplicationPhase.Disposed"/> rather than
    /// <see cref="XPlatApplicationPhase.Stopping"/>.
    /// </remarks>
    public static IServiceCollection AddXPlatApplicationHook(
        this IServiceCollection services,
        XPlatApplicationPhase phase,
        Action<XPlatApplication> hookFunc)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(hookFunc);

        return services.AddSingleton<IXPlatApplicationHook>(
            _ => new ActionBasedAppHook(phase, hookFunc));
    }

    /// <summary>Shorthand for a hook at <see cref="XPlatApplicationPhase.Creating"/>.</summary>
    /// <param name="services">The collection being built.</param>
    /// <param name="startupHookFunc">The work to do before the first window exists.</param>
    public static IServiceCollection AddXPlatApplicationStartupHook(
        this IServiceCollection services,
        Action<XPlatApplication> startupHookFunc) =>
        services.AddXPlatApplicationHook(XPlatApplicationPhase.Creating, startupHookFunc);

    /// <summary>
    /// Registers a window hook that runs one callback per phase, skipping the phases left null.
    /// </summary>
    /// <param name="services">The collection being built.</param>
    /// <param name="onCreated">Runs at <see cref="XPlatWindowPhase.Created"/>.</param>
    /// <param name="onShowing">Runs at <see cref="XPlatWindowPhase.Showing"/>.</param>
    /// <param name="onShown">Runs at <see cref="XPlatWindowPhase.Shown"/>.</param>
    /// <param name="onHiding">Runs at <see cref="XPlatWindowPhase.Hiding"/>.</param>
    /// <param name="onHidden">Runs at <see cref="XPlatWindowPhase.Hidden"/>.</param>
    /// <param name="onClosing">Runs at <see cref="XPlatWindowPhase.Closing"/>.</param>
    /// <param name="onClosed">Runs at <see cref="XPlatWindowPhase.Closed"/>.</param>
    /// <param name="onDisposed">Runs at <see cref="XPlatWindowPhase.Disposed"/>.</param>
    /// <remarks>
    /// The hook is window-scoped, so each window gets its own instance and its own copy of anything
    /// a callback closes over per window. A callback that needs state shared across phases should
    /// hold it in the captured closure, not in a table keyed by window.
    /// </remarks>
    public static IServiceCollection AddXPlatWindowHook(
        this IServiceCollection services,
        Action<XPlatWindow>? onCreated = null,
        Action<XPlatWindow>? onShowing = null,
        Action<XPlatWindow>? onShown = null,
        Action<XPlatWindow>? onHiding = null,
        Action<XPlatWindow>? onHidden = null,
        Action<XPlatWindow>? onClosing = null,
        Action<XPlatWindow>? onClosed = null,
        Action<XPlatWindow>? onDisposed = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        return services.AddWindowScoped<IXPlatWindowHook>((_, _) => new ActionBasedWindowHook(
            onCreated,
            onShowing,
            onShown,
            onHiding,
            onHidden,
            onClosing,
            onClosed,
            onDisposed));
    }
}
