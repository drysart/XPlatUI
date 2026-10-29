namespace XPlatUI;

/// <summary>
/// Describes how a selectable control takes part in a selection.
/// </summary>
public enum XPlatSelectionStyle
{
    /// <summary>
    /// The control is selected as a single unit: its whole bounds light up and it contributes
    /// all of its <see cref="XPlatControl.Text"/> to the clipboard.
    /// </summary>
    Whole = 0,

    /// <summary>
    /// The control maps pointer positions to offsets inside its own content, so a selection can
    /// start or end in the middle of it - inside text, for example.
    /// </summary>
    Partial = 1,
}
