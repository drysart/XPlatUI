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
    /// True to keep one process per user: a second launch hands its command line to the running
    /// instance and exits instead of starting a competing one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The running instance learns about the launch through <see cref="XPlatApplication.SecondInstance"/>
    /// and comes to the foreground. A second launch's <see cref="XPlatApplication.Run"/> returns 0
    /// before <see cref="XPlatApplication.Startup"/> runs, so nothing is ever shown by it.
    /// </para>
    /// <para>
    /// Windows and a direct macOS launch get this from <see cref="XPlatInstanceLock"/>; GTK4 uses
    /// <c>Gio.Application</c>'s own uniqueness, which is also the only way to raise a window from
    /// another process under Wayland. A Finder or Dock launch on macOS is single-instance whether or
    /// not this is set, because the platform routes it to the running application.
    /// </para>
    /// </remarks>
    public bool SingleInstance { get; set; }

    /// <summary>
    /// What identifies the application for <see cref="SingleInstance"/>. Null uses
    /// <see cref="ApplicationName"/>, and failing that the entry assembly's name.
    /// </summary>
    /// <remarks>
    /// Set this when two different applications built on this library would otherwise share a name,
    /// or when the name is not a legal platform identifier: each backend sanitises it, and GTK4
    /// additionally turns it into the reverse-DNS application id D-Bus requires.
    /// </remarks>
    public string? InstanceKey { get; set; }

    /// <summary>
    /// The command line arguments this launch was started with, without the executable path. A
    /// second launch's are delivered in <see cref="XPlatSecondInstanceEventArgs.Arguments"/>.
    /// </summary>
    /// <remarks>
    /// Null reads <see cref="Environment.GetCommandLineArgs"/>. A launcher that adds switches of its
    /// own, such as <c>--render</c> in the test harness, should pass only the arguments the
    /// application understands.
    /// </remarks>
    public IReadOnlyList<string>? CommandLine { get; set; }

    /// <summary>
    /// The arguments a forwarded launch carries: <see cref="CommandLine"/>, or this process's own
    /// command line without the executable path when that is null.
    /// </summary>
    /// <returns>The arguments, never null.</returns>
    public IReadOnlyList<string> ResolveCommandLine() =>
        CommandLine ?? Environment.GetCommandLineArgs()[1..];

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
    /// The icon every window starts with, and the icon the desktop shows for the application: the
    /// taskbar and Alt-Tab on Windows, the Dock on macOS. Null keeps the platform's generic icon.
    /// </summary>
    /// <remarks>
    /// Set it before the application is created: the platform applies it during initialization,
    /// which is the only moment macOS lets an application change its Dock icon without a bundle
    /// resource. A window may still override it with <see cref="XPlatWindow.Icon"/>.
    /// </remarks>
    public XPlatIcon? Icon { get; set; }

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
