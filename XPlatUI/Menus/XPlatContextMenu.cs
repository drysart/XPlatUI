using SkiaSharp;

namespace XPlatUI;

/// <summary>
/// A context menu: an ordered list of items shown at the pointer when the user asks for one.
/// The menu itself draws nothing - <see cref="XPlatContextMenuPresenter.Current"/> hands it to
/// the platform layer, which is free to show a native menu.
/// </summary>
public sealed class XPlatContextMenu
{
    private readonly List<XPlatMenuItem> _items = new();

    public XPlatContextMenu()
    {
    }

    /// <summary>The items in the order they should appear.</summary>
    public IReadOnlyList<XPlatMenuItem> Items => _items;

    /// <summary>True when the menu has nothing worth showing: no items, or only separators.</summary>
    public bool IsEmpty
    {
        get
        {
            foreach (var item in _items)
            {
                if (!item.IsSeparator)
                {
                    return false;
                }
            }

            return true;
        }
    }

    /// <summary>
    /// The items with the separators that would look wrong dropped: leading, trailing, and doubled.
    /// Renderers use this so a menu never starts or ends on a separator line, and never shows two
    /// in a row where the middle group was empty.
    /// </summary>
    public IReadOnlyList<XPlatMenuItem> TrimmedItems
    {
        get
        {
            var trimmed = new List<XPlatMenuItem>(_items.Count);

            foreach (var item in _items)
            {
                if (item.IsSeparator && (trimmed.Count == 0 || trimmed[^1].IsSeparator))
                {
                    continue;
                }

                trimmed.Add(item);
            }

            while (trimmed.Count > 0 && trimmed[^1].IsSeparator)
            {
                trimmed.RemoveAt(trimmed.Count - 1);
            }

            return trimmed;
        }
    }

    /// <summary>Adds an item that runs <paramref name="command"/> when picked.</summary>
    public XPlatContextMenu Add(string text, Action? command = null) => Add(new XPlatMenuItem(text, command));

    public XPlatContextMenu Add(XPlatMenuItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        _items.Add(item);
        return this;
    }

    /// <summary>Adds a separator line between two groups of items.</summary>
    public XPlatContextMenu AddSeparator() => Add(XPlatMenuItem.Separator);

    public XPlatContextMenu Clear()
    {
        _items.Clear();
        return this;
    }

    /// <summary>
    /// Builds the menu offered when a selection is right-clicked and nothing else supplies one:
    /// a single Copy item that puts the selection on the clipboard.
    /// </summary>
    public static XPlatContextMenu CreateCopyMenu(XPlatUiHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        return new XPlatContextMenu().Add("Copy", () => host.CopySelection());
    }

    /// <summary>
    /// Asks the platform presenter to show this menu. Returns false when no presenter is
    /// installed, which is the documented no-op fallback: the menu is simply not shown.
    /// </summary>
    public bool Show(XPlatUiHost host, XPlatControl? control, SKPoint location)
    {
        ArgumentNullException.ThrowIfNull(host);

        if (IsEmpty || XPlatContextMenuPresenter.Current is not { } presenter)
        {
            return false;
        }

        presenter.Show(new XPlatContextMenuRequest(this, host, control, location));
        return true;
    }
}
