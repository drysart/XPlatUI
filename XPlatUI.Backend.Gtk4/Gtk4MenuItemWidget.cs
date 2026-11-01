namespace XPlatUI.Backend.Gtk4;

/// <summary>
/// Builds the GTK widgets for <see cref="XPlatMenuItem"/> values. Context menus and the menu bar
/// share this so the two cannot drift apart visually or in behaviour.
/// </summary>
internal static class Gtk4MenuItemWidget
{
    public static Gtk.Widget Create(XPlatMenuItem item, Action? beforeInvoke)
    {
        var content = Gtk.Box.New(Gtk.Orientation.Horizontal, 24);

        var text = item.Checked ? $"\u2713  {item.Text}" : item.Text;
        var label = Gtk.Label.New(text);
        label.SetHalign(Gtk.Align.Start);
        label.SetHexpand(true);
        content.Append(label);

        if (item.ShortcutDisplayText is { } hint)
        {
            var shortcut = Gtk.Label.New(hint);
            shortcut.SetHalign(Gtk.Align.End);
            shortcut.AddCssClass("dim-label");
            content.Append(shortcut);
        }

        var button = Gtk.Button.New();
        button.SetChild(content);
        button.SetHalign(Gtk.Align.Fill);
        button.AddCssClass("flat");
        button.SetSensitive(item.Enabled);
        button.OnClicked += (_, _) =>
        {
            // Close before running the command so the command is free to open another menu.
            beforeInvoke?.Invoke();
            item.Invoke();
        };

        return button;
    }

    public static Gtk.Widget CreateSeparator()
    {
        var separator = Gtk.Separator.New(Gtk.Orientation.Horizontal);
        separator.SetMarginTop(4);
        separator.SetMarginBottom(4);
        return separator;
    }
}
