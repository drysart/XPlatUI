using SkiaSharp;

namespace XPlatUI;

/// <summary>
/// The blinking insertion caret shared by editable controls, so every control that takes text input
/// shows the same caret and blinks it on the same rhythm.
/// </summary>
/// <remarks>
/// <para>
/// A caret owns the timer that blinks it and wires itself to its control: it takes over when the
/// control gains focus, gives up when the control loses focus or is disabled, and is disposed with
/// the control. Building one costs a line - <c>new XPlatCaret(this)</c> - and needs no timer, no
/// focus handling and no disposal in the control itself.
/// </para>
/// <para>
/// The caret knows nothing about text. A control works out where the caret belongs and hands that
/// rectangle to <see cref="Draw(SKCanvas, SKRect)"/>, which paints nothing while the caret is in its
/// hidden blink phase, so a paint method never has to test the blink state itself.
/// </para>
/// </remarks>
public sealed class XPlatCaret : IDisposable
{
    /// <summary>The default interval between blinks, matching the platform text fields.</summary>
    public static readonly TimeSpan DefaultBlinkInterval = TimeSpan.FromMilliseconds(530);

    /// <summary>The default caret thickness, in device-independent pixels.</summary>
    public const float DefaultWidth = 1.5f;

    private readonly XPlatControl _owner;
    private readonly XPlatTimer _timer;
    private bool _active;
    private bool _visible = true;
    private bool _disposed;

    /// <summary>Creates a caret for a control and registers it to be disposed with that control.</summary>
    /// <param name="owner">
    /// The control the caret belongs to: the caret follows its focus and enabled state and repaints it
    /// when it blinks.
    /// </param>
    /// <param name="blinkInterval">How often the caret toggles. Defaults to <see cref="DefaultBlinkInterval"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="owner"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="blinkInterval"/> is not positive.</exception>
    public XPlatCaret(XPlatControl owner, TimeSpan? blinkInterval = null)
    {
        ArgumentNullException.ThrowIfNull(owner);

        _owner = owner;
        _timer = owner.CreateTimer(blinkInterval ?? DefaultBlinkInterval, OnTick);

        owner.GotFocus += OnOwnerGotFocus;
        owner.LostFocus += OnOwnerLostFocus;
        owner.EnabledChanged += OnOwnerEnabledChanged;
        owner.Own(this);
    }

    /// <summary>How often the caret toggles between its visible and hidden phases.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not positive.</exception>
    public TimeSpan BlinkInterval
    {
        get => _timer.Interval;
        set => _timer.Interval = value;
    }

    /// <summary>The caret's thickness. Defaults to <see cref="DefaultWidth"/>.</summary>
    public float Width { get; set; } = DefaultWidth;

    /// <summary>
    /// The caret's color, or null (the default) to follow <see cref="XPlatTheme.Caret"/> so the caret
    /// tracks the theme.
    /// </summary>
    public SKColor? Color { get; set; }

    /// <summary>
    /// Whether this control currently owns the caret. Editable controls activate it when they take
    /// the insertion point and deactivate it when something else does.
    /// </summary>
    public bool IsActive => _active;

    /// <summary>Whether the blink is currently in its visible phase.</summary>
    public bool IsVisible => _visible;

    /// <summary>
    /// Whether a paint should draw the caret: it is active, in its visible phase, and its control is
    /// focused and enabled.
    /// </summary>
    public bool ShouldPaint => _active && _visible && _owner.IsFocused && _owner.Enabled;

    /// <summary>
    /// Takes the caret and restarts the blink, so it appears solid the moment the user starts editing
    /// instead of vanishing under the first keystroke.
    /// </summary>
    public void Activate()
    {
        if (_disposed)
        {
            return;
        }

        _active = true;
        ResetBlink();
    }

    /// <summary>Gives up the caret and stops the blink timer until it is activated again.</summary>
    public void Deactivate()
    {
        _active = false;
        _visible = true;
        _timer.Stop();
    }

    /// <summary>
    /// Shows the caret solidly and restarts its blink. Every edit and every caret move does this, so
    /// the caret is never mid-blink when the user is looking for it.
    /// </summary>
    public void ResetBlink()
    {
        if (_disposed)
        {
            return;
        }

        _visible = true;
        _timer.Enabled = _active && _owner.IsFocused && _owner.Enabled;
    }

    /// <summary>Draws the caret over <paramref name="bounds"/> if it should currently be visible.</summary>
    /// <param name="canvas">The canvas to paint on.</param>
    /// <param name="bounds">The area the caret occupies: one <see cref="Width"/>-wide column of the text line's height.</param>
    /// <exception cref="ArgumentNullException"><paramref name="canvas"/> is null.</exception>
    public void Draw(SKCanvas canvas, SKRect bounds)
    {
        ArgumentNullException.ThrowIfNull(canvas);

        if (!ShouldPaint || bounds.Width <= 0f || bounds.Height <= 0f)
        {
            return;
        }

        XPlatPainter.Fill(canvas, bounds, Color ?? XPlatTheme.Caret);
    }

    /// <summary>
    /// Draws a caret <see cref="Width"/> wide at <paramref name="x"/>, spanning <paramref name="height"/>
    /// down from <paramref name="top"/>.
    /// </summary>
    /// <param name="canvas">The canvas to paint on.</param>
    /// <param name="x">The left edge of the caret.</param>
    /// <param name="top">The top edge of the caret.</param>
    /// <param name="height">The caret's height, normally the height of the text line it sits on.</param>
    /// <exception cref="ArgumentNullException"><paramref name="canvas"/> is null.</exception>
    public void Draw(SKCanvas canvas, float x, float top, float height) =>
        Draw(canvas, new SKRect(x, top, x + Width, top + height));

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _active = false;

        _owner.GotFocus -= OnOwnerGotFocus;
        _owner.LostFocus -= OnOwnerLostFocus;
        _owner.EnabledChanged -= OnOwnerEnabledChanged;

        _timer.Dispose();
    }

    private void OnOwnerGotFocus(object? sender, EventArgs e) => Activate();

    private void OnOwnerLostFocus(object? sender, EventArgs e)
    {
        // Losing focus does not give the caret up - the control may get focus back without the user
        // having moved anywhere - but the blink stops and the caret comes back solid.
        _visible = true;
        _timer.Stop();
    }

    private void OnOwnerEnabledChanged(object? sender, EventArgs e) =>
        _timer.Enabled = _active && _owner.IsFocused && _owner.Enabled;

    private void OnTick(object? sender, EventArgs e)
    {
        // The control tree belongs to the caller, so this timer can outlive the application. Once
        // that happens XPlatTimer ticks inline on a pool thread, which must not touch the UI.
        if (XPlatApplication.Current is null || !_active || !_owner.IsFocused || !_owner.Enabled)
        {
            _visible = true;
            _timer.Stop();
            return;
        }

        _visible = !_visible;
        _owner.Invalidate();
    }
}
