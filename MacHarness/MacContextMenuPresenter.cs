using AppKit;
using CoreGraphics;
using XPlatUI;

namespace MacHarness;

/// <summary>
/// Shows <see cref="XPlatContextMenu"/> values with a native NSMenu, so a right-click gets the same
/// menu every other app on the machine gets - including the system's own appearance, vibrancy and
/// keyboard navigation. This is the counterpart of the Win32 and GTK presenters: the library only
/// describes the menu, the platform layer presents it.
/// </summary>
internal sealed class MacContextMenuPresenter : IXPlatContextMenuPresenter
{
    private readonly NSView _view;

    public MacContextMenuPresenter(NSView view) => _view = view;

    public void Show(XPlatContextMenuRequest request)
    {
        var items = request.Menu.TrimmedItems;
        if (items.Count == 0)
        {
            return;
        }

        var menu = new NSMenu("Context")
        {
            // Without this AppKit enables or disables items by asking the responder chain, which
            // would override the enabled state the library has already decided on.
            AutoEnablesItems = false,
        };

        foreach (var item in items)
        {
            if (item.IsSeparator)
            {
                menu.AddItem(NSMenuItem.SeparatorItem);
                continue;
            }

            menu.AddItem(CreateItem(item));
        }

        // The location is in the view's (flipped) coordinate space, which is exactly the space
        // popUpMenuPositioningItem:atLocation:inView: expects, so no conversion is needed.
        var location = new CGPoint(request.Location.X, request.Location.Y);
        menu.PopUpMenu(null!, location, _view);
    }

    private static NSMenuItem CreateItem(XPlatMenuItem item)
    {
        var menuItem = new NSMenuItem(item.Text ?? string.Empty, string.Empty, (_, _) => item.Invoke())
        {
            Enabled = item.Enabled,
            State = item.Checked ? NSCellStateValue.On : NSCellStateValue.Off,
        };

        ApplyShortcut(menuItem, item);
        return menuItem;
    }

    /// <summary>
    /// Turns the library's "Ctrl+C" style shortcut label into a native key equivalent, so the item
    /// shows the Command key glyph the way every other macOS menu does. The library spells them
    /// with Ctrl because that is the cross-platform convention; on macOS the same accelerators are
    /// reached through Command.
    /// </summary>
    private static void ApplyShortcut(NSMenuItem menuItem, XPlatMenuItem item)
    {
        var text = item.ShortcutText;
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        var key = text[^1];
        if (!char.IsLetterOrDigit(key))
        {
            return;
        }

        menuItem.KeyEquivalent = char.ToLowerInvariant(key).ToString();
        menuItem.KeyEquivalentModifierMask = NSEventModifierMask.CommandKeyMask;
    }
}