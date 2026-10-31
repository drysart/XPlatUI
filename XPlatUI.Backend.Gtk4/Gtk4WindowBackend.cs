using SkiaSharp;

namespace XPlatUI.Backend.Gtk4;

/// <summary>
/// A GTK4 window hosting a SkiaSharp-drawn control tree.
/// </summary>
/// <remarks>
/// <para>
/// The native window is created lazily, on the first <see cref="Show"/> or
/// <see cref="ShowDialog"/>. That matters because the model allows windows to be configured - and
/// even created - before the application runs, and GTK cannot build a window before the display is
/// open.
/// </para>
/// <para>
/// GTK4 has no way to place a window on screen: <c>gtk_window_set_position</c> was removed in GTK4
/// because it fights the compositor, so <see cref="Location"/> is recorded but only honoured where
/// the platform allows it (it is not). <see cref="TopMost"/> is likewise a window-manager decision
/// on Wayland and is only stored. Both are kept so the model's API is uniform across platforms.
/// </para>
/// </remarks>
public sealed class Gtk4WindowBackend : IXPlatWindowBackend
{
    private readonly Gtk4ApplicationBackend _application;
    private readonly XPlatUiHost _host = new();

    private Gtk.ApplicationWindow? _window;
    private Gtk.Box? _container;
    private Gtk4SkiaView? _view;
    private Gtk4ContextMenuPresenter? _presenter;
    private Gtk4MenuBar? _menuBarWidget;
    private GLib.MainLoop? _modalLoop;
    private IXPlatWindowBackend? _owner;

    private string _title = string.Empty;
    private SKSize _clientSize = new(800f, 600f);
    private SKSize _minimumClientSize;
    private SKSize _maximumClientSize;
    private SKPoint? _location;
    private bool _resizable = true;
    private bool _topMost;
    private XPlatWindowState _state = XPlatWindowState.Normal;
    private XPlatWindowState _reportedState = XPlatWindowState.Normal;
    private XPlatMenuBar? _menuBar;
    private bool? _darkChrome;
    private XPlatIcon? _icon;

    private float _scale = 1f;
    private bool _disposed;
    private bool _presentPending;
    private bool _menuBarAttached;
    private bool _active;

    internal Gtk4WindowBackend(Gtk4ApplicationBackend application, XPlatWindow window)
    {
        ArgumentNullException.ThrowIfNull(application);
        ArgumentNullException.ThrowIfNull(window);

        _application = application;
    }

    /// <inheritdoc />
    public XPlatUiHost Host => _host;

    /// <inheritdoc />
    public float ScaleFactor => _scale;

    /// <inheritdoc />
    public XPlatScreen? Screen => _window is null
        ? _application.PrimaryScreen
        : Gtk4ApplicationBackend.ScreenForWindow(_window) ?? _application.PrimaryScreen;

    /// <summary>The native window, or null before it is created. Used to parent native dialogs.</summary>
    public Gtk.Window? Window => _window;

    /// <summary>The presenter that shows context menus for this window.</summary>
    internal Gtk4ContextMenuPresenter? Presenter => _presenter;

    /// <inheritdoc />
    public string Title
    {
        get => _title;
        set
        {
            _title = value ?? string.Empty;

            if (_window is not null)
            {
                _window.Title = _title;
            }
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// GTK4 takes an icon by name from the desktop's theme and has no binding that accepts a bitmap
    /// for a window, so only <see cref="XPlatIcon.Name"/> reaches the title bar; a pixel-only icon is
    /// stored and ignored. X11 window managers honour the name, and a Wayland compositor takes the
    /// icon from the application's desktop entry instead.
    /// </remarks>
    public XPlatIcon? Icon
    {
        get => _icon;
        set
        {
            _icon = value;
            ApplyIcon();
        }
    }

    /// <inheritdoc />
    public SKSize ClientSize
    {
        get => _clientSize;
        set
        {
            if (value.Width <= 0f || value.Height <= 0f || _clientSize == value)
            {
                return;
            }

            _clientSize = value;

            // GTK sizes windows in logical pixels, which is the same space the library uses, so no
            // scale conversion is involved.
            _window?.SetDefaultSize(ToPixels(value.Width), ToPixels(value.Height));
        }
    }

    /// <inheritdoc />
    public SKSize MinimumClientSize
    {
        get => _minimumClientSize;
        set
        {
            _minimumClientSize = value;
            ApplySizeLimits();
        }
    }

    /// <inheritdoc />
    public SKSize MaximumClientSize
    {
        get => _maximumClientSize;
        set
        {
            // GTK has no maximum window size: the compositor, not the application, decides how big
            // a window may get. The value is recorded so the model round-trips it.
            _maximumClientSize = value;
        }
    }

    /// <inheritdoc />
    public SKPoint Location
    {
        get
        {
            if (_window is not null && _window.GetBounds(out var x, out var y, out _, out _))
            {
                return new SKPoint(x, y);
            }

            return _location ?? SKPoint.Empty;
        }
        set => _location = value;
    }

    /// <inheritdoc />
    public bool Resizable
    {
        get => _resizable;
        set
        {
            if (_resizable == value)
            {
                return;
            }

            _resizable = value;

            if (_window is not null)
            {
                _window.Resizable = value;
            }
        }
    }

    /// <inheritdoc />
    public bool TopMost
    {
        get => _topMost;
        set => _topMost = value;
    }

    /// <summary>
    /// The window's display state. GTK reports full screen and maximize separately, and both are
    /// honoured; minimize is polled because GTK does not notify on it.
    /// </summary>
    public XPlatWindowState State
    {
        get => _window is null ? _state : CurrentState();
        set
        {
            _state = value;

            if (_window is null)
            {
                return;
            }

            switch (value)
            {
                case XPlatWindowState.Minimized:
                    _window.Minimize();
                    break;

                case XPlatWindowState.Maximized:
                    _window.Unminimize();
                    _window.Maximize();
                    break;

                case XPlatWindowState.FullScreen:
                    _window.Unminimize();
                    _window.Fullscreen();
                    break;

                default:
                    _window.Unminimize();
                    if (_window.IsFullscreen())
                    {
                        _window.Unfullscreen();
                    }

                    if (_window.IsMaximized())
                    {
                        _window.Unmaximize();
                    }

                    break;
            }

            RaiseStateChangedIfNeeded();
        }
    }

    /// <inheritdoc />
    public XPlatMenuBar? MenuBar
    {
        get => _menuBar;
        set
        {
            if (ReferenceEquals(_menuBar, value))
            {
                return;
            }

            _menuBar = value;
            ApplyMenuBar();
        }
    }

    /// <inheritdoc />
    public void Show(IXPlatWindowBackend? owner)
    {
        // GTK refuses a window created before GApplication::startup, and the model shows windows
        // before it enters the loop. Creation is therefore deferred to the activate signal, which
        // is GTK's equivalent of AppKit's finishLaunching.
        _presentPending = true;
        _owner = owner;

        if (!_application.IsLoopRunning)
        {
            return;
        }

        PresentPending();
    }

    /// <inheritdoc />
    public void ShowDialog(IXPlatWindowBackend? owner)
    {
        _owner = owner;

        if (!EnsureCreated())
        {
            return;
        }

        _window!.Modal = true;
        Present();

        // A nested main loop is GTK's version of a modal session: the parent window stops accepting
        // input, the rest of the application keeps painting, and the call returns when the dialog is
        // closed by Dispose.
        var loop = GLib.MainLoop.New(GLib.MainContext.Default(), false);
        _modalLoop = loop;
        _application.EnterModal(loop);

        try
        {
            loop.Run();
        }
        finally
        {
            _modalLoop = null;
            _application.ExitModal(loop);
        }
    }

    /// <inheritdoc />
    public void Activate()
    {
        if (_window is null)
        {
            return;
        }

        _window.Present();
        _view?.Focus();
    }

    /// <summary>
    /// Asks the model to close the window. GTK's own close button is vetoed for the same reason:
    /// the model has to raise its cancellable closing event before anything is destroyed.
    /// </summary>
    public void Close()
    {
        if (!_disposed)
        {
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <inheritdoc />
    public void SetTitleBarAppearance(bool dark)
    {
        _darkChrome = dark;
        ApplyChrome();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        var window = _window;
        var view = _view;
        var menuBar = _menuBarWidget;
        var presenter = _presenter;
        var loop = _modalLoop;

        _window = null;
        _container = null;
        _view = null;
        _presenter = null;
        _menuBarWidget = null;
        _modalLoop = null;

        // End the nested loop first: everything below would otherwise run while it is still the
        // active loop.
        if (loop is not null)
        {
            _application.ExitModal(loop);

            if (loop.IsRunning())
            {
                loop.Quit();
            }
        }

        presenter?.Dismiss();

        if (view is not null)
        {
            view.MetricsChanged -= OnViewMetricsChanged;
            view.Dispose();
        }

        menuBar?.Dispose();

        if (window is not null)
        {
            window.OnNotify -= OnWindowNotify;
            window.Destroy();
        }

        _application.RemoveWindow(this);
        Closed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Called by the application backend once the main loop exists.</summary>
    internal void PresentPending()
    {
        if (!_presentPending || _disposed)
        {
            return;
        }

        if (!EnsureCreated())
        {
            return;
        }

        _presentPending = false;
        Present();
    }

    /// <summary>Called by the application backend's poll timer; only reports under XPLATUI_DEBUG.</summary>
    internal void ReportStatistics()
    {
        _view?.ReportStatistics();
        RaiseStateChangedIfNeeded();
    }

    internal void RaiseActivatedIfNeeded(bool active)
    {
        if (_active == active)
        {
            return;
        }

        _active = active;

        if (active)
        {
            // Becoming active is how a minimized window comes back: GTK4 does not tell us that the
            // window manager restored it, but it always restores focus with it.
            if (_state == XPlatWindowState.Minimized)
            {
                _state = XPlatWindowState.Normal;
                RaiseStateChangedIfNeeded();
            }

            _application.NotifyWindowActivated(this);
            Activated?.Invoke(this, EventArgs.Empty);
            return;
        }

        _application.NotifyWindowDeactivated(this);
        Deactivated?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc />
    public event EventHandler? Shown;

    /// <inheritdoc />
    public event EventHandler? CloseRequested;

    /// <inheritdoc />
    public event EventHandler? Closed;

    /// <inheritdoc />
    public event EventHandler? Activated;

    /// <inheritdoc />
    public event EventHandler? Deactivated;

    /// <inheritdoc />
    public event EventHandler? ClientSizeChanged;

    /// <inheritdoc />
    public event EventHandler? ScaleChanged;

    /// <inheritdoc />
    public event EventHandler? StateChanged;

    private bool EnsureCreated()
    {
        if (_window is not null)
        {
            return true;
        }

        if (_disposed || _application.NativeApplication is not { } native)
        {
            return false;
        }

        var window = Gtk.ApplicationWindow.New(native);
        window.Title = _title;
        window.Resizable = _resizable;
        window.SetDefaultSize(ToPixels(_clientSize.Width), ToPixels(_clientSize.Height));

        var view = new Gtk4SkiaView(_host);

        // The drawing area lives in a vertical box so a per-window menu bar can be packed above it.
        var container = Gtk.Box.New(Gtk.Orientation.Vertical, 0);
        container.Append(view.Widget);
        window.SetChild(container);

        // Returning true keeps GTK from destroying the window behind the model's back.
        window.OnCloseRequest += (_, _) =>
        {
            Close();
            return true;
        };

        window.OnNotify += OnWindowNotify;

        view.MetricsChanged += OnViewMetricsChanged;

        _window = window;
        _container = container;
        _view = view;
        _presenter = new Gtk4ContextMenuPresenter(view.Widget);

        _scale = view.ScaleFactor;
        _reportedState = CurrentState();

        ApplySizeLimits();
        ApplyMenuBar();
        ApplyChrome();
        ApplyIcon();

        return true;
    }

    private void Present()
    {
        if (_window is null)
        {
            return;
        }

        ApplyChrome();
        SetTransient(_owner);
        _window.Present();

        // A state requested before the window existed still has to be honoured, and a window can
        // only be minimized or maximized once it has been mapped.
        if (_state != XPlatWindowState.Normal)
        {
            State = _state;
        }

        _view?.Focus();
        Shown?.Invoke(this, EventArgs.Empty);
    }

    private void SetTransient(IXPlatWindowBackend? owner)
    {
        if (_window is null || owner is not Gtk4WindowBackend { Window: { } parent })
        {
            return;
        }

        _window.SetTransientFor(parent);
    }

    private void ApplyChrome()
    {
        if (_darkChrome is not { } dark)
        {
            return;
        }

        // GTK keeps a single appearance setting for the whole process, so every window asks for the
        // same theme. They always agree, because the palette itself is global.
        if (_application.SystemTheme is Gtk4SystemTheme theme)
        {
            theme.ApplyToGtk(dark);
        }
    }

    /// <summary>Hands the icon's theme name to the window manager, which is all GTK4 accepts.</summary>
    private void ApplyIcon()
    {
        if (_window is not null && _icon?.Name is { Length: > 0 } name)
        {
            _window.SetIconName(name);
        }
    }

    private void ApplySizeLimits()
    {
        // GTK's size request is the minimum size a widget will accept.
        _window?.SetSizeRequest(ToPixels(_minimumClientSize.Width), ToPixels(_minimumClientSize.Height));
    }

    private void ApplyMenuBar()
    {
        if (_container is null)
        {
            return;
        }

        if (_menuBar is null || _menuBar.Menus.Count == 0)
        {
            if (_menuBarAttached && _menuBarWidget is not null)
            {
                _container.Remove(_menuBarWidget.Widget);
                _menuBarAttached = false;
            }

            _menuBarWidget?.Dispose();
            _menuBarWidget = null;
            return;
        }

        _menuBarWidget ??= new Gtk4MenuBar();
        _menuBarWidget.SetMenus(_menuBar);

        if (!_menuBarAttached)
        {
            _container.Prepend(_menuBarWidget.Widget);
            _menuBarAttached = true;
        }
    }

    private void OnViewMetricsChanged(object? sender, EventArgs e)
    {
        if (_view is null || _disposed)
        {
            return;
        }

        var width = _view.Width;
        var height = _view.Height;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        var sizeChanged = Math.Abs(width - _clientSize.Width) > 0.5f || Math.Abs(height - _clientSize.Height) > 0.5f;
        _clientSize = new SKSize(width, height);

        var scale = _view.ScaleFactor;
        var scaleChanged = Math.Abs(scale - _scale) > 0.001f;
        _scale = scale;

        if (sizeChanged)
        {
            ClientSizeChanged?.Invoke(this, EventArgs.Empty);
        }

        if (scaleChanged)
        {
            ScaleChanged?.Invoke(this, EventArgs.Empty);
        }

        RaiseStateChangedIfNeeded();
    }

    private void OnWindowNotify(GObject.Object sender, GObject.Object.NotifySignalArgs args)
    {
        switch (args.Pspec?.GetName())
        {
            case "is-active":
                RaiseActivatedIfNeeded(_window?.GetIsActive() ?? false);
                break;

            case "maximized" or "fullscreened":
                RaiseStateChangedIfNeeded();
                break;
        }
    }

    private XPlatWindowState CurrentState()
    {
        if (_window is null)
        {
            return _state;
        }

        if (_window.IsFullscreen())
        {
            return XPlatWindowState.FullScreen;
        }

        if (_window.IsMaximized())
        {
            return XPlatWindowState.Maximized;
        }

        return _state == XPlatWindowState.Minimized ? XPlatWindowState.Minimized : XPlatWindowState.Normal;
    }

    private void RaiseStateChangedIfNeeded()
    {
        var state = CurrentState();
        if (state == _reportedState)
        {
            return;
        }

        _reportedState = state;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private static int ToPixels(float value) => (int)MathF.Round(MathF.Max(value, 0f));
}
