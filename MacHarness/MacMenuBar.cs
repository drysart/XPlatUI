using AppKit;
using XPlatUI;

namespace MacHarness;

/// <summary>
/// The application menu bar. macOS expects a real menu bar even for a one window app, and the
/// standard editing commands have to be there: their Cmd key equivalents are what route Cut, Copy,
/// Paste and Select All into the focused control, because AppKit consumes a menu key equivalent
/// before it ever reaches the view.
/// </summary>
internal static class MacMenuBar
{
    public static NSMenu Build()
    {
        var main = new NSMenu("Main") { AutoEnablesItems = false };
        main.AddItem(CreateApplicationMenu());
        main.AddItem(CreateEditMenu());
        return main;
    }

    private static NSMenuItem CreateApplicationMenu()
    {
        var menu = new NSMenu("XPlat UI") { AutoEnablesItems = false };
        menu.AddItem(new NSMenuItem("About XPlat UI", (_, _) => ShowAbout()));
        menu.AddItem(NSMenuItem.SeparatorItem);
        menu.AddItem(new NSMenuItem("Hide XPlat UI", "h", (_, _) => Hide()));
        menu.AddItem(NSMenuItem.SeparatorItem);
        menu.AddItem(new NSMenuItem("Quit XPlat UI", "q", (_, _) => NSApplication.SharedApplication.Terminate(null!)));

        return new NSMenuItem("XPlat UI") { Submenu = menu };
    }

    private static NSMenuItem CreateEditMenu()
    {
        var menu = new NSMenu("Edit") { AutoEnablesItems = false };
        menu.AddItem(Accelerator("Cut", "x", XPlatKey.X));
        menu.AddItem(Accelerator("Copy", "c", XPlatKey.C));
        menu.AddItem(Accelerator("Paste", "v", XPlatKey.V));
        menu.AddItem(NSMenuItem.SeparatorItem);
        menu.AddItem(Accelerator("Select All", "a", XPlatKey.A));

        return new NSMenuItem("Edit") { Submenu = menu };
    }

    /// <summary>
    /// A menu item that feeds an editing accelerator back into the UI host. The host and the
    /// focused control decide what the key means, so the menu stays free of editing logic.
    /// </summary>
    private static NSMenuItem Accelerator(string title, string keyEquivalent, XPlatKey key) =>
        new(title, keyEquivalent, (_, _) => Program.SendAccelerator(key));

    private static void Hide() => NSApplication.SharedApplication.Hide(null!);

    private static void ShowAbout()
    {
        var alert = new NSAlert
        {
            MessageText = "XPlat UI",
            InformativeText = "A lightweight, cross platform UI library drawn with SkiaSharp.\n\n" +
                              "This window is a plain AppKit NSView; everything inside it is drawn by the library.",
        };

        alert.AddButton("OK");
        alert.RunModal();
    }
}