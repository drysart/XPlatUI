namespace XPlatUI;

/// <summary>Carries the exit code of an <see cref="XPlatApplication"/> that is shutting down.</summary>
public sealed class XPlatExitEventArgs : EventArgs
{
    /// <summary>Creates the arguments for an exit with <paramref name="exitCode"/>.</summary>
    public XPlatExitEventArgs(int exitCode) => ExitCode = exitCode;

    /// <summary>
    /// The process exit code. A handler may replace it, which is how a last-minute failure turns
    /// into a non-zero exit code.
    /// </summary>
    public int ExitCode { get; set; }
}
