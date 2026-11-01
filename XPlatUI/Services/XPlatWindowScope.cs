using Microsoft.Extensions.DependencyInjection;

namespace XPlatUI;

/// <summary>
/// Carries the window a service scope belongs to. MEDI can create a scope but not a scope with a
/// payload, so the model puts a mutable accessor inside the scope and fills it in immediately after
/// the scope exists. Nothing outside the library touches this type.
/// </summary>
internal sealed class XPlatWindowAccessor
{
    /// <summary>The window this scope belongs to, or null before the scope has been bound.</summary>
    public XPlatWindow? Window { get; set; }
}

/// <summary>Registration helpers for services whose lifetime is one window.</summary>
public static partial class XPlatServiceCollectionExtensions
{
    /// <summary>
    /// Registers a service that is created once per window, receives that window, and is disposed
    /// when the window closes.
    /// </summary>
    /// <remarks>
    /// This is the one XPlatUI-specific registration; everything else is MEDI's own API. It is
    /// <c>AddScoped</c> underneath, so a window-scoped service can depend on application-scoped
    /// singletons normally, and anything the application registers with <c>AddScoped</c> also
    /// becomes per-window.
    /// </remarks>
    /// <typeparam name="TService">The service to register.</typeparam>
    /// <param name="services">The collection being built.</param>
    /// <param name="factory">Builds the service from the window it belongs to and the scope's provider.</param>
    public static IServiceCollection AddWindowScoped<TService>(
        this IServiceCollection services,
        Func<XPlatWindow, IServiceProvider, TService> factory)
        where TService : class
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(factory);

        return services.AddScoped(provider => factory(
            provider.GetRequiredService<XPlatWindowAccessor>().Window
                ?? throw new InvalidOperationException(
                    "A window-scoped service was resolved outside a window scope."),
            provider));
    }
}
