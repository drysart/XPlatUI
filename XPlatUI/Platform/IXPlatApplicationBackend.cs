using Microsoft.Extensions.DependencyInjection;

namespace XPlatUI;

/// <summary>
/// The platform half of an <see cref="XPlatApplication"/>: it owns the message loop, creates
/// windows and registers the platform services. One instance per process, handed to the
/// <c>XPlatApplication</c> constructor.
/// </summary>
/// <remarks>
/// <para>
/// Backends are selected with <c>#if</c>, never by reflection: reflection discovery breaks under
/// trimming and AOT, and an explicit factory is a few lines that stay visible in the call stack.
/// </para>
/// <para>
/// <b>The loop may be nested.</b> <see cref="Run"/> enters the platform loop, and modal work -
/// <see cref="IXPlatDialogService"/> methods and <see cref="IXPlatWindowBackend.ShowDialog"/> -
/// re-enters it for as long as the modal session lasts. <see cref="Invoke"/> callbacks must keep
/// dispatching to the same UI thread throughout.
/// </para>
/// </remarks>
public interface IXPlatApplicationBackend
{
    /// <summary>
    /// Binds the backend to <paramref name="application"/>: installs the platform services and
    /// reads the application's options. Runs before the first window is created.
    /// </summary>
    void Initialize(XPlatApplication application);

    /// <summary>
    /// Runs the platform message loop until the application exits and returns its exit code.
    /// Blocks the calling thread, which becomes the UI thread for the rest of the process.
    /// </summary>
    int Run();

    /// <summary>Asks the loop to end with <paramref name="exitCode"/>. Safe to call from any thread.</summary>
    void Quit(int exitCode);

    /// <summary>
    /// Runs <paramref name="action"/> on the UI thread: synchronously when
    /// <paramref name="synchronous"/> is true, otherwise queued behind the current work.
    /// </summary>
    void Invoke(Action action, bool synchronous);

    /// <summary>Creates the native window behind <paramref name="window"/>.</summary>
    IXPlatWindowBackend CreateWindow(XPlatWindow window);

    /// <summary>
    /// Registers the platform services. Called once, before the application's own
    /// <c>ConfigureServices</c> runs, so an application can override any of them.
    /// </summary>
    /// <remarks>
    /// One registration hook rather than a <c>Create*</c> member per service: a flat list of
    /// factory methods makes every new platform service a breaking change to this interface, and
    /// lazy resolution means the headless backend never constructs a clipboard it will not use.
    /// Every backend must register an <see cref="IXPlatDialogService"/>, since dialogs are part of
    /// v1.
    /// </remarks>
    void RegisterServices(IServiceCollection services);

    /// <summary>The displays attached to the machine.</summary>
    IReadOnlyList<XPlatScreen> GetScreens();

    /// <summary>Releases the loop's resources once <see cref="Run"/> has returned.</summary>
    void Shutdown();
}
