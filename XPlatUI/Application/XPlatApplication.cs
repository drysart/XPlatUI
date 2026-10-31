using System.Runtime.ExceptionServices;
using Microsoft.Extensions.DependencyInjection;

namespace XPlatUI;

/// <summary>
/// Owns the message loop, the window list and the platform services. One per process;
/// <see cref="Current"/> is the ambient handle controls and services use to reach it.
/// </summary>
/// <remarks>
/// <para>
/// The application is the composition root: it builds the service container from the backend's
/// platform services plus the application's own registrations, then runs the backend's loop. Every
/// public member is UI-thread only; <see cref="Invoke"/> and <see cref="BeginInvoke"/> are the only
/// cross-thread entry points.
/// </para>
/// <para>
/// Disposing the application is the teardown path for one that never ran, such as a headless
/// render. <see cref="Run"/> tears down on its own and calls it too, so disposing afterwards is
/// harmless.
/// </para>
/// </remarks>
public sealed partial class XPlatApplication : IDisposable
{
    /// <summary>The exit code a fault produces when nothing else has set one.</summary>
    private const int FaultExitCode = 1;

    private static readonly bool ValidateScopes =
#if DEBUG
        true;
#else
        false;
#endif

    private readonly ServiceProvider _provider;
    private readonly List<XPlatWindow> _windows = [];
    private ExceptionDispatchInfo? _fault;
    private ExceptionDispatchInfo? _handlerFault;
    private IReadOnlyList<XPlatScreen>? _screens;
    private IXPlatDialogService? _dialogs;
    private XPlatTimer? _tickTimer;
    private long _lastTick;
    private int _uiThreadId;
    private int _exitCode;
    private bool _exitRequested;
    private bool _exiting;
    private bool _running;
    private bool _runCalled;
    private bool _tornDown;

    /// <summary>Creates the application with default <see cref="XPlatApplicationOptions"/>.</summary>
    /// <param name="backend">The platform half. The application owns it and shuts it down.</param>
    public XPlatApplication(IXPlatApplicationBackend backend)
        : this(backend, new XPlatApplicationOptions())
    {
    }

    /// <summary>Creates the application, its service container and the backend, in that order.</summary>
    /// <param name="backend">The platform half. The application owns it and shuts it down.</param>
    /// <param name="options">Application-wide configuration.</param>
    /// <exception cref="InvalidOperationException">An application already exists in this process.</exception>
    public XPlatApplication(IXPlatApplicationBackend backend, XPlatApplicationOptions options)
    {
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(options);

        if (Current is not null)
        {
            throw new InvalidOperationException(
                "An XPlatApplication already exists in this process. One per process is the supported model; " +
                "use XPlatApplication.Current to reach it.");
        }

        Backend = backend;
        Options = options;
        _uiThreadId = Environment.CurrentManagedThreadId;
        Current = this;

        backend.Initialize(this);

        var services = new ServiceCollection();
        services.AddSingleton(this);
        services.AddSingleton(options);
        services.AddScoped<XPlatWindowAccessor>();

        backend.RegisterServices(services);
        options.ConfigureServices?.Invoke(services);

        _provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = ValidateScopes });
        Services = _provider;

        InstallProcessServices();
        XPlatTheme.Changed += OnThemeChanged;
    }

    /// <summary>The application in this process, or null before one has been created.</summary>
    public static XPlatApplication? Current { get; private set; }

    /// <summary>Runs <paramref name="application"/>, so <c>Main</c> can be <c>return XPlatApplication.Run(app);</c>.</summary>
    /// <param name="application">The application to run.</param>
    /// <returns>The process exit code.</returns>
    public static int Run(XPlatApplication application)
    {
        ArgumentNullException.ThrowIfNull(application);
        return application.Run();
    }

    /// <summary>The platform half this application drives.</summary>
    public IXPlatApplicationBackend Backend { get; }

    /// <summary>The options this application was created with.</summary>
    public XPlatApplicationOptions Options { get; }

    /// <summary>The application-scoped service provider. One per process.</summary>
    public IServiceProvider Services { get; }

    /// <summary>True from the moment <see cref="Run"/> enters until it has returned.</summary>
    public bool IsRunning => _running;

    /// <summary>The exit code <see cref="Run"/> returns, or will return once it is called.</summary>
    public int ExitCode => _exitCode;

    /// <summary>
    /// The application's main window, or null when it has none. This is identity, never a
    /// requirement: it is the default owner for modal windows, the window <see cref="Activate"/>
    /// brings forward, and the window <see cref="XPlatShutdownMode.OnMainWindowClose"/> watches.
    /// The first window shown becomes the main window; closing it does not clear it, so it can be
    /// shown again.
    /// </summary>
    public XPlatWindow? MainWindow { get; set; }

    /// <summary>The open windows. A snapshot, so it is safe to enumerate while closing windows.</summary>
    public IReadOnlyList<XPlatWindow> Windows => _windows.ToArray();

    /// <summary>The displays attached to the machine, as the backend reports them.</summary>
    public IReadOnlyList<XPlatScreen> Screens => _screens ??= Backend.GetScreens();

    /// <summary>
    /// The platform's message, file and folder dialogs. Never null: every backend registers one,
    /// because dialogs are part of v1.
    /// </summary>
    /// <exception cref="InvalidOperationException">The backend registered no <see cref="IXPlatDialogService"/>.</exception>
    public IXPlatDialogService Dialogs => _dialogs ??= Services.GetService<IXPlatDialogService>()
        ?? throw new InvalidOperationException(
            "The backend registered no IXPlatDialogService. Every backend must register one; " +
            "see IXPlatApplicationBackend.RegisterServices.");

    /// <summary>Raised inside <see cref="Run"/>, before any window is shown.</summary>
    public event EventHandler? Startup;

    /// <summary>
    /// Raised once as the loop ends, before windows are closed.
    /// <see cref="XPlatExitEventArgs.ExitCode"/> can be replaced here.
    /// </summary>
    /// <remarks>
    /// Not cancellable on purpose. By the time the loop has ended there is nothing to return to on
    /// any of the three platforms, so a cancel flag would be a lie; a window that wants to refuse a
    /// shutdown does it from <see cref="XPlatWindow.Closing"/>, which is raised while the exit is
    /// still being decided.
    /// </remarks>
    public event EventHandler<XPlatExitEventArgs>? Exiting;

    /// <summary>Raised after a window has been shown.</summary>
    public event EventHandler<XPlatWindowEventArgs>? WindowOpened;

    /// <summary>Raised after a window has closed and has left <see cref="Windows"/>.</summary>
    public event EventHandler<XPlatWindowEventArgs>? WindowClosed;

    /// <summary>Raised after <see cref="XPlatTheme.Palette"/> or <see cref="XPlatTheme.FontFamily"/> changed.</summary>
    public event EventHandler? ThemeChanged;

    /// <summary>
    /// Raised when an exception escapes application, window or control code on the UI thread,
    /// instead of letting it unwind into the platform's message pump.
    /// </summary>
    /// <remarks>
    /// The exception is fatal unless a handler sets
    /// <see cref="XPlatUnhandledExceptionEventArgs.Handled"/>: the application then shuts down
    /// through the normal path - windows close, <see cref="Exiting"/> runs, services are disposed -
    /// and <see cref="Run"/> rethrows it. A handled exception is swallowed and the loop keeps
    /// running, unless the failure happened inside the platform's own loop, in which case there is
    /// nothing left to run and the application exits with code 1.
    /// </remarks>
    public event EventHandler<XPlatUnhandledExceptionEventArgs>? UnhandledException;

    /// <summary>Runs the message loop and returns the exit code. Call it once.</summary>
    /// <returns>The process exit code.</returns>
    /// <exception cref="InvalidOperationException"><see cref="Run()"/> was already called.</exception>
    /// <exception cref="Exception">An unhandled exception was raised through <see cref="UnhandledException"/> and not handled.</exception>
    public int Run()
    {
        if (_runCalled)
        {
            throw new InvalidOperationException("Run() may only be called once per application.");
        }

        _runCalled = true;
        _uiThreadId = Environment.CurrentManagedThreadId;
        _running = true;

        try
        {
            if (!_exitRequested)
            {
                Guard(() => Startup?.Invoke(this, EventArgs.Empty));

                // Windows shown before Run() become visible now, in the order they were created.
                foreach (var window in _windows.ToArray())
                {
                    if (_exitRequested)
                    {
                        break;
                    }

                    Guard(window.ShowNow);
                }

                if (!_exitRequested)
                {
                    StartTickTimer();
                    _exitCode = Backend.Run();
                }
            }
        }
        catch (Exception ex) when (IsCatchable(ex))
        {
            // The loop is gone, so a handler can only decide whether Run rethrows.
            RecordFault(ReportUnhandledException(ex) ? null : ex);
        }
        finally
        {
            _running = false;
            TearDown();
        }

        _fault?.Throw();
        return _exitCode;
    }

    /// <summary>Asks the application to exit with code 0.</summary>
    public void Exit() => Exit(0);

    /// <summary>
    /// Asks the application to exit. Every open window is closed first and any of them may refuse,
    /// which aborts the exit; <see cref="Exiting"/> is raised by <see cref="Run"/> once the loop has
    /// ended.
    /// </summary>
    /// <param name="exitCode">The process exit code to return.</param>
    public void Exit(int exitCode)
    {
        if (_tornDown || _exiting)
        {
            return;
        }

        _exiting = true;
        try
        {
            _exitCode = exitCode;
            _exitRequested = true;

            foreach (var window in _windows.ToArray())
            {
                if (!window.RequestClose())
                {
                    _exitRequested = false;
                    return;
                }
            }

            if (_running)
            {
                Backend.Quit(exitCode);
            }
        }
        finally
        {
            _exiting = false;
        }
    }

    /// <summary>Brings <see cref="MainWindow"/> forward, or the first open window when there is none.</summary>
    public void Activate()
    {
        var window = MainWindow ?? (_windows.Count > 0 ? _windows[0] : null);
        window?.Activate();
    }

    /// <summary>True when the calling thread is the UI thread.</summary>
    /// <returns>True on the UI thread.</returns>
    public bool CheckAccess() => Environment.CurrentManagedThreadId == _uiThreadId;

    /// <summary>Runs <paramref name="action"/> on the UI thread, waiting for it when called from another thread.</summary>
    /// <param name="action">The work to run.</param>
    public void Invoke(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        if (CheckAccess())
        {
            action();
            return;
        }

        Backend.Invoke(action, synchronous: true);
    }

    /// <summary>
    /// Queues <paramref name="action"/> on the UI thread and returns immediately. Queued work runs
    /// when the loop runs, so queueing before <see cref="Run"/> is legal but does not run early.
    /// </summary>
    /// <param name="action">The work to queue.</param>
    /// <remarks>
    /// A queued action has no caller to report to, so an exception it throws is routed through
    /// <see cref="UnhandledException"/> rather than allowed to unwind into the message pump, where
    /// a native frame would make it unrecoverable.
    /// </remarks>
    public void BeginInvoke(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        Backend.Invoke(() => Guard(action), synchronous: false);
    }

    /// <summary>Tears the application down. Idempotent, and safe to call after <see cref="Run"/>.</summary>
    public void Dispose() => TearDown();

    internal void RegisterWindow(XPlatWindow window)
    {
        if (!_windows.Contains(window))
        {
            _windows.Add(window);
        }
    }

    internal void UnregisterWindow(XPlatWindow window)
    {
        if (!_windows.Remove(window))
        {
            return;
        }

        Guard(() =>
        {
            WindowClosed?.Invoke(this, new XPlatWindowEventArgs(window));
            CheckShutdownMode(window);
        });
    }

    internal void NotifyWindowShown(XPlatWindow window)
    {
        MainWindow ??= window;
        Guard(() => WindowOpened?.Invoke(this, new XPlatWindowEventArgs(window)));
    }

    /// <summary>
    /// Reports <paramref name="exception"/> through <see cref="UnhandledException"/>.
    /// </summary>
    /// <param name="exception">The exception that escaped UI-thread code.</param>
    /// <returns>True when a handler claimed the exception.</returns>
    internal bool ReportUnhandledException(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        if (UnhandledException is not { } handlers)
        {
            return false;
        }

        var args = new XPlatUnhandledExceptionEventArgs(exception);

        foreach (var handler in handlers.GetInvocationList())
        {
            try
            {
                ((EventHandler<XPlatUnhandledExceptionEventArgs>)handler)(this, args);
            }
            catch (Exception ex) when (IsCatchable(ex))
            {
                // A handler that throws must not stop the ones after it. Its own exception is only
                // used when nothing else was recorded, so the original fault is never displaced.
                _handlerFault ??= ExceptionDispatchInfo.Capture(ex);
            }
        }

        return args.Handled;
    }

    private void CheckShutdownMode(XPlatWindow closed)
    {
        if (_tornDown || !_running || _exitRequested)
        {
            return;
        }

        switch (Options.ShutdownMode)
        {
            case XPlatShutdownMode.OnLastWindowClose when _windows.Count == 0:
            case XPlatShutdownMode.OnMainWindowClose when ReferenceEquals(MainWindow, closed):
                Exit(_exitCode);
                break;
        }
    }

    private void InstallProcessServices()
    {
        if (_provider.GetService<IXPlatClipboard>() is { } clipboard)
        {
            XPlatClipboard.Current = clipboard;
        }

        // A system theme only arrives from a backend. Without one there is no desktop preference
        // to follow, so the palette is left exactly as the application set it - which is what keeps
        // a headless render deterministic regardless of how the host machine is configured.
        if (_provider.GetService<IXPlatSystemTheme>() is { } theme)
        {
            XPlatSystemTheme.Current = theme;
        }

        if (!string.IsNullOrWhiteSpace(Options.FontFamily))
        {
            XPlatTheme.FontFamily = Options.FontFamily;
        }

        if (Options.ForcedDarkMode is { } dark)
        {
            XPlatTheme.Palette = dark ? XPlatPalette.Dark : XPlatPalette.Light;
        }
        else if (Options.FollowSystemAppearance && _provider.GetService<IXPlatSystemTheme>() is not null)
        {
            XPlatSystemTheme.Refresh();
        }
    }

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        Guard(() =>
        {
            foreach (var window in _windows.ToArray())
            {
                window.Backend.SetTitleBarAppearance(XPlatTheme.IsDarkMode);
                window.Host.Invalidate();
            }

            ThemeChanged?.Invoke(this, EventArgs.Empty);
        });
    }

    /// <summary>
    /// One timer drives every window's animation and caret blink, so idle cost does not grow with
    /// the number of open windows.
    /// </summary>
    private void StartTickTimer()
    {
        if (Options.TickInterval <= TimeSpan.Zero)
        {
            return;
        }

        _lastTick = Environment.TickCount64;
        _tickTimer = new XPlatTimer(Options.TickInterval);
        _tickTimer.Tick += OnTick;
        _tickTimer.Start();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        Guard(() =>
        {
            var now = Environment.TickCount64;
            var elapsed = TimeSpan.FromMilliseconds(now - _lastTick);
            _lastTick = now;

            foreach (var window in _windows.ToArray())
            {
                if (window.IsShown && !window.IsClosed)
                {
                    window.Host.ProcessTick(elapsed);
                }
            }
        });
    }

    /// <summary>
    /// Reports an exception caught where it cannot be allowed to propagate - a platform callback
    /// such as a window procedure, whose caller is native code with no way to unwind a managed
    /// exception. Exceptions the model refuses to swallow are rethrown, which at such a boundary
    /// ends the process; that is the correct outcome for them.
    /// </summary>
    /// <param name="exception">The exception that escaped UI-thread code.</param>
    /// <remarks>
    /// <para>
    /// A backend calls this from every callback it hands to the platform, because an exception that
    /// unwinds into a native frame is a fail-fast with no hook. Exceptions the model dispatches
    /// itself - <see cref="Startup"/>, <see cref="WindowOpened"/>, <see cref="WindowClosed"/>,
    /// <see cref="ThemeChanged"/>, <see cref="Exiting"/>, <see cref="BeginInvoke"/> work and the
    /// tick - already go through it, so a backend only has to cover its own callbacks.
    /// </para>
    /// <para>
    /// When there is no <see cref="Current"/> application - a host painted standalone, as
    /// <c>--render</c> does - the exception is rethrown, so a plain <see cref="XPlatUiHost"/> keeps
    /// letting its caller see failures.
    /// </para>
    /// </remarks>
    public static void HandleNativeBoundary(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        if (Current is { } application)
        {
            application.HandleNativeBoundaryException(exception);
            return;
        }

        ExceptionDispatchInfo.Capture(exception).Throw();
    }

    private void HandleNativeBoundaryException(Exception exception)
    {
        if (!IsCatchable(exception))
        {
            ExceptionDispatchInfo.Capture(exception).Throw();
        }

        if (!ReportUnhandledException(exception))
        {
            Fault(exception);
        }
    }

    /// <summary>
    /// Runs work the model dispatches on the UI thread, so an exception reaches
    /// <see cref="UnhandledException"/> instead of unwinding into the platform's message pump.
    /// </summary>
    private void Guard(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            HandleNativeBoundaryException(ex);
        }
    }

    /// <summary>
    /// Records an unhandled exception as fatal and asks the application to exit. The loop is still
    /// alive here, so the shutdown is a normal one and <see cref="Run"/> rethrows afterwards.
    /// </summary>
    private void Fault(Exception exception)
    {
        RecordFault(exception);

        if (!_running || _exitRequested)
        {
            return;
        }

        try
        {
            Exit(_exitCode);
        }
        catch (Exception ex) when (IsCatchable(ex))
        {
            // The shutdown path itself failed. Stop the loop anyway; the first fault still wins.
            RecordFault(ex);
            _exitRequested = true;
            Backend.Quit(_exitCode);
        }
    }

    private void RecordFault(Exception? exception)
    {
        if (exception is not null)
        {
            _fault ??= ExceptionDispatchInfo.Capture(exception);
        }

        _fault ??= _handlerFault;

        if (_exitCode == 0)
        {
            _exitCode = FaultExitCode;
        }
    }

    /// <summary>
    /// Exceptions that no application should be able to swallow: the process state is already
    /// unusable, so reporting them through the model would only hide a failure that must crash.
    /// </summary>
    private static bool IsCatchable(Exception exception) =>
        exception is not (OutOfMemoryException or StackOverflowException or AccessViolationException);

    private void TearDown()
    {
        if (_tornDown)
        {
            return;
        }

        _tornDown = true;
        _running = false;
        XPlatTheme.Changed -= OnThemeChanged;

        if (_tickTimer is not null)
        {
            _tickTimer.Dispose();
            _tickTimer = null;
        }

        if (_runCalled)
        {
            var args = new XPlatExitEventArgs(_exitCode);
            Guard(() => Exiting?.Invoke(this, args));

            // A fault is never reported as success, whatever Exiting left behind.
            _exitCode = args.ExitCode == 0 && _fault is not null ? FaultExitCode : args.ExitCode;
        }

        foreach (var window in _windows.ToArray())
        {
            window.Dispose();
        }

        _windows.Clear();
        _dialogs = null;
        _screens = null;
        _provider.Dispose();

        if (ReferenceEquals(Current, this))
        {
            Current = null;
        }

        Backend.Shutdown();
    }
}
