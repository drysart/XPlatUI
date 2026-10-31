using System.Runtime.ExceptionServices;
using Microsoft.Extensions.DependencyInjection;
using SkiaSharp;

namespace XPlatUI.Backend.Gtk4;

/// <summary>
/// The GTK4 application backend: it owns the <see cref="Gtk.Application"/>, the main loop, the
/// cross-thread work queue and the process-wide services (clipboard, system theme, dialogs).
/// </summary>
/// <remarks>
/// <para>
/// GTK has no run-loop modes and no "stop" flag, so the two jobs AppKit splits between an
/// <c>NSApplication</c> stop and a posted event are done here by the GLib main context: queued work
/// is drained from an idle source, and a repeating timeout keeps the loop honest while a nested
/// modal loop or a dialog owns it.
/// </para>
/// <para>
/// The application holds a GLib application reference for as long as it runs
/// (<see cref="Gio.Application.Hold"/>), because GTK would otherwise end the loop the moment the
/// last window was destroyed, which would defeat <see cref="XPlatShutdownMode.OnExplicitExit"/>.
/// </para>
/// </remarks>
public sealed class Gtk4ApplicationBackend : IXPlatApplicationBackend
{
    private const string DefaultApplicationId = "com.xplatui.application";

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    private readonly Queue<Action> _pending = new();
    private readonly object _pendingGate = new();
    private readonly List<Gtk4WindowBackend> _windows = [];
    private readonly object _windowsGate = new();
    private readonly List<GLib.MainLoop> _modalLoops = [];
    private readonly GLib.SourceFunc _drain;
    private readonly GLib.SourceFunc _poll;

    private Gtk.Application? _native;
    private uint _pollSource;
    private Gtk4WindowBackend? _activeWindow;
    private int _uiThreadId;
    private int _exitCode;
    private bool _held;
    private bool _initialized;
    private bool _loopRunning;
    private bool _shutDown;

    /// <summary>Creates the backend. Nothing touches GTK until <see cref="Initialize"/>.</summary>
    public Gtk4ApplicationBackend()
    {
        _uiThreadId = Environment.CurrentManagedThreadId;
        _drain = DrainIdle;
        _poll = OnPoll;
    }

    /// <summary>The application this backend belongs to, or null before <see cref="Initialize"/>.</summary>
    public XPlatApplication? Application { get; private set; }

    /// <summary>
    /// The desktop's light/dark preference, as the application will see it.
    /// </summary>
    /// <remarks>
    /// Exposed because the palette sometimes has to be right before an <see cref="XPlatApplication"/>
    /// exists: the harness's headless <c>--render</c> mode paints through the headless backend,
    /// which installs no system theme, so the host applies this one itself.
    /// </remarks>
    public IXPlatSystemTheme SystemTheme { get; } = new Gtk4SystemTheme();

    /// <summary>The GTK application, or null before <see cref="Initialize"/>.</summary>
    public Gtk.Application? NativeApplication => _native;

    /// <inheritdoc />
    public void Initialize(XPlatApplication application)
    {
        ArgumentNullException.ThrowIfNull(application);

        Application = application;
        _uiThreadId = Environment.CurrentManagedThreadId;

        // Opens the display and registers the native library resolver. Without a display GDK ends
        // the process here, which is why a host that only wants to render to a PNG must never
        // create an application.
        Gtk.Module.Initialize();

        var native = Gtk.Application.New(DefaultApplicationId, Gio.ApplicationFlags.NonUnique);
        native.OnActivate += (_, _) => OnActivate();
        _native = native;

        // The default icon name is the application-wide half of XPlatApplicationOptions.Icon; each
        // window still sets its own, which is what an X11 window manager reads.
        if (application.Options.Icon?.Name is { Length: > 0 } iconName)
        {
            Gtk.Window.SetDefaultIconName(iconName);
        }

        if (SystemTheme is Gtk4SystemTheme theme)
        {
            theme.Changed += OnSystemThemeChanged;
            theme.Attach();
        }

        _initialized = true;
    }

    /// <inheritdoc />
    public int Run()
    {
        _uiThreadId = Environment.CurrentManagedThreadId;

        if (!_initialized || _native is null)
        {
            return -1;
        }

        StartPollTimer();

        // Keeps the application alive even with no windows, so the shutdown mode decides when it
        // ends rather than GTK's last-window rule.
        Hold();

        _native.Run([Environment.ProcessPath ?? "XPlatUI"]);
        return _exitCode;
    }

    /// <inheritdoc />
    public void Quit(int exitCode)
    {
        _exitCode = exitCode;

        if (!_initialized)
        {
            return;
        }

        // A nested modal loop owns the loop while it runs, so it is ended first; the application's
        // own quit would not be seen until that loop returned.
        foreach (var loop in _modalLoops.ToArray())
        {
            if (loop.IsRunning())
            {
                loop.Quit();
            }
        }

        _modalLoops.Clear();

        _native?.Quit();
        Wake();
    }

    /// <inheritdoc />
    public void Invoke(Action action, bool synchronous)
    {
        ArgumentNullException.ThrowIfNull(action);

        if (synchronous && Environment.CurrentManagedThreadId == _uiThreadId)
        {
            action();
            return;
        }

        if (!synchronous)
        {
            lock (_pendingGate)
            {
                _pending.Enqueue(action);
            }

            Wake();
            return;
        }

        using var completed = new ManualResetEventSlim();
        ExceptionDispatchInfo? failure = null;

        lock (_pendingGate)
        {
            _pending.Enqueue(() =>
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    failure = ExceptionDispatchInfo.Capture(ex);
                }
                finally
                {
                    completed.Set();
                }
            });
        }

        Wake();
        completed.Wait();
        failure?.Throw();
    }

    /// <inheritdoc />
    public IXPlatWindowBackend CreateWindow(XPlatWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);

        var backend = new Gtk4WindowBackend(this, window);

        lock (_windowsGate)
        {
            _windows.Add(backend);
        }

        return backend;
    }

    /// <inheritdoc />
    public void RegisterServices(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton(this);
        services.AddSingleton<Gtk4Clipboard>();
        services.AddSingleton<IXPlatClipboard>(static provider => provider.GetRequiredService<Gtk4Clipboard>());
        services.AddSingleton(SystemTheme);
        services.AddSingleton<IXPlatDialogService, Gtk4DialogService>();
    }

    /// <inheritdoc />
    public IReadOnlyList<XPlatScreen> GetScreens()
    {
        var screens = new List<XPlatScreen>();

        try
        {
            var monitors = Gdk.Display.GetDefault()?.GetMonitors();
            if (monitors is not null)
            {
                var count = monitors.GetNItems();

                for (uint i = 0; i < count; i++)
                {
                    if (monitors.GetObject(i) is Gdk.Monitor monitor && monitor.Valid)
                    {
                        screens.Add(ToScreen(monitor, isPrimary: i == 0));
                    }
                }
            }
        }
        catch (Exception)
        {
            // No display - a service, a CI agent, an ssh session. Reported below as one synthetic
            // display so a window still has somewhere to centre itself.
        }

        if (screens.Count == 0)
        {
            screens.Add(new XPlatScreen(
                new SKRect(0f, 0f, 1920f, 1080f),
                new SKRect(0f, 0f, 1920f, 1055f),
                1f,
                isPrimary: true));
        }

        return screens;
    }

    /// <inheritdoc />
    public void Shutdown()
    {
        if (_shutDown)
        {
            return;
        }

        _shutDown = true;

        StopPollTimer();

        lock (_pendingGate)
        {
            _pending.Clear();
        }

        lock (_windowsGate)
        {
            _windows.Clear();
        }

        _modalLoops.Clear();
        _activeWindow = null;
        _loopRunning = false;

        if (SystemTheme is Gtk4SystemTheme theme)
        {
            theme.Changed -= OnSystemThemeChanged;
        }

        Release();

        _native?.Dispose();
        _native = null;

        XPlatContextMenuPresenter.Current = null;

        if (ReferenceEquals(XPlatSystemTheme.Current, SystemTheme))
        {
            XPlatSystemTheme.Current = null;
        }

        _initialized = false;
    }

    /// <summary>True once GTK has entered its main loop, which is when windows can be presented.</summary>
    internal bool IsLoopRunning => _loopRunning;

    /// <summary>The primary display, as <see cref="IXPlatWindowBackend.Screen"/> needs before a window exists.</summary>
    internal XPlatScreen PrimaryScreen
    {
        get
        {
            var screens = GetScreens();

            foreach (var screen in screens)
            {
                if (screen.IsPrimary)
                {
                    return screen;
                }
            }

            return screens[0];
        }
    }

    /// <summary>The window that currently has focus, or null when none does.</summary>
    internal Gtk4WindowBackend? ActiveWindow => _activeWindow;

    /// <summary>Records that <paramref name="window"/> became the active window.</summary>
    internal void NotifyWindowActivated(Gtk4WindowBackend window)
    {
        if (ReferenceEquals(_activeWindow, window))
        {
            return;
        }

        _activeWindow = window;

        // A context menu is anchored to the view that shows it, so the presenter follows the active
        // window rather than being installed once per window.
        XPlatContextMenuPresenter.Current = window.Presenter;
    }

    /// <summary>Records that <paramref name="window"/> stopped being the active window.</summary>
    internal void NotifyWindowDeactivated(Gtk4WindowBackend window)
    {
        if (ReferenceEquals(_activeWindow, window))
        {
            _activeWindow = null;
        }
    }

    /// <summary>Drops a disposed window from the bookkeeping the screens and presenters rely on.</summary>
    internal void RemoveWindow(Gtk4WindowBackend window)
    {
        lock (_windowsGate)
        {
            _windows.Remove(window);
        }

        if (ReferenceEquals(_activeWindow, window))
        {
            _activeWindow = null;
        }

        if (ReferenceEquals(XPlatContextMenuPresenter.Current, window.Presenter))
        {
            XPlatContextMenuPresenter.Current = null;
        }
    }

    /// <summary>
    /// Registers a nested main loop that owns the event loop while a modal window is up.
    /// </summary>
    internal void EnterModal(GLib.MainLoop loop) => _modalLoops.Add(loop);

    internal void ExitModal(GLib.MainLoop loop) => _modalLoops.Remove(loop);

    /// <summary>Converts one GDK monitor into the library's logical, y-down space.</summary>
    internal static XPlatScreen ToScreen(Gdk.Monitor monitor, bool isPrimary)
    {
        var geometry = monitor.Geometry;
        var bounds = new SKRect(
            geometry.X,
            geometry.Y,
            geometry.X + geometry.Width,
            geometry.Y + geometry.Height);

        // GTK reports a monitor's geometry but not its work area: the panel and dock are drawn by
        // the shell over the display, so the usable area is reported as the whole monitor.
        return new XPlatScreen(bounds, bounds, Math.Max(monitor.ScaleFactor, 1), isPrimary);
    }

    /// <summary>The display a window is on, in logical units.</summary>
    internal static XPlatScreen? ScreenForWindow(Gtk.Window window)
    {
        try
        {
            var display = window.GetDisplay();
            var surface = window.GetSurface();
            if (display is null || surface is null)
            {
                return null;
            }

            var monitor = display.GetMonitorAtSurface(surface);
            if (monitor is null || !monitor.Valid)
            {
                return null;
            }

            return ToScreen(monitor, isPrimary: IsPrimaryMonitor(display, monitor));
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool IsPrimaryMonitor(Gdk.Display display, Gdk.Monitor monitor)
    {
        var monitors = display.GetMonitors();
        if (monitors is null || monitors.GetNItems() == 0)
        {
            return false;
        }

        if (monitors.GetObject(0) is not Gdk.Monitor first || !first.Valid)
        {
            return false;
        }

        var geometry = monitor.Geometry;
        var candidate = first.Geometry;

        return geometry.X == candidate.X
            && geometry.Y == candidate.Y
            && geometry.Width == candidate.Width
            && geometry.Height == candidate.Height;
    }

    private void OnActivate()
    {
        // The activate signal is GTK's "the application is up", which is the earliest moment a
        // window can be presented.
        _loopRunning = true;

        Gtk4WindowBackend[] windows;
        lock (_windowsGate)
        {
            windows = [.. _windows];
        }

        foreach (var window in windows)
        {
            window.PresentPending();
        }
    }

    private void Hold()
    {
        if (_held)
        {
            return;
        }

        _native?.Hold();
        _held = true;
    }

    private void Release()
    {
        if (!_held)
        {
            return;
        }

        _native?.Release();
        _held = false;
    }

    /// <summary>
    /// Posts an idle source so the main loop comes back around and drains the queue. GLib's idle
    /// sources are safe to add from any thread.
    /// </summary>
    private void Wake()
    {
        if (!_initialized)
        {
            // Nothing is draining yet: Run has not been entered, so the queue waits for it.
            return;
        }

        GLib.Functions.IdleAdd(0, _drain);
    }

    private bool DrainIdle()
    {
        Drain();
        return false;
    }

    private void Drain()
    {
        while (true)
        {
            Action next;

            lock (_pendingGate)
            {
                if (_pending.Count == 0)
                {
                    return;
                }

                next = _pending.Dequeue();
            }

            next();
        }
    }

    private void StartPollTimer()
    {
        if (_pollSource != 0)
        {
            return;
        }

        // A default-priority timeout keeps firing inside nested modal loops and while a dialog is
        // pumping the context, so queued work keeps running there.
        _pollSource = GLib.Functions.TimeoutAdd(0, (uint)PollInterval.TotalMilliseconds, _poll);
    }

    private void StopPollTimer()
    {
        if (_pollSource == 0)
        {
            return;
        }

        GLib.Functions.SourceRemove(_pollSource);
        _pollSource = 0;
    }

    private bool OnPoll()
    {
        Drain();

        Gtk4WindowBackend[] windows;
        lock (_windowsGate)
        {
            windows = [.. _windows];
        }

        foreach (var window in windows)
        {
            window.ReportStatistics();
        }

        return true;
    }

    private void OnSystemThemeChanged(object? sender, EventArgs e)
    {
        // A forced palette is an explicit choice, so the system must not override it.
        if (Application?.Options.ForcedDarkMode is not null)
        {
            return;
        }

        if (ReferenceEquals(XPlatSystemTheme.Current, SystemTheme))
        {
            XPlatSystemTheme.Refresh();
        }
    }
}
