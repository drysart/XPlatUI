using SkiaSharp;

namespace XPlatUI;

/// <summary>
/// One display, in logical pixels. Backends supply these through
/// <see cref="IXPlatApplicationBackend.GetScreens"/>, so default window sizing, placement and
/// "centre on the owner's screen" are platform-independent.
/// </summary>
public sealed class XPlatScreen
{
    /// <summary>Creates a description of one display.</summary>
    /// <param name="bounds">The whole display, in logical pixels.</param>
    /// <param name="workingArea"><paramref name="bounds"/> minus the taskbar, Dock or menu bar.</param>
    /// <param name="scaleFactor">Logical-to-physical pixel ratio, such as 2 for a Retina display.</param>
    /// <param name="isPrimary">True for the display the OS treats as primary.</param>
    public XPlatScreen(SKRect bounds, SKRect workingArea, float scaleFactor, bool isPrimary)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(scaleFactor);

        Bounds = bounds;
        WorkingArea = workingArea;
        ScaleFactor = scaleFactor;
        IsPrimary = isPrimary;
    }

    /// <summary>The whole display, in logical pixels.</summary>
    public SKRect Bounds { get; }

    /// <summary>The usable part of <see cref="Bounds"/>: no taskbar, Dock or menu bar.</summary>
    public SKRect WorkingArea { get; }

    /// <summary>Logical-to-physical pixel ratio for this display.</summary>
    public float ScaleFactor { get; }

    /// <summary>True for the display the OS treats as primary.</summary>
    public bool IsPrimary { get; }
}
