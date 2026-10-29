using SkiaSharp;

namespace XPlatUI;

/// <summary>
/// A request to show <see cref="Menu"/>. The location is in logical pixels in the host's
/// coordinate space, which is the space the adapter receives from
/// <see cref="XPlatUiHost.ProcessContextMenu"/>; converting it to screen coordinates is the
/// presenter's job, because only the platform layer knows where the surface sits on screen.
/// </summary>
public sealed class XPlatContextMenuRequest
{
    internal XPlatContextMenuRequest(
        XPlatContextMenu menu,
        XPlatUiHost host,
        XPlatControl? control,
        SKPoint location)
    {
        Menu = menu;
        Host = host;
        Control = control;
        Location = location;
    }

    public XPlatContextMenu Menu { get; }

    public XPlatUiHost Host { get; }

    /// <summary>The control the pointer was over, or null when it was over empty surface.</summary>
    public XPlatControl? Control { get; }

    /// <summary>Where to show the menu, in logical pixels in the host's coordinate space.</summary>
    public SKPoint Location { get; }
}

/// <summary>
/// Raised by <see cref="XPlatUiHost.ContextMenuOpening"/> before a context menu is shown, so an
/// application can build a menu for a control that does not carry one. Assigning
/// <see cref="Menu"/> takes precedence over the host's default selection menu.
/// </summary>
public sealed class XPlatContextMenuEventArgs : EventArgs
{
    internal XPlatContextMenuEventArgs(XPlatControl? control, SKPoint location)
    {
        Control = control;
        Location = location;
    }

    /// <summary>The control the pointer was over, or null when it was over empty surface.</summary>
    public XPlatControl? Control { get; }

    /// <summary>Where the menu will be shown, in logical pixels in the host's coordinate space.</summary>
    public SKPoint Location { get; }

    /// <summary>The menu to show. Null falls through to the host's default selection menu.</summary>
    public XPlatContextMenu? Menu { get; set; }
}

/// <summary>
/// Implemented by the platform layer to show <see cref="XPlatContextMenu"/> values. A Windows
/// adapter uses a native menu; other platforms do the same with their own menu API, which keeps
/// the library free of any menu rendering code.
/// </summary>
public interface IXPlatContextMenuPresenter
{
    /// <summary>Shows <paramref name="request"/> and runs the picked item's command, if any.</summary>
    void Show(XPlatContextMenuRequest request);
}

/// <summary>
/// Process-wide access point for the context menu presenter. When none is installed menus are
/// silently not shown, so the library never depends on a platform being present.
/// </summary>
public static class XPlatContextMenuPresenter
{
    private static IXPlatContextMenuPresenter? _current;

    /// <summary>
    /// The presenter in use. Adapters assign a platform implementation; assigning null restores
    /// the no-op fallback.
    /// </summary>
    public static IXPlatContextMenuPresenter? Current
    {
        get => _current;
        set => _current = value;
    }
}
