using AppKit;
using CoreGraphics;

namespace XPlatUI.Backend.AppKit;

/// <summary>
/// Shows <see cref="XPlatContextMenu"/> values with a native NSMenu, so a right-click gets the same
/// menu every other app on the machine gets - including the system's own appearance, vibrancy and
/// keyboard navigation. This is the counterpart of the Win32 and GTK presenters: the library only
/// describes the menu, the platform layer presents it.
/// </summary>
internal sealed class AppKitContextMenuPresenter : IXPlatContextMenuPresenter
{
    private readonly NSView _view;

    public AppKitContextMenuPresenter(NSView view) => _view = view;

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

    internal static NSMenuItem CreateItem(XPlatMenuItem item)
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
    /// Gives a menu item its native key equivalent, so the shortcut shows as the Command key glyph
    /// every other macOS menu uses and AppKit dispatches it before the view sees the key.
    /// </summary>
    /// <remarks>
    /// A hint that was set as text alone stays a hint: the library cannot know which key "Ctrl+Shift+F"
    /// means without a <see cref="XPlatShortcut"/>, and guessing from the last character produced a
    /// key equivalent that ignored the other modifiers.
    /// </remarks>
    internal static void ApplyShortcut(NSMenuItem menuItem, XPlatMenuItem item)
    {
        if (item.Shortcut is not { } shortcut ||
            AppKitKeyMap.ToKeyEquivalent(shortcut.Key) is not { } keyEquivalent)
        {
            return;
        }

        menuItem.KeyEquivalent = keyEquivalent;
        menuItem.KeyEquivalentModifierMask = AppKitKeyMap.ToKeyEquivalentMask(shortcut.Modifiers);
    }
}