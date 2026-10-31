using Microsoft.Extensions.DependencyInjection;

namespace XPlatUI;

/// <summary>
/// Configuration every platform needs but that is not specific to one window. Handed to the
/// <see cref="XPlatApplication"/> constructor.
/// </summary>
public sealed class XPlatApplicationOptions
{
    /// <summary>
    /// The application's name: the macOS menu bar's first entry, the taskbar entry, the about box.
    /// Null leaves it to the backend, which usually takes it from the executable.
    /// </summary>
    public string? ApplicationName { get; set; }

    /// <summary>What ends the message loop. Defaults to <see cref="XPlatShutdownMode.OnLastWindowClose"/>.</summary>
    public XPlatShutdownMode ShutdownMode { get; set; } = XPlatShutdownMode.OnLastWindowClose;

    /// <summary>
    /// True to follow the desktop's light/dark preference for the lifetime of the application.
    /// </summary>
    /// <remarks>
    /// Has no effect unless the backend registers an <see cref="IXPlatSystemTheme"/>: an
    /// application with no platform appearance to follow keeps whatever
    /// <see cref="XPlatTheme.Palette"/> is already in use. That is what keeps a headless render
    /// deterministic regardless of how the host machine is configured.
    /// </remarks>
    public bool FollowSystemAppearance { get; set; } = true;

    /// <summary>
    /// Forces a dark (true) or light (false) palette and ignores the desktop's preference. This is
    /// where a <c>--dark</c> or <c>--light</c> command line switch belongs; null means "do not force".
    /// </summary>
    public bool? ForcedDarkMode { get; set; }

    /// <summary>
    /// The font family the built-in controls draw with. Null uses the operating system's default,
    /// which is what a headless render does.
    /// </summary>
    public string? FontFamily { get; set; }

    /// <summary>How the process responds to display scaling. Defaults to <see cref="XPlatDpiAwareness.PerMonitor"/>.</summary>
    public XPlatDpiAwareness DpiAwareness { get; set; } = XPlatDpiAwareness.PerMonitor;

    /// <summary>
    /// Windows only: attach to the parent console so diagnostics from a GUI executable are visible
    /// when it is started from a terminal.
    /// </summary>
    public bool AttachConsole { get; set; }

    /// <summary>
    /// The menu bar every window uses unless it sets <see cref="XPlatWindow.MenuBar"/> itself. On
    /// macOS this is the only menu bar there is.
    /// </summary>
    public XPlatMenuBar? MenuBar { get; set; }

    /// <summary>
    /// Registers the application's own services. Runs after the backend has registered the platform
    /// services and before the container is built, so registering a service the backend already
    /// provides replaces it.
    /// </summary>
    /// <remarks>
    /// The parameter is MEDI's own <see cref="IServiceCollection"/>, so every <c>Add*</c> extension
    /// from every package works unchanged. Keep to the AOT-safe subset: generic
    /// <c>AddSingleton&lt;TService, TImpl&gt;()</c>, factory overloads, and
    /// <see cref="XPlatServiceCollectionExtensions.AddWindowScoped{TService}"/>. Assembly scanning
    /// is the one common pattern that is not trimming-safe.
    /// </remarks>
    public Action<IServiceCollection>? ConfigureServices { get; set; }
}
