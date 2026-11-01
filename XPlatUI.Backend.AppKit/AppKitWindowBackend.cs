using AppKit;
using CoreGraphics;
using Foundation;
using SkiaSharp;

namespace XPlatUI.Backend.AppKit;

/// <summary>
/// The AppKit half of <see cref="XPlatWindow"/>: one <c>NSWindow</c> whose content view is an
/// <see cref="AppKitSkiaView"/>, plus the <see cref="XPlatUiHost"/> that paints into it.
/// </summary>
/// <remarks>
/// <para>
/// The native window is created lazily, on the first <see cref="Show"/> or
/// <see cref="ShowDialog"/>, so every property can be set before anything exists and the initial
/// frame, style and limits are applied once instead of being patched afterwards.
/// </para>
/// <para>
/// <see cref="Close"/> only reports the request: AppKit's close button is routed through
/// <c>WindowShouldClose</c> for the same reason, so the model always gets to raise its cancellable
/// closing event before the window is destroyed.
/// </para>
/// </remarks>
public sealed class AppKitWindowBackend : IXPlatWindowBackend
{
    private readonly AppKitApplicationBackend _application;
    private readonly XPlatUiHost _host = new();
    private readonly WindowDelegate _delegate;

    private NSWindow? _window;
    private AppKitSkiaView? _view;
    private AppKitContextMenuPresenter? _presenter;

    private string _title = string.Empty;
    private SKSize _clientSize = new(800f, 600f);
    private SKSize _minimumClientSize;
    private SKSize _maximumClientSize;
    private SKPoint? _location;
    private bool _resizable = true;
    private bool _topMost;
    private IXPlatWindowBackend? _owner;
    private NSWindow? _attachedOwner;
    private XPlatWindowState _state = XPlatWindowState.Normal;
    private XPlatWindowState _reportedState = XPlatWindowState.Normal;
    private XPlatMenuBar? _menuBar;
    private bool? _darkChrome;
    private XPlatIcon? _icon;

    private float _scale = 1f;
    private bool _disposed;
    private bool _modal;

    internal AppKitWindowBackend(AppKitApplicationBackend application, XPlatWindow window)
    {
        ArgumentNullException.ThrowIfNull(application);
        ArgumentNullException.ThrowIfNull(window);

        _application = application;

        // The delegate must stay rooted for as long as the window points at it, and AppKit holds
        // only an unretained reference.
        _delegate = new WindowDelegate(this);
    }

    /// <inheritdoc />
    public XPlatUiHost Host => _host;

    /// <inheritdoc />
    public float ScaleFactor => _scale;

    /// <inheritdoc />
    public XPlatScreen? Screen => _window is null
        ? _application.PrimaryScreen
        : AppKitApplicationBackend.ScreenForWindow(_window) ?? _application.PrimaryScreen;

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
    /// macOS has no per-window icon: a window's icon is the application's, shown in the Dock and the
    /// app switcher. Setting this assigns <c>NSApplication.ApplicationIconImage</c>, so it changes
    /// the whole process rather than this window alone.
    /// </remarks>
    public XPlatIcon? Icon
    {
        get => _icon;
        set
        {
            _icon = value;

            if (AppKitIcons.Create(_icon) is { } image)
            {
                NSApplication.SharedApplication.ApplicationIconImage = image;
            }
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

            // AppKit sizes windows in points, which is the same logical space the library uses,
            // so no scale conversion is involved here at all.
            _window?.SetContentSize(new CGSize(value.Width, value.Height));
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
            _maximumClientSize = value;
            ApplySizeLimits();
        }
    }

    /// <inheritdoc />
    public SKPoint Location
    {
        get => _window is not null ? AppKitApplicationBackend.LocationOf(_window) : _location ?? SKPoint.Empty;
        set
        {
            _location = value;

            if (_window is not null)
            {
                _window.SetFrameOrigin(OriginFor(_window, value));
            }
        }
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
                var style = _window.StyleMask;
                _window.StyleMask = value ? style | NSWindowStyle.Resizable : style & ~NSWindowStyle.Resizable;
            }
        }
    }

    /// <inheritdoc />
    public bool TopMost
    {
        get => _topMost;
        set
        {
            if (_topMost == value)
            {
                return;
            }

            _topMost = value;

            if (_window is not null)
            {
                _window.Level = value ? NSWindowLevel.Floating : NSWindowLevel.Normal;
            }
        }
    }

    /// <inheritdoc />
    public IXPlatWindowBackend? Owner
    {
        get => _owner;
        set
        {
            if (ReferenceEquals(_owner, value))
            {
                return;
            }

            _owner = value;
            AttachOwner();
        }
    }

    /// <summary>
    /// The window's display state. <see cref="XPlatWindowState.FullScreen"/> maps to a zoomed
    /// window for now; true borderless full screen needs a style save/restore that is not worth
    /// carrying until a control asks for it.
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
                    _window.Miniaturize(null!);
                    break;

                case XPlatWindowState.Maximized:
                case XPlatWindowState.FullScreen:
                    if (_window.IsMiniaturized)
                    {
                        _window.Deminiaturize(null!);
                    }

                    if (!_window.IsZoomed)
                    {
                        _window.Zoom(null!);
                    }

                    break;

                default:
                    if (_window.IsMiniaturized)
                    {
                        _window.Deminiaturize(null!);
                    }

                    if (_window.IsZoomed)
                    {
                        _window.Zoom(null!);
                    }

                    break;
            }
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
            _application.RebuildMenuBar();
        }
    }

    /// <summary>The presenter that shows this window's context menus.</summary>
    internal AppKitContextMenuPresenter? Presenter => _presenter;

    /// <summary>The native window, or null before it has been shown.</summary>
    internal NSWindow? Window => _window;

    /// <inheritdoc />
    public void Show()
    {
        if (!EnsureCreated())
        {
            return;
        }

        Present();
    }

    /// <inheritdoc />
    public void ShowDialog()
    {
        if (!EnsureCreated())
        {
            return;
        }

        Present();

        _modal = true;
        _application.EnterModal();

        try
        {
            // A nested run loop, which is the macOS spelling of a modal window. It returns as soon
            // as this window stops the session, which Dispose does when the model closes it.
            NSApplication.SharedApplication.RunModalForWindow(_window!);
        }
        finally
        {
            _application.ExitModal();
            _modal = false;
        }
    }

    /// <inheritdoc />
    public void Activate()
    {
        if (_window is null)
        {
            return;
        }

        _window.MakeKeyAndOrderFront(null!);
        NSApplication.SharedApplication.Activate();
    }

    /// <inheritdoc />
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

        DetachOwner();

        _window = null;
        _view = null;
        _presenter = null;

        if (window is not null)
        {
            // Ending the session has to come first: RunModalForWindow owns the loop until it does.
            if (_modal)
            {
                _modal = false;
                NSApplication.SharedApplication.StopModal();
            }

            window.Delegate = null!;

            // Detach the view before its drawing buffers go away, so AppKit cannot ask it to draw
            // a frame it no longer has anything to draw with.
            window.ContentView = null!;
            window.OrderOut(null!);
            window.Close();
        }

        view?.Dispose();

        _application.RemoveWindow(this);
        Closed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Prints frame rate and memory use every few seconds when tracing is on.</summary>
    internal void ReportStatistics() => _view?.ReportStatistics();

    private bool EnsureCreated()
    {
        if (_window is not null)
        {
            return true;
        }

        if (_disposed)
        {
            return false;
        }

        var style = NSWindowStyle.Titled | NSWindowStyle.Closable | NSWindowStyle.Miniaturizable;

        if (_resizable)
        {
            style |= NSWindowStyle.Resizable;
        }

        var window = new NSWindow(
            new CGRect(0, 0, _clientSize.Width, _clientSize.Height),
            style,
            NSBackingStore.Buffered,
            deferCreation: false)
        {
            Title = _title,
        };

        // The managed side owns the window, so AppKit must not release it when it closes. The
        // property setter is obsolete; the method is the supported spelling of the same thing.
        window.ReleaseWhenClosed(false);
        window.Delegate = _delegate;

        var view = new AppKitSkiaView(_host)
        {
            AutoresizingMask = NSViewResizingMask.WidthSizable | NSViewResizingMask.HeightSizable,
        };

        window.ContentView = view;

        _window = window;
        _view = view;
        _scale = (float)window.BackingScaleFactor;
        _reportedState = CurrentState();

        // A context menu is anchored to the view that shows it, so it can only be built now.
        _presenter = new AppKitContextMenuPresenter(view);

        ApplySizeLimits();

        if (_topMost)
        {
            window.Level = NSWindowLevel.Floating;
        }

        // The child-window relationship is what makes an owned window stay above its owner and go
        // with it when the owner is ordered out; AppKit has nothing else for it.
        AttachOwner();

        Place(window);

        // A window that has never been focused is not the active window yet, but it is the one the
        // menu bar should describe as soon as it is.
        _application.NotifyWindowActivated(this);

        return true;
    }

    private void Present()
    {
        var window = _window!;

        ApplyChrome();

        window.MakeKeyAndOrderFront(null!);
        window.MakeFirstResponder(_view);

        // -[NSApplication activate] does nothing unless the application is already frontmost, so a
        // window shown by a background process would never become active. The running-application
        // proxy is the API that still brings one forward.
        if (!NSApplication.SharedApplication.Active)
        {
            NSRunningApplication.CurrentApplication?.Activate(NSApplicationActivationOptions.ActivateIgnoringOtherWindows);
        }

        Shown?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Makes this window a child of its owner, or detaches it when the owner changed. A child window
    /// is ordered above its parent and is ordered out with it, which is the whole of AppKit's
    /// ownership model; closing is the model's job.
    /// </summary>
    private void AttachOwner()
    {
        if (_window is null)
        {
            return;
        }

        var parent = (_owner as AppKitWindowBackend)?.Window;

        if (ReferenceEquals(parent, _attachedOwner))
        {
            return;
        }

        _attachedOwner?.RemoveChildWindow(_window);
        _attachedOwner = parent;
        parent?.AddChildWindow(_window, NSWindowOrderingMode.Above);
    }

    /// <summary>Breaks the child-window relationship, if there is one.</summary>
    private void DetachOwner()
    {
        if (_attachedOwner is { } parent && _window is { } window)
        {
            parent.RemoveChildWindow(window);
        }

        _attachedOwner = null;
    }

    /// <summary>Makes the title bar and other AppKit chrome match the palette the library draws with.</summary>
    private void ApplyChrome()
    {
        if (_window is null || _darkChrome is not { } dark)
        {
            return;
        }

        // When the two agree the window simply inherits the system appearance, which keeps the
        // vibrant and high contrast variants AppKit would otherwise lose.
        //
        // The factory has to be appearanceNamed: rather than initWithAppearanceNamed:bundle: - the
        // latter looks for an appearance resource inside a bundle, so a system appearance name
        // makes it return nil and the binding throws.
        _window.Appearance = dark == _application.SystemTheme.IsDarkMode
            ? null
            : NSAppearance.GetAppearance(dark ? NSAppearance.NameDarkAqua : NSAppearance.NameAqua);
    }

    private void ApplySizeLimits()
    {
        if (_window is null)
        {
            return;
        }

        _window.ContentMinSize = new CGSize(_minimumClientSize.Width, _minimumClientSize.Height);
        _window.ContentMaxSize = _maximumClientSize.Width > 0f && _maximumClientSize.Height > 0f
            ? new CGSize(_maximumClientSize.Width, _maximumClientSize.Height)
            : new CGSize(double.MaxValue, double.MaxValue);
    }

    /// <summary>Centres the window on the main display, or puts it where the model asked.</summary>
    private void Place(NSWindow window)
    {
        if (_location is { } location)
        {
            window.SetFrameOrigin(OriginFor(window, location));
            return;
        }

        window.Center();
    }

    private static CGPoint OriginFor(NSWindow window, SKPoint location)
    {
        var screens = NSScreen.Screens;
        var primaryHeight = screens is { Length: > 0 } ? screens[0].Frame.Height : window.Frame.Height;

        return new CGPoint(
            location.X,
            primaryHeight - location.Y - window.Frame.Height);
    }

    private XPlatWindowState CurrentState()
    {
        if (_window is null)
        {
            return _state;
        }

        if (_window.IsMiniaturized)
        {
            return XPlatWindowState.Minimized;
        }

        return _window.IsZoomed ? XPlatWindowState.Maximized : XPlatWindowState.Normal;
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

    /// <summary>
    /// The window delegate. AppKit's decisions are all reported back to the backend, which decides
    /// whether to forward them to the model, so no lifecycle policy lives here.
    /// </summary>
    private sealed class WindowDelegate : NSWindowDelegate
    {
        private readonly AppKitWindowBackend _backend;

        public WindowDelegate(AppKitWindowBackend backend) => _backend = backend;

        /// <summary>
        /// The close button and Cmd+W. Answering false keeps AppKit from destroying the window
        /// behind the model's back: the request goes to the model, which disposes it if nothing
        /// cancels the close.
        /// </summary>
        public override bool WindowShouldClose(NSObject sender)
        {
            _backend.Close();
            return false;
        }

        public override void DidBecomeKey(NSNotification notification)
        {
            _backend._application.NotifyWindowActivated(_backend);
            _backend.Activated?.Invoke(_backend, EventArgs.Empty);
        }

        public override void DidResignKey(NSNotification notification)
        {
            _backend._application.NotifyWindowDeactivated(_backend);
            _backend.Deactivated?.Invoke(_backend, EventArgs.Empty);
        }

        public override void DidResize(NSNotification notification)
        {
            var content = _backend._window?.ContentView;
            if (content is not null)
            {
                _backend._clientSize = new SKSize((float)content.Bounds.Width, (float)content.Bounds.Height);
            }

            _backend.ClientSizeChanged?.Invoke(_backend, EventArgs.Empty);
            _backend.RaiseStateChangedIfNeeded();
        }

        /// <summary>
        /// Fires when the window moves to a display with a different scale factor, and also when
        /// the display's own scale changes, which is the only signal either case gives.
        /// </summary>
        public override void DidChangeBackingProperties(NSNotification notification)
        {
            var scale = _backend._window is { } window ? (float)window.BackingScaleFactor : 1f;
            if (Math.Abs(scale - _backend._scale) < 0.001f)
            {
                return;
            }

            _backend._scale = scale;
            _backend.ScaleChanged?.Invoke(_backend, EventArgs.Empty);
        }

        public override void DidMiniaturize(NSNotification notification) => _backend.RaiseStateChangedIfNeeded();

        public override void DidDeminiaturize(NSNotification notification) => _backend.RaiseStateChangedIfNeeded();

        public override void DidEndLiveResize(NSNotification notification) => _backend.RaiseStateChangedIfNeeded();
    }

    /// <inheritdoc />
    public void RunWindowHooks(XPlatWindowPhase phase, XPlatWindow window)
    {
        // Empty on purpose: this backend declares no typed window hooks yet.
    }
}
