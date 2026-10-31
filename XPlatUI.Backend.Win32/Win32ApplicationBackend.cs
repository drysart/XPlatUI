using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using SkiaSharp;

namespace XPlatUI.Backend.Win32;

/// <summary>
/// The Windows backend for <see cref="XPlatApplication"/>: a Win32 message loop, Win32 windows and
/// the Win32 clipboard and appearance services.
/// </summary>
/// <remarks>
/// <para>
/// The loop is a plain <c>GetMessage</c>/<c>DispatchMessage</c> pair, except that it also owns a
/// <b>message-only window</b> (<c>HWND_MESSAGE</c>). That window is the wake-up channel: a
/// callback queued by <see cref="Invoke"/> posts <c>WM_APP_INVOKE</c> to it, so the loop drains the
/// queue instead of leaving it parked until some unrelated input arrives. Being a real window, it
/// keeps receiving those messages inside a nested modal loop as well, which is what
/// <see cref="IXPlatDialogService"/> needs.
/// </para>
/// <para>
/// Pick the backend with <c>#if</c>; there is no reflection anywhere in this assembly, so it stays
/// trimming and NativeAOT safe.
/// </para>
/// </remarks>
public sealed class Win32ApplicationBackend : IXPlatApplicationBackend
{
    private const string MessageWindowClass = "XPlatUI.MessageWindow";

    private readonly Queue<Action> _pending = new();
    private readonly List<Win32WindowBackend> _windows = new();
    private readonly object _gate = new();

    private NativeMethods.WndProc? _messageProc;
    private IntPtr _moduleHandle;
    private IntPtr _hwndMessage;
    private XPlatInstanceLock? _instanceLock;
    private bool _classRegistered;
    private bool _comInitialized;
    private int _uiThreadId;

    /// <summary>
    /// Shows context menus with <c>TrackPopupMenuEx</c> even on Windows 11, which otherwise gets
    /// the rounded, themed popup. Useful for comparing the two presentations by eye.
    /// </summary>
    public bool ForceClassicMenus { get; set; }

    /// <summary>The application this backend belongs to, or null before <see cref="Initialize"/>.</summary>
    public XPlatApplication? Application { get; private set; }

    /// <summary>
    /// The Windows light/dark preference, as the application will see it.
    /// </summary>
    /// <remarks>
    /// Exposed because the palette sometimes has to be right before an <see cref="XPlatApplication"/>
    /// exists: the harness's headless <c>--render</c> mode paints through the headless backend,
    /// which installs no system theme, so the host applies this one itself.
    /// </remarks>
    public IXPlatSystemTheme SystemTheme { get; } = new Win32SystemTheme();

    /// <summary>
    /// True when the UI thread is in a single-threaded COM apartment, which the shell's file dialogs
    /// require. False means <see cref="IXPlatDialogService.OpenFile"/> and friends will refuse rather
    /// than hang; see <see cref="InitializeCom"/>.
    /// </summary>
    internal bool IsSingleThreadedApartment { get; private set; }

    /// <inheritdoc />
    public void Initialize(XPlatApplication application)
    {
        ArgumentNullException.ThrowIfNull(application);

        Application = application;
        _uiThreadId = Environment.CurrentManagedThreadId;

        var options = application.Options;

        if (options.AttachConsole)
        {
            NativeMethods.AttachConsole(NativeMethods.AttachParentProcess);
        }

        ApplyDpiAwareness(options.DpiAwareness);

        IsSingleThreadedApartment = InitializeCom();

        Win32ContextMenuPresenter.ForceClassicMenu = ForceClassicMenus;

        EnsureMessageWindow();
    }

    /// <inheritdoc />
    public int Run()
    {
        _uiThreadId = Environment.CurrentManagedThreadId;

        if (!EnsureMessageWindow())
        {
            return -1;
        }

        return Pump();
    }

    /// <inheritdoc />
    /// <remarks>
    /// Windows has no single-instance mechanism of its own, so this is
    /// <see cref="XPlatInstanceLock"/>. The foreground permission is handed out first: the process
    /// the user just started is the one allowed to say who may take the foreground, so it has to do
    /// so before it finds out whether it is the second instance.
    /// </remarks>
    public bool TryClaimSingleInstance()
    {
        NativeMethods.AllowSetForegroundWindow(NativeMethods.AsfwAny);

        _instanceLock = XPlatInstanceLock.Acquire(
            Application?.InstanceKey ?? nameof(XPlatApplication),
            Application?.Options.ResolveCommandLine() ?? []);

        if (_instanceLock is null)
        {
            return false;
        }

        _instanceLock.SecondInstance += (_, e) => SecondInstance?.Invoke(this, e);
        return true;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Never raised: Windows activates a window from the shell itself, so the backend has no
    /// re-activation to report.
    /// </remarks>
    public event EventHandler? Activated
    {
        add { }
        remove { }
    }

    /// <inheritdoc />
    public event EventHandler<XPlatSecondInstanceEventArgs>? SecondInstance;

    /// <inheritdoc />
    public void Quit(int exitCode)
    {
        if (_hwndMessage != IntPtr.Zero)
        {
            NativeMethods.PostMessageW(_hwndMessage, NativeMethods.WmAppQuit, new IntPtr(exitCode), IntPtr.Zero);
            return;
        }

        // No message window yet, so nothing can be posted: end the calling thread's own queue.
        NativeMethods.PostQuitMessage(exitCode);
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
            lock (_gate)
            {
                _pending.Enqueue(action);
            }

            Wake();
            return;
        }

        using var completed = new ManualResetEventSlim();
        ExceptionDispatchInfo? failure = null;

        lock (_gate)
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

        var backend = new Win32WindowBackend(this, window);

        lock (_gate)
        {
            _windows.Add(backend);
        }

        return backend;
    }

    /// <inheritdoc />
    public void RegisterServices(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<Win32Clipboard>();
        services.AddSingleton<IXPlatClipboard>(static provider => provider.GetRequiredService<Win32Clipboard>());
        services.AddSingleton(SystemTheme);
        services.AddSingleton(this);
        services.AddSingleton<IXPlatDialogService, Win32DialogService>();
    }

    /// <inheritdoc />
    public IReadOnlyList<XPlatScreen> GetScreens()
    {
        var screens = new List<XPlatScreen>();

        NativeMethods.EnumDisplayMonitors(
            IntPtr.Zero,
            IntPtr.Zero,
            (IntPtr monitor, IntPtr hdc, ref NativeMethods.RECT rect, IntPtr data) =>
            {
                screens.Add(ToScreen(monitor, rect));
                return true;
            },
            IntPtr.Zero);

        if (screens.Count == 0)
        {
            // No monitor would enumerate (session 0, a service): fall back to the work area so a
            // window still has somewhere to centre itself.
            var work = NativeMethods.GetPrimaryWorkArea();
            var scale = ScaleOf(NativeMethods.GetDpiForSystem());
            screens.Add(new XPlatScreen(ToLogical(work, scale), ToLogical(work, scale), scale, isPrimary: true));
        }

        return screens;
    }

    /// <inheritdoc />
    public void Shutdown()
    {
        _instanceLock?.Dispose();
        _instanceLock = null;

        lock (_gate)
        {
            _pending.Clear();
            _windows.Clear();
        }

        if (_hwndMessage != IntPtr.Zero)
        {
            NativeMethods.DestroyWindow(_hwndMessage);
            _hwndMessage = IntPtr.Zero;
        }

        if (_classRegistered)
        {
            NativeMethods.UnregisterClassW(MessageWindowClass, _moduleHandle);
            _classRegistered = false;
        }

        if (_comInitialized)
        {
            NativeMethods.CoUninitialize();
            _comInitialized = false;
        }
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

    /// <summary>The display <paramref name="hwnd"/> is on, in logical units.</summary>
    internal static XPlatScreen ScreenForWindow(IntPtr hwnd)
    {
        var monitor = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MonitorDefaultToNearest);

        if (monitor == IntPtr.Zero)
        {
            return ToScreen(IntPtr.Zero, new NativeMethods.RECT { Right = 1920, Bottom = 1080 });
        }

        var info = new NativeMethods.MONITORINFO { cbSize = (uint)Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        var bounds = NativeMethods.GetMonitorInfoW(monitor, ref info) ? info.rcMonitor : default;
        return ToScreen(monitor, bounds);
    }

    /// <summary>
    /// Runs the loop until <paramref name="keepRunning"/> turns false, without returning to the
    /// caller of <see cref="Run"/>. This is what makes a modal window modal.
    /// </summary>
    internal void RunNestedLoop(Func<bool> keepRunning)
    {
        ArgumentNullException.ThrowIfNull(keepRunning);

        while (keepRunning())
        {
            var result = NativeMethods.GetMessageW(out var message, IntPtr.Zero, 0, 0);

            if (result == 0)
            {
                // The modal window consumed the quit. Hand it back so the outer loop ends too
                // instead of resuming with no windows left.
                NativeMethods.PostQuitMessage((int)message.wParam);
                return;
            }

            if (result < 0)
            {
                return;
            }

            if (IsKeyHandledByUi(message))
            {
                continue;
            }

            NativeMethods.TranslateMessage(ref message);
            NativeMethods.DispatchMessageW(ref message);
        }
    }

    /// <summary>Drops a window from the routing table. Called by <see cref="Win32WindowBackend.Dispose"/>.</summary>
    internal void RemoveWindow(Win32WindowBackend window)
    {
        lock (_gate)
        {
            _windows.Remove(window);
        }
    }

    private int Pump()
    {
        while (true)
        {
            var result = NativeMethods.GetMessageW(out var message, IntPtr.Zero, 0, 0);

            if (result == 0)
            {
                return (int)message.wParam;
            }

            if (result < 0)
            {
                return -1;
            }

            if (IsKeyHandledByUi(message))
            {
                continue;
            }

            NativeMethods.TranslateMessage(ref message);
            NativeMethods.DispatchMessageW(ref message);
        }
    }

    /// <summary>
    /// Swallowing the key here (instead of in a window procedure) keeps <c>TranslateMessage</c>
    /// from also turning it into a <c>WM_CHAR</c>, which would double up text input.
    /// </summary>
    private bool IsKeyHandledByUi(in NativeMethods.MSG message)
    {
        if (message.message != NativeMethods.WmKeyDown)
        {
            return false;
        }

        return FindWindow(message.hwnd)?.TryHandleKeyDown(message.wParam) == true;
    }

    private Win32WindowBackend? FindWindow(IntPtr hwnd)
    {
        lock (_gate)
        {
            foreach (var window in _windows)
            {
                if (window.Handle == hwnd)
                {
                    return window;
                }
            }
        }

        return null;
    }

    private void Wake()
    {
        if (_hwndMessage != IntPtr.Zero)
        {
            NativeMethods.PostMessageW(_hwndMessage, NativeMethods.WmAppInvoke, IntPtr.Zero, IntPtr.Zero);
        }
    }

    private void DrainPending()
    {
        while (true)
        {
            Action action;

            lock (_gate)
            {
                if (_pending.Count == 0)
                {
                    return;
                }

                action = _pending.Dequeue();
            }

            action();
        }
    }

    private bool EnsureMessageWindow()
    {
        if (_hwndMessage != IntPtr.Zero)
        {
            return true;
        }

        _moduleHandle = NativeMethods.GetModuleHandleW(null);
        _messageProc ??= MessageWindowProc;

        var windowClass = new NativeMethods.WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<NativeMethods.WNDCLASSEXW>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_messageProc),
            hInstance = _moduleHandle,
            lpszClassName = MessageWindowClass,
        };

        if (NativeMethods.RegisterClassExW(ref windowClass) == 0)
        {
            return false;
        }

        _classRegistered = true;

        _hwndMessage = NativeMethods.CreateWindowExW(
            0,
            MessageWindowClass,
            string.Empty,
            0,
            0,
            0,
            0,
            0,
            NativeMethods.HwndMessage,
            IntPtr.Zero,
            _moduleHandle,
            IntPtr.Zero);

        return _hwndMessage != IntPtr.Zero;
    }

    private IntPtr MessageWindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        switch (message)
        {
            case NativeMethods.WmAppInvoke:
                DrainPending();
                return IntPtr.Zero;

            case NativeMethods.WmAppQuit:
                DrainPending();
                NativeMethods.PostQuitMessage((int)wParam);
                return IntPtr.Zero;
        }

        return NativeMethods.DefWindowProcW(hwnd, message, wParam, lParam);
    }

    private static void ApplyDpiAwareness(XPlatDpiAwareness awareness)
    {
        var context = awareness switch
        {
            XPlatDpiAwareness.Unaware => NativeMethods.DpiAwarenessUnaware,
            XPlatDpiAwareness.System => NativeMethods.DpiAwarenessSystem,
            _ => NativeMethods.DpiAwarenessPerMonitorV2,
        };

        NativeMethods.SetProcessDpiAwarenessContext(context);
    }

    /// <summary>
    /// Claims the UI thread's COM apartment for the life of the application, and reports whether it
    /// ended up single-threaded.
    /// </summary>
    /// <remarks>
    /// The shell's file dialogs are in-proc COM objects that need a single-threaded apartment:
    /// <c>IFileDialog::Show</c> never returns on a multi-threaded one, and never even creates its
    /// window, which reads as an application that froze the moment the user asked for a file.
    /// <para>
    /// The apartment is decided before this can run - the runtime initializes the thread as MTA
    /// unless the entry point carries <see cref="STAThreadAttribute"/> - so this confirms the
    /// apartment rather than choosing it. <c>CoInitializeEx</c> fails with
    /// <c>RPC_E_CHANGED_MODE</c> on a thread that is already MTA, and no amount of COM calls
    /// afterwards can change it. When that happens the backend carries on: the clipboard and every
    /// window still work, and the file dialogs throw with an explanation instead of hanging.
    /// </para>
    /// </remarks>
    private bool InitializeCom()
    {
        var hr = NativeMethods.CoInitializeEx(IntPtr.Zero, NativeMethods.CoInitApartmentThreaded);

        // S_FALSE means the thread was already STA, which is the usual case: [STAThread] on Main
        // got there first. Either way one CoUninitialize balances this call.
        _comInitialized = hr is NativeMethods.SOk or NativeMethods.SFalse;
        return _comInitialized;
    }

    private static XPlatScreen ToScreen(IntPtr monitor, NativeMethods.RECT bounds)
    {
        var info = new NativeMethods.MONITORINFO { cbSize = (uint)Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        var hasInfo = NativeMethods.GetMonitorInfoW(monitor, ref info);

        if (hasInfo && info.rcMonitor.Right > info.rcMonitor.Left && info.rcMonitor.Bottom > info.rcMonitor.Top)
        {
            bounds = info.rcMonitor;
        }

        var work = hasInfo && info.rcWork.Right > info.rcWork.Left ? info.rcWork : bounds;
        var isPrimary = hasInfo && (info.dwFlags & NativeMethods.MonitorInfoPrimary) != 0;
        var scale = ScaleOf(monitor);

        return new XPlatScreen(ToLogical(bounds, scale), ToLogical(work, scale), scale, isPrimary);
    }

    private static float ScaleOf(IntPtr monitor)
    {
        if (monitor != IntPtr.Zero &&
            NativeMethods.GetDpiForMonitor(monitor, NativeMethods.MdtEffectiveDpi, out var dpiX, out _) == 0 &&
            dpiX > 0)
        {
            return dpiX / 96f;
        }

        return ScaleOf(NativeMethods.GetDpiForSystem());
    }

    private static float ScaleOf(uint dpi) => dpi > 0 ? dpi / 96f : 1f;

    private static SKRect ToLogical(NativeMethods.RECT rect, float scale) =>
        new(rect.Left / scale, rect.Top / scale, rect.Right / scale, rect.Bottom / scale);
}
