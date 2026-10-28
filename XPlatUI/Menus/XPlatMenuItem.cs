namespace XPlatUI;

/// <summary>
/// A single entry in an <see cref="XPlatContextMenu"/>. Items are plain data: the platform
/// presenter decides how they look, and calls <see cref="Invoke"/> when the user picks one.
/// </summary>
public sealed class XPlatMenuItem
{
    private XPlatMenuItem(string text, Action? command, bool isSeparator)
    {
        Text = text;
        Command = command;
        IsSeparator = isSeparator;
    }

    public XPlatMenuItem(string text)
        : this(text, null, false)
    {
    }

    public XPlatMenuItem(string text, Action? command)
        : this(text, command, false)
    {
    }

    /// <summary>The label to show for the item. Empty for a separator.</summary>
    public string Text { get; }

    /// <summary>What picking the item does. Null makes the item decorative.</summary>
    public Action? Command { get; }

    /// <summary>True for a separator line rather than a pickable item.</summary>
    public bool IsSeparator { get; }

    /// <summary>False greys the item out and stops <see cref="Invoke"/> from running its command.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>True to show a check mark next to the item.</summary>
    public bool Checked { get; set; }

    /// <summary>True for the item the platform should activate on a double click or Enter.</summary>
    public bool IsDefault { get; set; }

    /// <summary>Shortcut hint shown next to the text, for platforms that render one.</summary>
    public string? ShortcutText { get; set; }

    /// <summary>A separator line between two groups of items.</summary>
    public static XPlatMenuItem Separator { get; } = new(string.Empty, null, true);

    /// <summary>
    /// Runs the item's command. Returns false when the item is a separator or is disabled, so
    /// a presenter never has to repeat those checks.
    /// </summary>
    public bool Invoke()
    {
        if (IsSeparator || !Enabled)
        {
            return false;
        }

        Command?.Invoke();
        return true;
    }
}
