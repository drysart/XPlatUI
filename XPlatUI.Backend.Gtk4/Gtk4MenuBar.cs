namespace XPlatUI.Backend.Gtk4;

/// <summary>
/// Presents an <see cref="XPlatMenuBar"/> as a strip of <see cref="Gtk.MenuButton"/>s along the top
/// of a window.
/// </summary>
/// <remarks>
/// GTK has no application-wide menu bar: the desktop draws menus inside each window (GNOME's own
/// apps put them behind a single button in the header bar, KDE and XFCE draw a real strip). The bar
/// is therefore per window, which is exactly how the model describes it: every
/// <see cref="XPlatWindow"/> carries its own <see cref="XPlatMenuBar"/>. On macOS the same data
/// becomes the single system menu bar; see <c>XPlatUI.Backend.AppKit</c>.
/// </remarks>
internal sealed class Gtk4MenuBar : IDisposable
{
    private readonly Gtk.Box _bar = Gtk.Box.New(Gtk.Orientation.Horizontal, 0);
    private readonly List<Gtk.Widget> _buttons = [];
    private readonly List<Gtk.Popover> _popovers = [];

    public Gtk4MenuBar()
    {
        _bar.SetHalign(Gtk.Align.Start);
        _bar.SetMarginStart(4);
        _bar.SetMarginEnd(4);
    }

    /// <summary>The strip itself, to be packed above the drawing area.</summary>
    public Gtk.Widget Widget => _bar;

    public void SetMenus(XPlatMenuBar? menuBar)
    {
        Dismiss();

        foreach (var button in _buttons)
        {
            _bar.Remove(button);
        }

        _buttons.Clear();
        _popovers.Clear();

        if (menuBar is null)
        {
            return;
        }

        foreach (var menu in menuBar.Menus)
        {
            var button = CreateMenuButton(menu);
            _buttons.Add(button);
            _bar.Append(button);
        }
    }

    /// <summary>Closes any open menu.</summary>
    public void Dismiss()
    {
        foreach (var popover in _popovers)
        {
            popover.Popdown();
        }
    }

    public void Dispose()
    {
        Dismiss();
        SetMenus(null);
    }

    private Gtk.Widget CreateMenuButton(XPlatMenu menu)
    {
        var popover = Gtk.Popover.New();
        popover.SetChild(CreateMenuBody(menu));
        popover.SetHasArrow(false);
        popover.SetAutohide(true);
        _popovers.Add(popover);

        var button = Gtk.MenuButton.New();
        button.SetLabel(menu.Text);
        button.SetHasFrame(false);
        button.SetAlwaysShowArrow(false);
        button.AddCssClass("flat");
        button.SetPopover(popover);
        return button;
    }

    private Gtk.Widget CreateMenuBody(XPlatMenu menu)
    {
        var box = Gtk.Box.New(Gtk.Orientation.Vertical, 0);
        box.SetSizeRequest(220, -1);
        box.SetMarginTop(4);
        box.SetMarginBottom(4);

        foreach (var item in menu.Items)
        {
            box.Append(item.IsSeparator
                ? Gtk4MenuItemWidget.CreateSeparator()
                : Gtk4MenuItemWidget.Create(item, Dismiss));
        }

        return box;
    }
}
