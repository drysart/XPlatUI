using XPlatUI;

namespace GtkHarness;

/// <summary>
/// Shows <see cref="XPlatContextMenu"/> values with a GTK popover, so menus look and behave like
/// the rest of the desktop instead of being drawn by SkiaSharp. This is the counterpart of the
/// Win32 menu presenter: the library only describes the menu, the platform layer presents it.
/// </summary>
internal sealed class GtkContextMenuPresenter : IXPlatContextMenuPresenter
{
    private readonly Gtk.Widget _anchor;
    private Gtk.Popover? _popover;

    public GtkContextMenuPresenter(Gtk.Widget anchor) => _anchor = anchor;

    public void Show(XPlatContextMenuRequest request)
    {
        var items = request.Menu.TrimmedItems;
        if (items.Count == 0)
        {
            return;
        }

        Dismiss();

        var box = Gtk.Box.New(Gtk.Orientation.Vertical, 0);
        box.SetSizeRequest(200, -1);
        box.SetMarginTop(4);
        box.SetMarginBottom(4);

        foreach (var item in items)
        {
            if (item.IsSeparator)
            {
                var separator = Gtk.Separator.New(Gtk.Orientation.Horizontal);
                separator.SetMarginTop(4);
                separator.SetMarginBottom(4);
                box.Append(separator);
                continue;
            }

            box.Append(CreateItem(item));
        }

        var popover = Gtk.Popover.New();
        popover.SetParent(_anchor);
        popover.SetChild(box);
        popover.SetHasArrow(false);
        popover.SetAutohide(true);
        popover.SetPointingTo(new Gdk.Rectangle
        {
            X = (int)request.Location.X,
            Y = (int)request.Location.Y,
            Width = 1,
            Height = 1,
        });

        popover.Popup();
        _popover = popover;
    }

    /// <summary>Closes the open menu, if any. Called when the surface goes away.</summary>
    public void Dismiss()
    {
        var popover = _popover;
        _popover = null;

        if (popover is null)
        {
            return;
        }

        popover.Popdown();
        popover.Unparent();
    }

    private Gtk.Widget CreateItem(XPlatMenuItem item)
    {
        var content = Gtk.Box.New(Gtk.Orientation.Horizontal, 24);

        var text = item.Checked ? $"\u2713  {item.Text}" : item.Text;
        var label = Gtk.Label.New(text);
        label.SetHalign(Gtk.Align.Start);
        label.SetHexpand(true);
        content.Append(label);

        if (!string.IsNullOrEmpty(item.ShortcutText))
        {
            var shortcut = Gtk.Label.New(item.ShortcutText);
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
            Dismiss();
            item.Invoke();
        };

        return button;
    }
}