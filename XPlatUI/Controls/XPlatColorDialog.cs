using SkiaSharp;

namespace XPlatUI;

/// <summary>
/// A modal colour chooser built from an ordinary <see cref="XPlatWindow"/> and the
/// <see cref="XPlatColorPicker"/> control: the same picker an application can put on a page, with
/// OK and Cancel underneath it.
/// </summary>
/// <remarks>
/// <para>
/// This is deliberately not part of <see cref="IXPlatDialogService"/>. That seam is for dialogs the
/// operating system draws - message boxes, file pickers and the font chooser - where no portable
/// control can stand in. Here one can: <see cref="XPlatColorPicker"/> already does the spectrum,
/// the hue strip, the three component fields and the hex field, so a platform chooser would only
/// replace tested code with three separate answers that look and behave differently. The dialog is
/// therefore library-drawn on every platform, and it has no platform code at all.
/// </para>
/// <para>
/// The modal behaviour comes from the application model rather than a loop of this class's own:
/// <see cref="XPlatWindow.ShowDialog(XPlatWindow?)"/> disables the owner and re-enters the platform
/// loop on all three backends. The dialog opens with the focus on OK, so Enter accepts; Escape
/// cancels from any control in the tree, including the picker's text fields. Closing the window
/// with its own close button is a cancel, like every other dialog.
/// </para>
/// <para>
/// The owner's content is also disabled for the duration. That is redundant where the platform
/// already blocks the owner - Windows and macOS do, and GTK marks the dialog modal - but it makes
/// the same guarantee hold on a backend with no window manager to enforce it, and it is what lets
/// the headless suite assert that the owner really is blocked while the dialog is open.
/// </para>
/// <para>
/// A dialog instance is one showing: construct one, call <see cref="Show"/>, and construct another
/// for the next colour.
/// </para>
/// </remarks>
public sealed class XPlatColorDialog
{
    /// <summary>The title used when <see cref="XPlatColorDialogOptions.Title"/> is null or blank.</summary>
    private const string DefaultTitle = "Choose a colour";

    /// <summary>The client width the dialog opens at, in logical pixels.</summary>
    private const float DialogWidth = 300f;

    /// <summary>The client height the dialog opens at, in logical pixels.</summary>
    private const float DialogHeight = 372f;

    /// <summary>Space kept between the dialog and the edge of the screen, in logical pixels.</summary>
    private const float ScreenMargin = 24f;

    private readonly XPlatWindow _window;
    private readonly XPlatColorPicker _picker;
    private readonly XPlatButton _accept;
    private readonly XPlatButton _cancel;
    private bool _shown;

    /// <summary>
    /// Builds the dialog and its window. Constructing it needs an <see cref="XPlatApplication"/>,
    /// like any other window.
    /// </summary>
    /// <param name="options">The title, initial colour and picker mode, or null for the defaults.</param>
    /// <exception cref="InvalidOperationException">No <see cref="XPlatApplication"/> exists yet.</exception>
    public XPlatColorDialog(XPlatColorDialogOptions? options = null)
    {
        options ??= new XPlatColorDialogOptions();

        _picker = new XPlatColorPicker(options.InitialColor)
        {
            Dock = DockStyle.Fill,
            ColorMode = options.ColorMode,
        };

        // Docking takes the right edge in declaration order, so OK is rightmost and Cancel sits to
        // its left. That is the order macOS and GTK use, and the order the library's own dialogs
        // would have if they drew their buttons themselves.
        _accept = new XPlatButton("OK")
        {
            Dock = DockStyle.Right,
            AutoSize = true,
            Style = XPlatButtonStyle.Primary,
            Margin = new XPlatThickness(8f, 0f, 0f, 0f),
        };
        _accept.AddClickHandler(_ => Accept());

        _cancel = new XPlatButton("Cancel")
        {
            Dock = DockStyle.Right,
            AutoSize = true,
            Style = XPlatButtonStyle.Secondary,
        };
        _cancel.AddClickHandler(_ => Cancel());

        var buttons = new XPlatPanel
        {
            Dock = DockStyle.Bottom,
            Height = 40f,
            ShowFrame = false,
            IsCard = false,
            BackColor = null,
            BorderWidth = 0f,
            CornerRadius = 0f,
            Padding = new XPlatThickness(0f),
            Margin = new XPlatThickness(0f, 8f, 0f, 0f),
        };
        buttons.Controls.Add(_accept);
        buttons.Controls.Add(_cancel);

        var root = new XPlatPanel
        {
            Dock = DockStyle.Fill,
            ShowFrame = false,
            IsCard = false,
            BackColor = null,
            BorderWidth = 0f,
            CornerRadius = 0f,
            Padding = new XPlatThickness(12f),
        };
        root.Controls.Add(buttons);
        root.Controls.Add(_picker);

        _window = new XPlatWindow(
            string.IsNullOrWhiteSpace(options.Title) ? DefaultTitle : options.Title,
            root)
        {
            ClientSize = new SKSize(DialogWidth, DialogHeight),

            // A native colour chooser has one job and one layout; the picker's fields are designed
            // for that size, so the dialog does not offer a second one to design for.
            Resizable = false,
        };

        // The focus is set once the window is really up, which is when the platform can give the
        // keyboard to the control. Enter then activates OK through the button's own key handling;
        // there is no second Enter path to keep in step with it.
        _window.AddShownHandler(_ => _accept.Focus());

        AttachEscapeHandler(root);
    }

    /// <summary>The window the dialog is shown in, for a caller that needs to observe it.</summary>
    internal XPlatWindow Window => _window;

    /// <summary>The picker the dialog edits, for the headless suite to drive.</summary>
    internal XPlatColorPicker Picker => _picker;

    /// <summary>The button Enter activates and a click accepts.</summary>
    internal XPlatButton AcceptButton => _accept;

    /// <summary>The button that cancels.</summary>
    internal XPlatButton CancelButton => _cancel;

    /// <summary>
    /// Shows the dialog modally, owned by <paramref name="owner"/> when it is given, and returns the
    /// chosen colour or null when the user cancelled. Blocks until the dialog closes.
    /// </summary>
    /// <param name="owner">The window to centre on and block, or null to centre on the primary screen.</param>
    /// <returns>The picker's colour when OK was chosen, otherwise null.</returns>
    /// <exception cref="InvalidOperationException">This instance has already been shown.</exception>
    public SKColor? Show(XPlatWindow? owner)
    {
        if (_shown)
        {
            throw new InvalidOperationException("A colour dialog shows once; construct another to ask again.");
        }

        _shown = true;

        var ownerContent = owner?.Content;
        var ownerWasEnabled = ownerContent?.Enabled ?? true;
        if (ownerContent is not null)
        {
            ownerContent.Enabled = false;
        }

        try
        {
            Centre(owner);
            var result = _window.ShowDialog(owner);

            return result == XPlatDialogResult.Ok ? _picker.Color : null;
        }
        finally
        {
            if (ownerContent is not null)
            {
                ownerContent.Enabled = ownerWasEnabled;
            }
        }
    }

    private void Accept()
    {
        _window.DialogResult = XPlatDialogResult.Ok;
        _window.Close();
    }

    private void Cancel()
    {
        _window.DialogResult = XPlatDialogResult.Cancel;
        _window.Close();
    }

    /// <summary>
    /// Puts an Escape handler on every control in the dialog, so Escape cancels wherever the focus
    /// happens to be. A parent does not see a child's key press in this model, so the handler has to
    /// sit on each focusable descendant rather than on the root alone.
    /// </summary>
    private void AttachEscapeHandler(XPlatControl control)
    {
        control.AddKeyDownHandler((_, e) =>
        {
            if (e.Key != XPlatKey.Escape)
            {
                return;
            }

            e.Handled = true;
            Cancel();
        });

        foreach (var child in control.Controls)
        {
            AttachEscapeHandler(child);
        }
    }

    /// <summary>
    /// Centres the dialog on the owner, or on the primary screen when there is no owner, and keeps
    /// it wholly inside that screen's working area. A dialog larger than the screen is shrunk to
    /// fit rather than pushed against an edge: a fixed size that does not fit would otherwise put
    /// the buttons out of reach.
    /// </summary>
    private void Centre(XPlatWindow? owner)
    {
        var application = owner?.Application ?? XPlatApplication.Current;
        var screen = owner?.Screen
            ?? application?.Screens.FirstOrDefault(screen => screen.IsPrimary)
            ?? application?.Screens.FirstOrDefault();

        if (screen is null)
        {
            return;
        }

        var area = screen.WorkingArea;
        var width = MathF.Min(DialogWidth, MathF.Max(1f, area.Width - (2f * ScreenMargin)));
        var height = MathF.Min(DialogHeight, MathF.Max(1f, area.Height - (2f * ScreenMargin)));

        _window.ClientSize = new SKSize(width, height);

        var anchor = area;
        if (owner is not null)
        {
            var left = owner.Location.X;
            var top = owner.Location.Y;

            // SKRect takes the far edges, not a size, so the owner's client size is added here
            // rather than handed in as right and bottom.
            anchor = new SKRect(left, top, left + owner.ClientSize.Width, top + owner.ClientSize.Height);
        }

        var minimumX = area.Left + ScreenMargin;
        var minimumY = area.Top + ScreenMargin;
        var maximumX = MathF.Max(minimumX, area.Right - ScreenMargin - width);
        var maximumY = MathF.Max(minimumY, area.Bottom - ScreenMargin - height);

        _window.Location = new SKPoint(
            Math.Clamp(anchor.MidX - (width / 2f), minimumX, maximumX),
            Math.Clamp(anchor.MidY - (height / 2f), minimumY, maximumY));
    }
}
