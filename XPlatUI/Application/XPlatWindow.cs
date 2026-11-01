using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using SkiaSharp;

namespace XPlatUI;

/// <summary>
/// One OS window. Thin: it forwards to an <see cref="IXPlatWindowBackend"/> and raises the
/// cross-platform events. It does not duplicate <see cref="XPlatUiHost"/>; <see cref="Host"/>
/// <em>is</em> the host.
/// </summary>
/// <remarks>
/// <para>
/// A window owns a service scope, so a service registered with
/// <see cref="XPlatServiceCollectionExtensions.AddWindowScoped{TService}"/> is created once per
/// window and disposed when it closes. Input never passes through this type: the backend forwards
/// pointer and key events straight into <see cref="Host"/>.
/// </para>
/// <para>
/// Creating a window requires an <see cref="XPlatApplication"/> to exist. The application does not
/// own the window's lifetime; you do, with <see cref="Dispose"/>.
/// </para>
/// </remarks>
public sealed partial class XPlatWindow : IDisposable
{
    private readonly XPlatApplication _application;
    private readonly IServiceScope _scope;
    private XPlatControl? _content;
    private XPlatMenuBar? _menuBar;
    private XPlatWindow? _owner;
    private bool _clientSizeSet;
    private bool _shown;
    private bool _shownRaised;
    private bool _closed;

    /// <summary>Creates an untitled window with no content.</summary>
    /// <exception cref="InvalidOperationException">No <see cref="XPlatApplication"/> exists yet.</exception>
    public XPlatWindow()
        : this(string.Empty, null)
    {
    }

    /// <summary>Creates an untitled window showing <paramref name="content"/>.</summary>
    /// <param name="content">The root of the window's control tree.</param>
    public XPlatWindow(XPlatControl? content)
        : this(string.Empty, content)
    {
    }

    /// <summary>Creates a window titled <paramref name="title"/> showing <paramref name="content"/>.</summary>
    /// <param name="title">Title bar text.</param>
    /// <param name="content">The root of the window's control tree.</param>
    /// <exception cref="InvalidOperationException">No <see cref="XPlatApplication"/> exists yet.</exception>
    public XPlatWindow(string title, XPlatControl? content)
    {
        _application = XPlatApplication.Current
            ?? throw new InvalidOperationException(
                "Create the XPlatApplication before creating windows.");

        _scope = _application.Services.CreateScope();
        _scope.ServiceProvider.GetRequiredService<XPlatWindowAccessor>().Window = this;

        Backend = _application.Backend.CreateWindow(this);
        Host = Backend.Host;
        Host.Services = _scope.ServiceProvider;
        Host.Window = this;

        if (!string.IsNullOrEmpty(title))
        {
            Backend.Title = title;
        }

        // The application-wide icon is the default every window starts with; a window that wants a
        // different one assigns Icon afterwards.
        Backend.Icon = _application.Options.Icon;

        if (content is not null)
        {
            Content = content;
        }

        Backend.Shown += OnBackendShown;
        Backend.CloseRequested += OnBackendCloseRequested;
        Backend.Closed += OnBackendClosed;
        Backend.Activated += OnBackendActivated;
        Backend.Deactivated += OnBackendDeactivated;
        Backend.ClientSizeChanged += OnBackendClientSizeChanged;
        Backend.ScaleChanged += OnBackendScaleChanged;
        Backend.StateChanged += OnBackendStateChanged;
    }

    /// <summary>The control tree's root. Assigning it is the same as assigning <see cref="XPlatUiHost.Root"/>.</summary>
    public XPlatControl? Content
    {
        get => _content;
        set
        {
            if (ReferenceEquals(_content, value))
            {
                return;
            }

            _content = value;
            Host.Root = value;
        }
    }

    /// <summary>The host that paints this window's control tree.</summary>
    public XPlatUiHost Host { get; }

    /// <summary>The application this window belongs to.</summary>
    public XPlatApplication Application => _application;

    /// <summary>This window's service scope. Disposed when the window closes.</summary>
    public IServiceProvider Services => _scope.ServiceProvider;

    /// <summary>
    /// The platform half, as the escape hatch for platform-specific work: cast it to the backend's
    /// own type to reach an <c>NSWindow</c> or an <c>HWND</c>.
    /// </summary>
    public IXPlatWindowBackend Backend { get; }

    /// <summary>The display the window is on, or null before it has been shown.</summary>
    public XPlatScreen? Screen => Backend.Screen;

    /// <summary>Logical-to-physical pixel ratio of the display the window is on.</summary>
    public float ScaleFactor => Backend.ScaleFactor;

    /// <summary>Title bar text.</summary>
    public string Title
    {
        get => Backend.Title;
        set => Backend.Title = value ?? string.Empty;
    }

    /// <summary>
    /// The window's icon, or null to keep the platform's generic application icon. Defaults to
    /// <see cref="XPlatApplicationOptions.Icon"/>, which every window gets when it is created.
    /// </summary>
    /// <remarks>
    /// The icon reaches the title bar and the taskbar on Windows and the application icon on macOS,
    /// which is where a Mac shows it. GTK4 draws whatever the desktop's icon theme provides for
    /// <see cref="XPlatIcon.Name"/>, so a pixel-only icon has no effect there.
    /// </remarks>
    public XPlatIcon? Icon
    {
        get => Backend.Icon;
        set => Backend.Icon = value;
    }

    /// <summary>
    /// Client area size, in logical pixels. Reading it before the window is shown returns whatever
    /// the backend last reported. Setting it marks the size as explicit, which stops the window
    /// from sizing itself to <see cref="Content"/>.
    /// </summary>
    public SKSize ClientSize
    {
        get => Backend.ClientSize;
        set
        {
            _clientSizeSet = true;
            Backend.ClientSize = value;
        }
    }

    /// <summary>Smallest client area the user may drag the window to, in logical pixels.</summary>
    public SKSize MinimumClientSize
    {
        get => Backend.MinimumClientSize;
        set => Backend.MinimumClientSize = value;
    }

    /// <summary>Largest client area the user may drag the window to, in logical pixels.</summary>
    public SKSize MaximumClientSize
    {
        get => Backend.MaximumClientSize;
        set => Backend.MaximumClientSize = value;
    }

    /// <summary>Top-left corner in logical screen coordinates.</summary>
    public SKPoint Location
    {
        get => Backend.Location;
        set => Backend.Location = value;
    }

    /// <summary>False for a fixed-size window.</summary>
    public bool Resizable
    {
        get => Backend.Resizable;
        set => Backend.Resizable = value;
    }

    /// <summary>True to keep the window above all others.</summary>
    /// <remarks>
    /// Windows and macOS honour this. Linux does not: GTK4 removed the keep-above call it would need,
    /// and Wayland leaves stacking to the compositor, so the value is recorded and otherwise ignored.
    /// Treat it as best effort rather than as the only thing keeping a window in front - use
    /// <see cref="Owner"/> for that, and note that ownership is itself only a hint on Wayland.
    /// </remarks>
    public bool TopMost
    {
        get => Backend.TopMost;
        set => Backend.TopMost = value;
    }

    /// <summary>The window's display state.</summary>
    public XPlatWindowState State
    {
        get => Backend.State;
        set => Backend.State = value;
    }

    /// <summary>
    /// The window this one is owned by, or null. An owned window always sits above its owner,
    /// minimizes with it and is closed with it. Also the default owner for <see cref="ShowDialog()"/>.
    /// </summary>
    /// <remarks>
    /// Assigning an owner after the window is shown restacks the native window, so a window can be
    /// re-parented at run time.
    /// </remarks>
    public XPlatWindow? Owner
    {
        get => _owner;
        set
        {
            if (ReferenceEquals(_owner, value))
            {
                return;
            }

            if (ReferenceEquals(value, this))
            {
                throw new ArgumentException("A window cannot own itself.", nameof(value));
            }

            _owner = value;
            Backend.Owner = value?.Backend;
        }
    }

    /// <summary>
    /// The window's menu bar, or null to inherit <see cref="XPlatApplicationOptions.MenuBar"/>.
    /// Per window on Windows and Linux; the macOS backend mirrors the active window's bar into the
    /// single global menu bar, which is the only place the two presentations diverge.
    /// </summary>
    public XPlatMenuBar? MenuBar
    {
        get => _menuBar;
        set
        {
            _menuBar = value;
            Backend.MenuBar = value ?? _application.Options.MenuBar;
        }
    }

    /// <summary>
    /// What a modal <see cref="ShowDialog(XPlatWindow?)"/> returns once the window has closed.
    /// Defaults to <see cref="XPlatDialogResult.Cancel"/>, the value a dialog that was dismissed
    /// without choosing anything reports. Set it from the code that closes the window.
    /// </summary>
    public XPlatDialogResult DialogResult { get; set; } = XPlatDialogResult.Cancel;

    /// <summary>True once <see cref="Show()"/> has been called and before the window has closed.</summary>
    public bool IsShown => _shown;

    /// <summary>True once the window has closed or been disposed.</summary>
    public bool IsClosed => _closed;

    /// <summary>Raised once the window is visible.</summary>
    public event EventHandler? Shown;

    /// <summary>
    /// Raised when the window is about to close, whether from <see cref="Close"/>, a platform close
    /// button or <see cref="XPlatApplication.Exit(int)"/>. Set
    /// <see cref="CancelEventArgs.Cancel"/> to keep it open.
    /// </summary>
    public event EventHandler<CancelEventArgs>? Closing;

    /// <summary>Raised after the window has closed and released its service scope.</summary>
    public event EventHandler? Closed;

    /// <summary>Raised when the window becomes the active window.</summary>
    public event EventHandler? Activated;

    /// <summary>Raised when the window stops being the active window.</summary>
    public event EventHandler? Deactivated;

    /// <summary>Raised when the client area changes size, including as a result of a DPI change.</summary>
    public event EventHandler? ClientSizeChanged;

    /// <summary>Raised when the window moves to a display with a different scale factor.</summary>
    public event EventHandler? ScaleChanged;

    /// <summary>Raised when <see cref="State"/> changes.</summary>
    public event EventHandler? StateChanged;

    /// <summary>
    /// Converts a point in this window's client space to screen coordinates in the same logical unit
    /// as <see cref="Location"/>. A backend converts the result to physical pixels when it positions
    /// a native popup.
    /// </summary>
    /// <param name="logical">The client-space point.</param>
    /// <returns>The point in logical screen coordinates.</returns>
    public SKPoint LogicalToScreen(SKPoint logical) =>
        new(Location.X + logical.X, Location.Y + logical.Y);

    /// <summary>Shows the window, owned by <see cref="Owner"/> when it is set.</summary>
    /// <remarks>
    /// Legal before <see cref="XPlatApplication.Run"/>: the window is registered and becomes visible
    /// once the loop starts, so <c>app.MainWindow = new XPlatWindow(...)</c> works inside an object
    /// initialiser.
    /// </remarks>
    /// <exception cref="ObjectDisposedException">The window has been closed.</exception>
    public void Show() => ShowCore();

    /// <summary>Sets <see cref="Owner"/> and shows the window.</summary>
    /// <param name="owner">The window that owns this one.</param>
    public void Show(XPlatWindow owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        Owner = owner;
        ShowCore();
    }

    /// <summary>
    /// Shows the window modally and returns once it has closed. Disables the owner and re-enters the
    /// platform loop for as long as the modal session lasts.
    /// </summary>
    /// <returns><see cref="DialogResult"/>, which defaults to <see cref="XPlatDialogResult.Cancel"/>.</returns>
    /// <exception cref="ObjectDisposedException">The window has been closed.</exception>
    public XPlatDialogResult ShowDialog() => ShowDialogCore(_owner ?? _application.MainWindow);

    /// <summary>Shows the window modally, owned by <paramref name="owner"/>.</summary>
    /// <param name="owner">The window to disable while this one is up, or null for none.</param>
    /// <returns><see cref="DialogResult"/>, which defaults to <see cref="XPlatDialogResult.Cancel"/>.</returns>
    public XPlatDialogResult ShowDialog(XPlatWindow? owner) => ShowDialogCore(owner);

    /// <summary>Brings the window to the front and gives it focus.</summary>
    public void Activate() => Backend.Activate();

    /// <summary>
    /// Closes the window. <see cref="Closing"/> may cancel it; otherwise the native window is
    /// destroyed, the service scope is disposed and <see cref="Closed"/> is raised.
    /// </summary>
    public void Close() => RequestClose();

    /// <summary>Closes the window and releases its service scope. Idempotent.</summary>
    public void Dispose()
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        _shown = false;

        // Owned windows go with their owner, the way a Windows owner destroys its owned windows.
        // No platform does this for us - AppKit only orders children out, and an HWND owner keeps
        // its children alive - so the cascade belongs in the model, where it happens once.
        foreach (var owned in _application.Windows)
        {
            if (ReferenceEquals(owned._owner, this))
            {
                owned.Dispose();
            }
        }

        Backend.Shown -= OnBackendShown;
        Backend.CloseRequested -= OnBackendCloseRequested;
        Backend.Closed -= OnBackendClosed;
        Backend.Activated -= OnBackendActivated;
        Backend.Deactivated -= OnBackendDeactivated;
        Backend.ClientSizeChanged -= OnBackendClientSizeChanged;
        Backend.ScaleChanged -= OnBackendScaleChanged;
        Backend.StateChanged -= OnBackendStateChanged;

        Backend.Dispose();

        Closed?.Invoke(this, EventArgs.Empty);
        _application.UnregisterWindow(this);
        _scope.Dispose();
    }

    /// <summary>Called by <see cref="XPlatApplication.Run"/> for a window shown before the loop started.</summary>
    internal void ShowNow()
    {
        if (_shown || _closed)
        {
            return;
        }

        _shown = true;
        ApplyDefaultSize();
        Backend.MenuBar = _menuBar ?? _application.Options.MenuBar;
        Backend.Owner = _owner?.Backend;
        Backend.Show();
        RaiseShown();
    }

    /// <summary>
    /// Raises <see cref="Closing"/> and, unless it is cancelled, closes the window. Returns false
    /// when a handler cancelled the close.
    /// </summary>
    internal bool RequestClose()
    {
        if (_closed)
        {
            return true;
        }

        var args = new CancelEventArgs();
        Closing?.Invoke(this, args);
        if (args.Cancel)
        {
            return false;
        }

        Dispose();
        return true;
    }

    private void ShowCore()
    {
        ObjectDisposedException.ThrowIf(_closed, this);
        _application.RegisterWindow(this);

        if (_application.IsRunning)
        {
            ShowNow();
        }
    }

    private XPlatDialogResult ShowDialogCore(XPlatWindow? owner)
    {
        ObjectDisposedException.ThrowIf(_closed, this);

        if (owner is not null)
        {
            Owner = owner;
        }

        DialogResult = XPlatDialogResult.Cancel;
        _application.RegisterWindow(this);
        _shown = true;
        ApplyDefaultSize();
        Backend.MenuBar = _menuBar ?? _application.Options.MenuBar;
        Backend.Owner = _owner?.Backend;
        Backend.ShowDialog();
        RaiseShown();

        // A backend that never reported the close would leave the window registered forever.
        if (!_closed)
        {
            Dispose();
        }

        return DialogResult;
    }

    private void ApplyDefaultSize()
    {
        if (_clientSizeSet || _content is null)
        {
            return;
        }

        var desired = _content.Measure(new SKSize(float.PositiveInfinity, float.PositiveInfinity));
        var workingArea = (Backend.Screen ?? FindPrimaryScreen())?.WorkingArea;

        float width = desired.Width;
        float height = desired.Height;

        if (workingArea is { } area)
        {
            width = Math.Min(width, area.Width);
            height = Math.Min(height, area.Height);
        }

        if (width > 0 && height > 0)
        {
            Backend.ClientSize = new SKSize(width, height);
        }
    }

    private XPlatScreen? FindPrimaryScreen()
    {
        foreach (var screen in _application.Screens)
        {
            if (screen.IsPrimary)
            {
                return screen;
            }
        }

        return _application.Screens.Count > 0 ? _application.Screens[0] : null;
    }

    private void RaiseShown()
    {
        if (_shownRaised || _closed)
        {
            return;
        }

        _shownRaised = true;
        _application.NotifyWindowShown(this);
        Shown?.Invoke(this, EventArgs.Empty);
    }

    private void OnBackendShown(object? sender, EventArgs e) => Guard(RaiseShown);

    private void OnBackendCloseRequested(object? sender, EventArgs e) => Guard(() => RequestClose());

    private void OnBackendClosed(object? sender, EventArgs e) => Guard(Dispose);

    private void OnBackendActivated(object? sender, EventArgs e) =>
        Guard(() => Activated?.Invoke(this, EventArgs.Empty));

    private void OnBackendDeactivated(object? sender, EventArgs e) =>
        Guard(() => Deactivated?.Invoke(this, EventArgs.Empty));

    private void OnBackendClientSizeChanged(object? sender, EventArgs e) =>
        Guard(() => ClientSizeChanged?.Invoke(this, EventArgs.Empty));

    private void OnBackendScaleChanged(object? sender, EventArgs e) => Guard(() =>
    {
        Host.PerformLayout();
        Host.Invalidate();
        ScaleChanged?.Invoke(this, EventArgs.Empty);
    });

    private void OnBackendStateChanged(object? sender, EventArgs e) =>
        Guard(() => StateChanged?.Invoke(this, EventArgs.Empty));

    /// <summary>
    /// Runs work a backend raises from a platform callback, so a failure reaches
    /// <see cref="XPlatApplication.UnhandledException"/> rather than unwinding into native code.
    /// </summary>
    private static void Guard(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            XPlatApplication.HandleNativeBoundary(ex);
        }
    }
}
