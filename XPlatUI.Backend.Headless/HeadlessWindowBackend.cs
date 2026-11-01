using SkiaSharp;

namespace XPlatUI.Backend.Headless;

/// <summary>
/// The window half for a machine with no display. It owns a real <see cref="XPlatUiHost"/> - the
/// same one the native harnesses drive - so the control tree is laid out and painted exactly as it
/// would be on screen; only the surface it paints into is different.
/// </summary>
/// <remarks>
/// Nothing here is asynchronous: <see cref="Show"/> marks the window visible and returns, and
/// <see cref="SavePng"/> paints on demand. There is no chrome, no input and no DPI change.
/// </remarks>
public sealed class HeadlessWindowBackend : IXPlatWindowBackend
{
    private readonly XPlatScreen _screen;
    private SKSize _clientSize;
    private SKSize _minimumClientSize;
    private SKSize _maximumClientSize = new(float.PositiveInfinity, float.PositiveInfinity);
    private XPlatWindowState _state;
    private bool _disposed;

    /// <summary>Creates the backend for <paramref name="window"/>, reporting <paramref name="screen"/>.</summary>
    /// <param name="window">The model window this backend serves.</param>
    /// <param name="screen">The display to report, normally the backend's synthetic one.</param>
    public HeadlessWindowBackend(XPlatWindow window, XPlatScreen screen)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(screen);

        Window = window;
        _screen = screen;
        Host = new XPlatUiHost();
    }

    /// <summary>The model window this backend serves.</summary>
    public XPlatWindow Window { get; }

    /// <inheritdoc />
    public XPlatUiHost Host { get; }

    /// <inheritdoc />
    public float ScaleFactor => 1f;

    /// <inheritdoc />
    public XPlatScreen? Screen => _screen;

    /// <inheritdoc />
    public string Title { get; set; } = string.Empty;

    /// <inheritdoc />
    /// <remarks>Stored only: a synthetic window has no chrome to draw an icon in.</remarks>
    public XPlatIcon? Icon { get; set; }

    /// <inheritdoc />
    /// <remarks>Forwards to <see cref="XPlatUiHost.SetClientSize"/>, which is what drives layout.</remarks>
    public SKSize ClientSize
    {
        get => _clientSize;
        set
        {
            if (_clientSize == value)
            {
                return;
            }

            _clientSize = value;
            Host.SetClientSize(value.Width, value.Height);
            ClientSizeChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <inheritdoc />
    public SKSize MinimumClientSize
    {
        get => _minimumClientSize;
        set => _minimumClientSize = value;
    }

    /// <inheritdoc />
    public SKSize MaximumClientSize
    {
        get => _maximumClientSize;
        set => _maximumClientSize = value;
    }

    /// <inheritdoc />
    public SKPoint Location { get; set; }

    /// <inheritdoc />
    public bool Resizable { get; set; } = true;

    /// <inheritdoc />
    public bool TopMost { get; set; }

    /// <inheritdoc />
    public XPlatWindowState State
    {
        get => _state;
        set
        {
            if (_state == value)
            {
                return;
            }

            _state = value;
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <inheritdoc />
    public IXPlatWindowBackend? Owner { get; set; }

    /// <inheritdoc />
    public XPlatMenuBar? MenuBar { get; set; }

    /// <summary>True once the window is the current one on the stack.</summary>
    public bool IsVisible { get; private set; }

    /// <summary>The appearance last passed to <see cref="SetTitleBarAppearance"/>.</summary>
    public bool TitleBarIsDark { get; private set; }

    /// <inheritdoc />
    public void Show()
    {
        IsVisible = true;
        Shown?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc />
    /// <remarks>Nothing is modal without a display: this shows the window and returns immediately.</remarks>
    public void ShowDialog() => Show();

    /// <inheritdoc />
    public void Activate()
    {
        if (!_disposed)
        {
            Activated?.Invoke(this, EventArgs.Empty);
        }
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
    public void SetTitleBarAppearance(bool dark) => TitleBarIsDark = dark;

    /// <summary>
    /// Raises <see cref="Deactivated"/>. There is no window manager to move focus away, so a test
    /// that needs the event asks for it.
    /// </summary>
    public void SimulateDeactivate()
    {
        if (!_disposed)
        {
            Deactivated?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Raises <see cref="ScaleChanged"/>, standing in for a window dragged onto a display with a
    /// different scale factor.
    /// </summary>
    public void SimulateScaleChange()
    {
        if (!_disposed)
        {
            ScaleChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Paints the control tree into <paramref name="canvas"/> at the current client size, exactly as
    /// a native backend would paint it into its framebuffer.
    /// </summary>
    /// <param name="canvas">The canvas to paint into.</param>
    public void RenderTo(SKCanvas canvas)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        Host.Paint(canvas);
    }

    /// <summary>Paints the control tree and writes it out as a PNG.</summary>
    /// <param name="path">The file to write.</param>
    public void SavePng(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var width = Math.Max(1, (int)MathF.Round(_clientSize.Width));
        var height = Math.Max(1, (int)MathF.Round(_clientSize.Height));
        var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);

        using var surface = SKSurface.Create(info);
        RenderTo(surface.Canvas);

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using var stream = File.Create(path);
        data.SaveTo(stream);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        IsVisible = false;
        Closed?.Invoke(this, EventArgs.Empty);
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
    public void RunWindowHooks(XPlatWindowPhase phase, XPlatWindow window)
    {
        // Empty on purpose: this backend declares no typed window hooks yet.
    }
}
