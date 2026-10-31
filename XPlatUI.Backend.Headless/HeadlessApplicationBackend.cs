using Microsoft.Extensions.DependencyInjection;
using SkiaSharp;

namespace XPlatUI.Backend.Headless;

/// <summary>
/// The platform half for a machine with no display: renders, tests and CI. It registers a scripted
/// dialog service instead of a native one, keeps no clipboard, and has no window chrome, so a
/// render is byte-for-byte reproducible regardless of how the host machine is configured.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Run"/> does not enter a loop. It drains queued work and returns the exit code, which
/// is what makes a headless run a straight line a test can step through.
/// </para>
/// <para>
/// No <see cref="IXPlatSystemTheme"/> is registered on purpose. The application model only follows
/// the desktop's appearance when a backend supplies one, so leaving it out is what keeps a render
/// independent of the host's light/dark setting.
/// </para>
/// </remarks>
public sealed class HeadlessApplicationBackend : IXPlatApplicationBackend
{
    private readonly Queue<Action> _pending = new();
    private XPlatApplication? _application;
    private int _exitCode;

    /// <summary>The size every window reports before it is given one, and the size of the synthetic screen.</summary>
    public static SKSize DefaultScreenSize { get; } = new(1920f, 1080f);

    /// <summary>The most recently created window backend, for a harness that needs to paint it.</summary>
    public HeadlessWindowBackend? LastWindow { get; private set; }

    /// <inheritdoc />
    public void Initialize(XPlatApplication application)
    {
        ArgumentNullException.ThrowIfNull(application);
        _application = application;
    }

    /// <inheritdoc />
    /// <remarks>Drains queued work once and returns; there is no loop to enter.</remarks>
    public int Run()
    {
        DrainPending();
        return _exitCode;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Always succeeds: a render or a test run has no desktop to be a second instance of, and taking
    /// a process-wide claim would make two concurrent renders of the same application collide.
    /// </remarks>
    public bool TryClaimSingleInstance() => true;

    /// <inheritdoc />
    public event EventHandler? Activated;

    /// <inheritdoc />
    public event EventHandler<XPlatSecondInstanceEventArgs>? SecondInstance;

    /// <inheritdoc />
    public void Quit(int exitCode) => _exitCode = exitCode;

    /// <inheritdoc />
    public void Invoke(Action action, bool synchronous)
    {
        ArgumentNullException.ThrowIfNull(action);

        if (synchronous)
        {
            action();
            return;
        }

        _pending.Enqueue(action);
    }

    /// <inheritdoc />
    public IXPlatWindowBackend CreateWindow(XPlatWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);

        LastWindow = new HeadlessWindowBackend(window, GetScreens()[0]);
        return LastWindow;
    }

    /// <inheritdoc />
    public void RegisterServices(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<HeadlessDialogService>();
        services.AddSingleton<IXPlatDialogService>(provider => provider.GetRequiredService<HeadlessDialogService>());
    }

    /// <inheritdoc />
    public IReadOnlyList<XPlatScreen> GetScreens() =>
        [new XPlatScreen(new SKRect(0f, 0f, DefaultScreenSize.Width, DefaultScreenSize.Height),
            new SKRect(0f, 0f, DefaultScreenSize.Width, DefaultScreenSize.Height), 1f, isPrimary: true)];

    /// <inheritdoc />
    public void Shutdown() => _pending.Clear();

    /// <summary>Runs the work queued by <see cref="Invoke"/> with <c>synchronous: false</c>.</summary>
    public void DrainPending()
    {
        while (_pending.TryDequeue(out var action))
        {
            action();
        }
    }

    /// <summary>
    /// Raises <see cref="Activated"/> the way a desktop does, so a test can drive the re-activation
    /// path without a display.
    /// </summary>
    public void RaiseActivated() => Activated?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// Raises <see cref="SecondInstance"/> the way a desktop does, so a test can drive the
    /// second-launch path without a display.
    /// </summary>
    /// <param name="arguments">The arguments the forwarded launch would have carried.</param>
    public void RaiseSecondInstance(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        SecondInstance?.Invoke(this, new XPlatSecondInstanceEventArgs(arguments));
    }

    /// <summary>The application this backend drives, or null before <see cref="Initialize"/>.</summary>
    public XPlatApplication? Application => _application;
}
