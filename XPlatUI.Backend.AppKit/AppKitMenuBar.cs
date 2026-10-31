using AppKit;

namespace XPlatUI.Backend.AppKit;

/// <summary>
/// Mirrors an <see cref="XPlatMenuBar"/> into <c>NSApplication.MainMenu</c>.
/// </summary>
/// <remarks>
/// <para>
/// macOS has one menu bar for the whole process, so this is a translation rather than a control:
/// the application backend rebuilds it whenever the active window's bar changes. Every macOS
/// application also has to carry the standard application and Edit menus, so those are added
/// around whatever the model supplies.
/// </para>
/// <para>
/// The Edit menu is not decoration. AppKit consumes a menu's key equivalent before the view ever
/// sees the key, so Cut, Copy, Paste and Select All only reach the focused control because the
/// items route them back into the UI host.
/// </para>
/// </remarks>
internal static class AppKitMenuBar
{
    /// <summary>Builds the whole menu bar for <paramref name="bar"/>.</summary>
    public static NSMenu Build(string applicationName, XPlatMenuBar? bar, AppKitApplicationBackend application)
    {
        var main = new NSMenu("Main") { AutoEnablesItems = false };

        main.AddItem(CreateApplicationMenu(applicationName, application));
        main.AddItem(CreateEditMenu(application));

        if (bar is not null)
        {
            foreach (var menu in bar.Menus)
            {
                main.AddItem(CreateMenu(menu));
            }
        }

        return main;
    }

    private static NSMenuItem CreateApplicationMenu(string applicationName, AppKitApplicationBackend application)
    {
        var menu = new NSMenu(applicationName) { AutoEnablesItems = false };

        menu.AddItem(new NSMenuItem($"About {applicationName}", (_, _) => ShowAbout(applicationName)));
        menu.AddItem(NSMenuItem.SeparatorItem);
        menu.AddItem(new NSMenuItem($"Hide {applicationName}", "h", (_, _) => NSApplication.SharedApplication.Hide(null!)));
        menu.AddItem(NSMenuItem.SeparatorItem);

        // Quit goes through the model so every window gets to cancel the close first; AppKit's own
        // termination is declined by the application delegate for the same reason.
        menu.AddItem(new NSMenuItem($"Quit {applicationName}", "q", (_, _) => application.RequestExit()));

        return new NSMenuItem(applicationName) { Submenu = menu };
    }

    private static NSMenuItem CreateEditMenu(AppKitApplicationBackend application)
    {
        var menu = new NSMenu("Edit") { AutoEnablesItems = false };

        menu.AddItem(Accelerator("Cut", "x", XPlatKey.X, application));
        menu.AddItem(Accelerator("Copy", "c", XPlatKey.C, application));
        menu.AddItem(Accelerator("Paste", "v", XPlatKey.V, application));
        menu.AddItem(NSMenuItem.SeparatorItem);
        menu.AddItem(Accelerator("Select All", "a", XPlatKey.A, application));

        return new NSMenuItem("Edit") { Submenu = menu };
    }

    /// <summary>
    /// One top-level menu. The library has no submenus in v1, so a menu is exactly one drop-down.
    /// </summary>
    private static NSMenuItem CreateMenu(XPlatMenu menu)
    {
        var text = menu.Text ?? string.Empty;
        var submenu = new NSMenu(text) { AutoEnablesItems = false };

        foreach (var item in menu.Items)
        {
            if (item.IsSeparator)
            {
                submenu.AddItem(NSMenuItem.SeparatorItem);
                continue;
            }

            // The same builder the context menus use, so an item looks identical in both places.
            submenu.AddItem(AppKitContextMenuPresenter.CreateItem(item));
        }

        return new NSMenuItem(text) { Submenu = submenu };
    }

    /// <summary>
    /// A menu item that feeds an editing accelerator back into the UI host. The host and the
    /// focused control decide what the key means, so the menu stays free of editing logic.
    /// </summary>
    private static NSMenuItem Accelerator(
        string title,
        string keyEquivalent,
        XPlatKey key,
        AppKitApplicationBackend application) =>
        new(title, keyEquivalent, (_, _) => application.SendAccelerator(key));

    private static void ShowAbout(string applicationName)
    {
        var alert = new NSAlert
        {
            MessageText = applicationName,
            InformativeText = "A lightweight, cross platform UI library drawn with SkiaSharp.\n\n" +
                              "This window is a plain AppKit NSView; everything inside it is drawn by the library.",
        };

        alert.AddButton("OK");
        alert.RunModal();
    }
}
