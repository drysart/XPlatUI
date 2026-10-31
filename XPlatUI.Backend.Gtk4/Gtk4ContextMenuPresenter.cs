namespace XPlatUI.Backend.Gtk4;

/// <summary>
/// Shows <see cref="XPlatContextMenu"/> values with a GTK popover, so menus look and behave like
/// the rest of the desktop instead of being drawn by SkiaSharp. This is the counterpart of the
/// Win32 menu presenter: the library only describes the menu, the platform layer presents it.
/// </summary>
internal sealed class Gtk4ContextMenuPresenter : IXPlatContextMenuPresenter
{
    private readonly Gtk.Widget _anchor;
    private Gtk.Popover? _popover;

    public Gtk4ContextMenuPresenter(Gtk.Widget anchor) => _anchor = anchor;

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
            box.Append(item.IsSeparator
                ? Gtk4MenuItemWidget.CreateSeparator()
                : Gtk4MenuItemWidget.Create(item, Dismiss));
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

    /// <summary>Closes the open menu, if any. Called when the view goes away.</summary>
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
}
