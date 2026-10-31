using System.Runtime.ExceptionServices;
using AppKit;
using CoreGraphics;
using Foundation;
using Microsoft.Extensions.DependencyInjection;
using SkiaSharp;

namespace XPlatUI.Backend.AppKit;

/// <summary>
/// The AppKit half of <see cref="XPlatApplication"/>: owns the <c>NSApplication</c>, the run loop,
/// the process-wide menu bar and the platform services every macOS window shares.
/// </summary>
/// <remarks>
/// <para>
/// macOS has one menu bar and one application delegate for the whole process, so both live here
/// rather than on a window. The active window's <see cref="XPlatMenuBar"/> is mirrored into
/// <c>NSApplication.MainMenu</c> whenever it changes or the active window changes, which is the
/// only place the macOS presentation of the model differs from Windows and Linux.
/// </para>
/// <para>
/// The loop is <c>NSApplication.Run</c>. Modal work re-enters it - a modal window with
/// <c>RunModalForWindow</c>, a dialog with its own modal session - which is the same run loop in a
/// modal mode, so queued <see cref="Invoke"/> work keeps flowing while a dialog is open.
/// </para>
/// </remarks>
public sealed class AppKitApplicationBackend : IXPlatApplicationBackend
{
    /// <summary>How often queued work is drained and the system appearance re-read.</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    private readonly Queue<Action> _pending = new();
    private readonly object _pendingGate = new();
    private readonly List<AppKitWindowBackend> _windows = [];
    private readonly object _windowsGate = new();

    private AppKitAppDelegate? _appDelegate;
    private NSTimer? _pollTimer;
    private AppKitWindowBackend? _activeWindow;
    private int _uiThreadId;
    private int _exitCode;
    private int _modalDepth;
    private bool _initialized;
    private bool _shutDown;

    /// <summary>Creates the backend. Nothing touches AppKit until <see cref="Initialize"/>.</summary>
    public AppKitApplicationBackend() => _uiThreadId = Environment.CurrentManagedThreadId;

    /// <summary>The application this backend belongs to, or null before <see cref="Initialize"/>.</summary>
    public XPlatApplication? Application { get; private set; }

    /// <summary>
    /// The macOS light/dark preference, as the application will see it.
    /// </summary>
    /// <remarks>
    /// Exposed because the palette sometimes has to be right before an <see cref="XPlatApplication"/>
    /// exists: the harness's headless <c>--render</c> mode paints through the headless backend,
    /// which installs no system theme, so the host applies this one itself.
    /// </remarks>
    public IXPlatSystemTheme SystemTheme { get; } = new AppKitSystemTheme();

    /// <inheritdoc />
    public void Initialize(XPlatApplication application)
    {
        ArgumentNullException.ThrowIfNull(application);

        Application = application;
        _uiThreadId = Environment.CurrentManagedThreadId;

        // Registers the managed/Objective-C bridge. Nothing AppKit related works before this.
        NSApplication.Init();

        var native = NSApplication.SharedApplication;
        native.ActivationPolicy = NSApplicationActivationPolicy.Regular;

        // The delegate must stay rooted: AppKit holds an unretained reference to it.
        _appDelegate = new AppKitAppDelegate(this);
        native.Delegate = _appDelegate;

        // Windows are created and shown before Run is entered, which is earlier than AppKit
        // finishes launching on its own. Finishing the launch here means the windows that follow
        // are ordered in normally instead of waiting for a launch that has not happened yet.
        native.FinishLaunching();

        if (SystemTheme is AppKitSystemTheme theme)
        {
            theme.Changed += OnSystemThemeChanged;
        }

        _initialized = true;
        RebuildMenuBar();
    }

    /// <inheritdoc />
    public int Run()
    {
        _uiThreadId = Environment.CurrentManagedThreadId;

        if (!_initialized)
        {
            return -1;
        }

        StartPollTimer();
        NSApplication.SharedApplication.Run();
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

        var native = NSApplication.SharedApplication;

        // A modal session owns the loop while it runs, so ending the loop means ending that
        // session first; Stop alone would not be seen until the session returned.
        if (_modalDepth > 0)
        {
            native.AbortModal();
        }

        native.Stop(native);
        WakeUp();
    }

    /// <summary>
    /// Posts an empty application-defined event so a blocked run loop comes back around and
    /// notices <see cref="NSApplication.Stop"/>.
    /// </summary>
    /// <remarks>
    /// <c>-stop:</c> only clears a flag that <c>-run</c> checks between events. The loop is parked
    /// inside the run loop source waiting for the next event, and stopping the application does not
    /// generate one, so without a wake-up the loop stays parked and the process never exits.
    /// </remarks>
    private static void WakeUp()
    {
        using var wake = NSEvent.OtherEvent(
            NSEventType.ApplicationDefined,
            CGPoint.Empty,
            0,
            0,
            0,
            null!,
            0,
            IntPtr.Zero,
            IntPtr.Zero);

        NSApplication.SharedApplication.PostEvent(wake, true);
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

        var backend = new AppKitWindowBackend(this, window);

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
        services.AddSingleton<AppKitClipboard>();
        services.AddSingleton<IXPlatClipboard>(static provider => provider.GetRequiredService<AppKitClipboard>());
        services.AddSingleton(SystemTheme);
        services.AddSingleton<IXPlatDialogService, AppKitDialogService>();
    }

    /// <inheritdoc />
    public IReadOnlyList<XPlatScreen> GetScreens()
    {
        var screens = new List<XPlatScreen>();

        NSScreen[]? native;
        try
        {
            native = NSScreen.Screens;
        }
        catch (Exception)
        {
            // No window server - a service, a CI agent, an ssh session. Reported below as one
            // synthetic display so a window still has somewhere to centre itself.
            native = null;
        }

        if (native is null || native.Length == 0)
        {
            screens.Add(new XPlatScreen(
                new SKRect(0f, 0f, 1920f, 1080f),
                new SKRect(0f, 0f, 1920f, 1055f),
                1f,
                isPrimary: true));

            return screens;
        }

        // AppKit's screen space has its origin at the bottom left of the primary display and y
        // growing up; the library's is top-left and y down. Converting against the primary
        // display's height is what makes a second display stacked above it report a negative y,
        // which is what the library expects.
        var primaryHeight = native[0].Frame.Height;

        for (var i = 0; i < native.Length; i++)
        {
            screens.Add(ToScreen(native[i], primaryHeight, isPrimary: i == 0));
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

        _activeWindow = null;

        if (SystemTheme is AppKitSystemTheme theme)
        {
            theme.Changed -= OnSystemThemeChanged;
        }

        if (_appDelegate is not null)
        {
            NSApplication.SharedApplication.Delegate = null!;
            _appDelegate.Dispose();
            _appDelegate = null;
        }

        XPlatContextMenuPresenter.Current = null;

        if (ReferenceEquals(XPlatSystemTheme.Current, SystemTheme))
        {
            XPlatSystemTheme.Current = null;
        }

        _initialized = false;
    }

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
    internal AppKitWindowBackend? ActiveWindow => _activeWindow;

    /// <summary>The menu bar the process should show right now, or null for the standard one.</summary>
    internal XPlatMenuBar? MenuBarForActiveWindow()
    {
        if (_activeWindow is { } active)
        {
            return active.MenuBar;
        }

        // Nothing is focused - during startup, or after the last window closed but before the
        // loop ended - so the first window's bar stands in for the active one.
        lock (_windowsGate)
        {
            return _windows.Count > 0 ? _windows[0].MenuBar : null;
        }
    }

    /// <summary>Replaces <c>NSApplication.MainMenu</c> with the active window's menu bar.</summary>
    internal void RebuildMenuBar()
    {
        if (!_initialized)
        {
            return;
        }

        NSApplication.SharedApplication.MainMenu = AppKitMenuBar.Build(
            Application?.Options.ApplicationName ?? "XPlat UI",
            MenuBarForActiveWindow(),
            this);
    }

    /// <summary>Records that <paramref name="window"/> became the active window.</summary>
    internal void NotifyWindowActivated(AppKitWindowBackend window)
    {
        if (ReferenceEquals(_activeWindow, window))
        {
            return;
        }

        _activeWindow = window;

        // A context menu is anchored to the view that shows it, so the presenter follows the
        // active window rather than being installed once per window.
        XPlatContextMenuPresenter.Current = window.Presenter;
        RebuildMenuBar();
    }

    /// <summary>Records that <paramref name="window"/> stopped being the active window.</summary>
    internal void NotifyWindowDeactivated(AppKitWindowBackend window)
    {
        if (ReferenceEquals(_activeWindow, window))
        {
            _activeWindow = null;
        }
    }

    /// <summary>Drops a disposed window from the bookkeeping the menu bar and screens rely on.</summary>
    internal void RemoveWindow(AppKitWindowBackend window)
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

        RebuildMenuBar();
    }

    /// <summary>
    /// Feeds one of the standard editing accelerators into the active window, so the focused
    /// control handles it exactly as it would a real key press. AppKit consumes a menu's key
    /// equivalent before the view ever sees it, so the Edit menu has to route these itself.
    /// </summary>
    internal void SendAccelerator(XPlatKey key)
    {
        if (_activeWindow is { } window)
        {
            window.Host.ProcessKeyDown(key, XPlatModifiers.Control);
        }
    }

    /// <summary>Ends the application through the model, so every window gets to cancel the close.</summary>
    internal void RequestExit()
    {
        if (Application is { } application)
        {
            application.Exit();
            return;
        }

        NSApplication.SharedApplication.Stop(NSApplication.SharedApplication);
        WakeUp();
    }

    /// <summary>Called when AppKit asks the process to terminate, which includes logging out.</summary>
    internal void OnTerminationRequested()
    {
        // The model owns shutdown, so a quit closes the windows itself and ends the loop with
        // Stop; AppKit's own termination is cancelled. A window that refuses leaves the
        // application running, which is the whole point of asking it first.
        RequestExit();
    }

    internal void EnterModal() => _modalDepth++;

    internal void ExitModal() => _modalDepth = Math.Max(_modalDepth - 1, 0);

    /// <summary>Converts one AppKit screen into the library's logical, y-down space.</summary>
    internal static XPlatScreen ToScreen(NSScreen screen, double primaryHeight, bool isPrimary)
    {
        var frame = screen.Frame;
        var visible = screen.VisibleFrame;

        return new XPlatScreen(
            ToLogical(frame, primaryHeight),
            ToLogical(visible, primaryHeight),
            (float)screen.BackingScaleFactor,
            isPrimary);
    }

    /// <summary>The display a window is on, in logical units.</summary>
    internal static XPlatScreen? ScreenForWindow(NSWindow window)
    {
        var screen = window.Screen;
        if (screen is null)
        {
            return null;
        }

        var all = NSScreen.Screens;
        var primaryHeight = all is { Length: > 0 } ? all[0].Frame.Height : screen.Frame.Height;
        var isPrimary = all is { Length: > 0 } && ReferenceEquals(all[0], screen);

        return ToScreen(screen, primaryHeight, isPrimary);
    }

    /// <summary>The top-left corner of <paramref name="window"/>, in the library's logical space.</summary>
    internal static SKPoint LocationOf(NSWindow window) => ToLogical(window.Frame, PrimaryHeightFor(window)).Location;

    /// <summary>The AppKit frame for a window at <paramref name="location"/> with a content size of <paramref name="contentSize"/>.</summary>
    internal static CGRect FrameFor(SKPoint location, SKSize contentSize, double primaryHeight) => new(
        location.X,
        primaryHeight - location.Y - contentSize.Height,
        contentSize.Width,
        contentSize.Height);

    private static double PrimaryHeightFor(NSWindow window)
    {
        var screens = NSScreen.Screens;
        return screens is { Length: > 0 } ? screens[0].Frame.Height : window.Frame.Height;
    }

    private static SKRect ToLogical(CGRect rect, double primaryHeight) => new(
        (float)rect.X,
        (float)(primaryHeight - rect.Y - rect.Height),
        (float)(rect.X + rect.Width),
        (float)(primaryHeight - rect.Y));

    /// <summary>
    /// Hands the queued work to the main thread. This is the immediate path; the poll timer is the
    /// backstop for the run loop modes that do not service it, such as a live resize.
    /// </summary>
    private void Wake()
    {
        if (!_initialized)
        {
            // Nothing is draining yet: Run has not been entered, so the queue waits for it.
            return;
        }

        NSApplication.SharedApplication.BeginInvokeOnMainThread(Drain);
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
        if (_pollTimer is not null)
        {
            return;
        }

        // Repeating, and not scheduled: the run loop is the one that starts it, in common modes, so
        // it keeps firing while a modal dialog or a live resize owns the loop. Without that the
        // caret would stop blinking for as long as a dialog is open.
        _pollTimer = NSTimer.CreateRepeatingTimer(PollInterval, _ => OnPoll());
        NSRunLoop.Main.AddTimer(_pollTimer, NSRunLoopMode.Common);
    }

    private void StopPollTimer()
    {
        if (_pollTimer is null)
        {
            return;
        }

        _pollTimer.Invalidate();
        _pollTimer.Dispose();
        _pollTimer = null;
    }

    private void OnPoll()
    {
        Drain();

        (SystemTheme as AppKitSystemTheme)?.Poll();

        AppKitWindowBackend[] windows;
        lock (_windowsGate)
        {
            windows = [.. _windows];
        }

        foreach (var window in windows)
        {
            window.ReportStatistics();
        }
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

    /// <summary>
    /// The application delegate. It exists so AppKit's lifecycle decisions are made by the model
    /// rather than by AppKit's defaults, which assume a single window application.
    /// </summary>
    private sealed class AppKitAppDelegate : NSApplicationDelegate
    {
        private readonly AppKitApplicationBackend _application;

        public AppKitAppDelegate(AppKitApplicationBackend application) => _application = application;

        /// <summary>
        /// AppKit would otherwise end the process when the last window closes. Whether that ends
        /// the application is <see cref="XPlatShutdownMode"/>'s decision, so it is declined here.
        /// </summary>
        public override bool ApplicationShouldTerminateAfterLastWindowClosed(NSApplication sender) => false;

        public override NSApplicationTerminateReply ApplicationShouldTerminate(NSApplication sender)
        {
            _application.OnTerminationRequested();
            return NSApplicationTerminateReply.Cancel;
        }
    }
}
