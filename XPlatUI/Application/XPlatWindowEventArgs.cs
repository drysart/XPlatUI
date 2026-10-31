namespace XPlatUI;

/// <summary>Identifies one window in an <see cref="XPlatApplication"/> event.</summary>
public sealed class XPlatWindowEventArgs : EventArgs
{
    /// <summary>Creates the arguments for <paramref name="window"/>.</summary>
    public XPlatWindowEventArgs(XPlatWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);
        Window = window;
    }

    /// <summary>The window the event is about.</summary>
    public XPlatWindow Window { get; }
}
