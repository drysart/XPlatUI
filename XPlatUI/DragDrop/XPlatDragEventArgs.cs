using SkiaSharp;

namespace XPlatUI;

/// <summary>
/// Carries the data being dragged and where the pointer is, for the drag events a control receives
/// while <see cref="XPlatControl.AllowDrop"/> is set.
/// </summary>
/// <remarks>
/// <see cref="Effect"/> starts out as the most useful action the source allows, and a handler
/// narrows it (or sets <see cref="XPlatDragDropEffects.None"/> to refuse the drop) before the
/// backend is told what to show in its drag cursor.
/// </remarks>
public class XPlatDragEventArgs : EventArgs
{
    /// <summary>Creates the arguments for one drag event.</summary>
    /// <param name="data">The payload being dragged.</param>
    /// <param name="location">The pointer, in the receiving control's client coordinates.</param>
    /// <param name="allowedEffects">What the drag source allows the target to do.</param>
    /// <param name="modifiers">The modifier keys held down.</param>
    public XPlatDragEventArgs(
        XPlatDataObject data,
        SKPoint location,
        XPlatDragDropEffects allowedEffects,
        XPlatModifiers modifiers = XPlatModifiers.None)
    {
        ArgumentNullException.ThrowIfNull(data);
        Data = data;
        Location = location;
        AllowedEffects = allowedEffects;
        Modifiers = modifiers;
        Effect = BestEffect(allowedEffects);
    }

    /// <summary>The payload being dragged.</summary>
    public XPlatDataObject Data { get; }

    /// <summary>The pointer, in the receiving control's client coordinates.</summary>
    public SKPoint Location { get; }

    /// <summary>The modifier keys held down.</summary>
    public XPlatModifiers Modifiers { get; }

    /// <summary>What the drag source allows the target to do.</summary>
    public XPlatDragDropEffects AllowedEffects { get; }

    /// <summary>
    /// What the target will do with the data. The backend clamps it to
    /// <see cref="AllowedEffects"/>, so setting an action the source refuses shows a "no drop"
    /// cursor.
    /// </summary>
    public XPlatDragDropEffects Effect { get; set; }

    private static XPlatDragDropEffects BestEffect(XPlatDragDropEffects allowed)
    {
        if (allowed.HasFlag(XPlatDragDropEffects.Copy))
        {
            return XPlatDragDropEffects.Copy;
        }

        if (allowed.HasFlag(XPlatDragDropEffects.Move))
        {
            return XPlatDragDropEffects.Move;
        }

        return allowed.HasFlag(XPlatDragDropEffects.Link) ? XPlatDragDropEffects.Link : XPlatDragDropEffects.None;
    }
}
