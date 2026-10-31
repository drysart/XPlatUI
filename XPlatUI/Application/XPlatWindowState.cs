namespace XPlatUI;

/// <summary>
/// The display state of an <see cref="XPlatWindow"/>, matching the states every windowing
/// system exposes.
/// </summary>
public enum XPlatWindowState
{
    /// <summary>Restored: neither minimized nor maximized.</summary>
    Normal,

    /// <summary>Minimized to the taskbar, the Dock or the window list.</summary>
    Minimized,

    /// <summary>Maximized to fill the screen's working area.</summary>
    Maximized,

    /// <summary>Full screen, covering the working area and the system chrome.</summary>
    FullScreen,
}
