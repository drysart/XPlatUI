namespace XPlatUI;

/// <summary>How the process responds to a display whose scale factor is not 100%.</summary>
public enum XPlatDpiAwareness
{
    /// <summary>
    /// The process never learns the scale factor. The system stretches the window bitmap, so text
    /// and lines are blurry on a scaled display. Only useful for pixel-exact tooling.
    /// </summary>
    Unaware,

    /// <summary>
    /// One scale factor for the whole process, taken from the primary display at start-up. Moving a
    /// window to a display with a different scale factor leaves it blurry.
    /// </summary>
    System,

    /// <summary>
    /// Every window tracks the scale factor of the display it is on. This is what
    /// <see cref="XPlatWindow.ScaleFactor"/> and <see cref="XPlatWindow.ScaleChanged"/> assume, and
    /// it is the default.
    /// </summary>
    PerMonitor,
}
