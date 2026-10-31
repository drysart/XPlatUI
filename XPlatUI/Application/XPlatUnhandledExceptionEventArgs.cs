namespace XPlatUI;

/// <summary>
/// Carries an exception that escaped application, window or control code on the UI thread, and
/// whether the application should survive it.
/// </summary>
/// <remarks>
/// Without a handler the exception is fatal: the application shuts down through the normal path -
/// windows close, <see cref="XPlatApplication.Exiting"/> runs, services are disposed - and then
/// <see cref="XPlatApplication.Run"/> rethrows it, so the failure is neither swallowed nor left to
/// unwind through a native message pump.
/// </remarks>
public sealed class XPlatUnhandledExceptionEventArgs : EventArgs
{
    /// <summary>Creates the arguments for <paramref name="exception"/>.</summary>
    /// <param name="exception">The exception that escaped UI-thread code.</param>
    public XPlatUnhandledExceptionEventArgs(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        Exception = exception;
    }

    /// <summary>The exception that escaped UI-thread code.</summary>
    public Exception Exception { get; }

    /// <summary>
    /// Set to <c>true</c> to keep the application alive. The default is <c>false</c>, which shuts
    /// the application down.
    /// </summary>
    /// <remarks>
    /// A handled exception is swallowed and the loop keeps running, which is only honest when the
    /// loop is still alive: if the failure happened inside the platform's own message loop, the loop
    /// has already ended and the application shuts down regardless - with exit code 1 unless a
    /// handler set one through <see cref="XPlatApplication.Exit(int)"/>.
    /// </remarks>
    public bool Handled { get; set; }
}
