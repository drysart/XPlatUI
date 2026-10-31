namespace XPlatUI;

/// <summary>What ends an <see cref="XPlatApplication"/>'s message loop.</summary>
public enum XPlatShutdownMode
{
    /// <summary>The loop ends when the last entry in <see cref="XPlatApplication.Windows"/> closes.</summary>
    OnLastWindowClose,

    /// <summary>The loop ends when <see cref="XPlatApplication.MainWindow"/> closes.</summary>
    OnMainWindowClose,

    /// <summary>
    /// The loop ends only when <see cref="XPlatApplication.Exit()"/> is called. A backend keeps the
    /// platform application alive with no windows open: a message-only window on Windows,
    /// <c>GApplication.Hold()</c> on GTK4, nothing at all on macOS.
    /// </summary>
    OnExplicitShutdown,
}
