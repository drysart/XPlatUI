namespace XPlatUI;

/// <summary>
/// The top-level menu of a window: a row of <see cref="XPlatMenu"/>s that macOS renders in the
/// one system-wide menu bar and that Windows and Linux render as a bar inside the window.
/// </summary>
/// <remarks>
/// <para>
/// Plain data, like <see cref="XPlatContextMenu"/>: the platform decides how the bar looks and
/// calls <see cref="XPlatMenuItem.Invoke"/> when the user picks an entry, so the library carries
/// no menu drawing code for a bar it does not own.
/// </para>
/// <para>
/// The shape here is what v1 needs - one drop-down per top-level menu - which covers File, Edit,
/// View, Window and Help including the clipboard commands a text box depends on. Submenus can be
/// added when a backend actually requires them.
/// </para>
/// </remarks>
public sealed class XPlatMenuBar
{
    /// <summary>Creates an empty menu bar.</summary>
    public XPlatMenuBar()
    {
    }

    /// <summary>Creates a menu bar holding <paramref name="menus"/>, left to right.</summary>
    public XPlatMenuBar(params XPlatMenu[] menus)
    {
        ArgumentNullException.ThrowIfNull(menus);

        foreach (var menu in menus)
        {
            Menus.Add(menu);
        }
    }

    /// <summary>The top-level menus, left to right.</summary>
    public IList<XPlatMenu> Menus { get; } = new List<XPlatMenu>();

    /// <summary>
    /// Runs the first item whose <see cref="XPlatMenuItem.Shortcut"/> is <paramref name="key"/>
    /// pressed with <paramref name="modifiers"/>, and reports whether an item claimed the key.
    /// </summary>
    /// <remarks>
    /// A matching but disabled item still claims the key, so a shortcut the user can see greyed out
    /// does not fall through to the focused control and do something else instead. Menus are
    /// searched in the order they appear in the bar.
    /// </remarks>
    public bool TryInvokeShortcut(XPlatKey key, XPlatModifiers modifiers)
    {
        foreach (var menu in Menus)
        {
            foreach (var item in menu.Items)
            {
                if (item.IsSeparator ||
                    item.Shortcut is not { } shortcut ||
                    !shortcut.Matches(key, modifiers))
                {
                    continue;
                }

                item.Invoke();
                return true;
            }
        }

        return false;
    }
}

/// <summary>
/// One top-level entry in an <see cref="XPlatMenuBar"/> and the items it drops down.
/// </summary>
public sealed class XPlatMenu
{
    /// <summary>Creates a top-level menu with <paramref name="text"/> and <paramref name="items"/>.</summary>
    public XPlatMenu(string text, params XPlatMenuItem[] items)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        ArgumentNullException.ThrowIfNull(items);

        Text = text;

        foreach (var item in items)
        {
            Items.Add(item);
        }
    }

    /// <summary>
    /// The label shown in the menu bar. On macOS the platform turns the first character into the
    /// mnemonic, so a caller does not spell out the underscore itself.
    /// </summary>
    public string Text { get; }

    /// <summary>The drop-down entries, top to bottom. Use <see cref="XPlatMenuItem.Separator"/> for dividers.</summary>
    public IList<XPlatMenuItem> Items { get; } = new List<XPlatMenuItem>();
}
