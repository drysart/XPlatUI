using SkiaSharp;
using XPlatUI;
using XPlatUI.Backend.Headless;

namespace XPlatUI.Tests;

/// <summary>
/// The library-drawn colour dialog: the window, the picker, the button row and the modal call, all
/// driven through the same host entry points a backend uses.
/// </summary>
/// <remarks>
/// <para>
/// Every interaction here is real input rather than a direct call on the dialog's methods: Enter and
/// Escape go through <see cref="XPlatUiHost.ProcessKeyDown"/>, and OK is pressed with mouse events at
/// the button's own position. That is the input path the Win32, AppKit and GTK4 backends feed, so a
/// test that passes here is describing the dialog rather than a shortcut.
/// </para>
/// <para>
/// What the headless backend cannot show is a nested platform loop: its <c>ShowDialog</c> raises
/// Shown and returns. The dialogs here therefore finish themselves from a Shown handler, and the
/// tests assert on the result and the state at the moment Shown runs. The blocking form of modality
/// is the application model's and is verified on a real window.
/// </para>
/// </remarks>
[TestClass]
public sealed class ColorDialogTests : HeadlessTestBase
{
    [TestMethod]
    [TestCategory("Headless")]
    public void ClickingOkReturnsTheColourThePickerHolds()
    {
        using var app = NewApplication();
        var owner = new XPlatWindow("owner", new XPlatPanel()) { ClientSize = new SKSize(640f, 480f) };
        var dialog = new XPlatColorDialog(new XPlatColorDialogOptions
        {
            InitialColor = new SKColor(0x33, 0x66, 0x99),
        });

        dialog.Window.AddShownHandler(_ =>
        {
            Assert.AreEqual(new SKColor(0x33, 0x66, 0x99), dialog.Picker.Color, "the initial colour is shown");
            Assert.IsTrue(dialog.AcceptButton.IsFocused, "the dialog opens with OK as the default button");

            // A colour the user could have produced with the square and the fields.
            dialog.Picker.Color = new SKColor(0x12, 0x34, 0x56);

            Click(dialog.Window, dialog.AcceptButton);
        });

        var chosen = dialog.Show(owner);

        Assert.AreEqual(new SKColor(0x12, 0x34, 0x56), chosen, "OK returns the picker's colour");
        Assert.IsTrue(dialog.Window.IsClosed, "and the dialog is closed when the call returns");
    }

    [TestMethod]
    [TestCategory("Headless")]
    public void EnterOnTheFocusedOkButtonAccepts()
    {
        using var app = NewApplication();
        var owner = new XPlatWindow("owner", new XPlatPanel());
        var dialog = new XPlatColorDialog(new XPlatColorDialogOptions
        {
            InitialColor = new SKColor(10, 20, 30),
        });

        dialog.Window.AddShownHandler(_ =>
        {
            Assert.IsTrue(dialog.AcceptButton.IsFocused);

            dialog.Window.Host.ProcessKeyDown(XPlatKey.Enter);
            dialog.Window.Host.ProcessKeyUp(XPlatKey.Enter);
        });

        Assert.AreEqual(new SKColor(10, 20, 30), dialog.Show(owner));
    }

    [TestMethod]
    [TestCategory("Headless")]
    public void EnterAcceptsFromAPickerField()
    {
        using var app = NewApplication();
        var owner = new XPlatWindow("owner", new XPlatPanel());
        var dialog = new XPlatColorDialog();
        var field = dialog.Picker.Controls[2];

        dialog.Window.AddShownHandler(_ =>
        {
            // Picking with the mouse leaves the focus in the picker, and the dialog promises that
            // Enter accepts: the default button has to work from there too, not only from OK.
            dialog.Picker.Color = new SKColor(0x40, 0x50, 0x60);
            field.Focus();
            Assert.IsTrue(field.IsFocused, "a picker field has the keyboard");

            dialog.Window.Host.ProcessKeyDown(XPlatKey.Enter);
            dialog.Window.Host.ProcessKeyUp(XPlatKey.Enter);
        });

        Assert.AreEqual(new SKColor(0x40, 0x50, 0x60), dialog.Show(owner));
        Assert.IsTrue(dialog.Window.IsClosed);
    }

    [TestMethod]
    [TestCategory("Headless")]
    public void EscapeCancelsFromAPickerField()
    {
        using var app = NewApplication();
        var owner = new XPlatWindow("owner", new XPlatPanel());
        var dialog = new XPlatColorDialog();

        dialog.Window.AddShownHandler(_ =>
        {
            // A text field inside the picker, not the OK button: Escape has to reach the dialog
            // from wherever the focus is, which a handler on the root alone could not do.
            dialog.Picker.Controls[2].Focus();
            Assert.IsTrue(dialog.Picker.Controls[2].IsFocused);

            dialog.Window.Host.ProcessKeyDown(XPlatKey.Escape);
        });

        Assert.IsNull(dialog.Show(owner), "cancelling returns null");
        Assert.IsTrue(dialog.Window.IsClosed);
    }

    [TestMethod]
    [TestCategory("Headless")]
    public void CancellingLeavesTheCallersColourUntouched()
    {
        using var app = NewApplication();
        var owner = new XPlatWindow("owner", new XPlatPanel());
        var current = new SKColor(0xAA, 0xBB, 0xCC);
        var dialog = new XPlatColorDialog(new XPlatColorDialogOptions { InitialColor = current });

        dialog.Window.AddShownHandler(_ =>
        {
            dialog.Picker.Color = new SKColor(1, 2, 3);
            Click(dialog.Window, dialog.CancelButton);
        });

        var chosen = dialog.Show(owner);

        Assert.IsNull(chosen, "Cancel returns null");
        Assert.AreEqual(new SKColor(0xAA, 0xBB, 0xCC), current, "the caller's own value is not changed");
    }

    [TestMethod]
    [TestCategory("Headless")]
    public void TheInitialColourRoundTripsExactly()
    {
        using var app = NewApplication();
        var owner = new XPlatWindow("owner", new XPlatPanel());

        // Every channel is represented exactly, including the far ends of the range, and the alpha
        // the picker preserves through an edit is the caller's own.
        var initial = new SKColor(0, 255, 17, 128);
        var dialog = new XPlatColorDialog(new XPlatColorDialogOptions { InitialColor = initial });

        dialog.Window.AddShownHandler(_ => Click(dialog.Window, dialog.AcceptButton));

        Assert.AreEqual(initial, dialog.Show(owner));
    }

    [TestMethod]
    [TestCategory("Headless")]
    public void TheOwnerIsBlockedWhileTheDialogIsOpen()
    {
        using var app = NewApplication();
        var ownerContent = new XPlatPanel();
        var owner = new XPlatWindow("owner", ownerContent);
        var dialog = new XPlatColorDialog();
        var sawBlocked = false;

        dialog.Window.AddShownHandler(_ =>
        {
            sawBlocked = !ownerContent.Enabled;
            Click(dialog.Window, dialog.CancelButton);
        });

        Assert.IsNull(dialog.Show(owner));
        Assert.IsTrue(sawBlocked, "the owner's content is disabled for the modal session");
        Assert.IsTrue(ownerContent.Enabled, "and is enabled again once the dialog closes");
    }

    [TestMethod]
    [TestCategory("Headless")]
    public void TheDialogCentresOnTheOwnerAndStaysInsideTheScreen()
    {
        using var app = NewApplication();
        var owner = new XPlatWindow("owner", new XPlatPanel())
        {
            ClientSize = new SKSize(400f, 300f),

            // Bottom-right of the headless 1920 by 1080 screen, so the dialog would hang over the
            // edge if it were simply centred without clamping.
            Location = new SKPoint(1500f, 900f),
        };
        var dialog = new XPlatColorDialog();
        SKPoint location = default;

        dialog.Window.AddShownHandler(_ =>
        {
            location = dialog.Window.Location;
            Click(dialog.Window, dialog.CancelButton);
        });

        dialog.Show(owner);

        // Centred would be (1550, 864); the vertical clamp is the screen's 1080 less the margin and
        // the dialog's height.
        Assert.AreEqual(1550f, location.X, 0.5f);
        Assert.AreEqual(684f, location.Y, 0.5f);
        Assert.AreEqual(new SKSize(300f, 372f), dialog.Window.ClientSize, "the dialog keeps its default size");
    }

    [TestMethod]
    [TestCategory("Headless")]
    public void WithNoOwnerTheDialogCentresOnThePrimaryScreen()
    {
        using var app = NewApplication();
        var dialog = new XPlatColorDialog();
        SKPoint location = default;

        dialog.Window.AddShownHandler(_ =>
        {
            location = dialog.Window.Location;
            Click(dialog.Window, dialog.CancelButton);
        });

        Assert.IsNull(dialog.Show(null));

        Assert.AreEqual((1920f - 300f) / 2f, location.X, 0.5f);
        Assert.AreEqual((1080f - 372f) / 2f, location.Y, 0.5f);
    }

    [TestMethod]
    [TestCategory("Core")]
    public void TheOptionsReachTheWindowAndThePicker()
    {
        using var app = NewApplication();
        var options = new XPlatColorDialogOptions
        {
            Title = "Pick a colour",
            InitialColor = new SKColor(1, 2, 3),
            ColorMode = XPlatColorMode.Hsl,
        };

        var dialog = new XPlatColorDialog(options);

        Assert.AreEqual("Pick a colour", dialog.Window.Title);
        Assert.AreEqual(new SKColor(1, 2, 3), dialog.Picker.Color);
        Assert.AreEqual(XPlatColorMode.Hsl, dialog.Picker.ColorMode);
        Assert.IsFalse(dialog.Window.Resizable, "a colour dialog has one layout");
    }

    [TestMethod]
    [TestCategory("Core")]
    public void AnUntitledDialogUsesItsDefaultTitle()
    {
        using var app = NewApplication();

        Assert.AreEqual("Choose a colour", new XPlatColorDialog().Window.Title);
        Assert.AreEqual("Choose a colour", new XPlatColorDialog(new XPlatColorDialogOptions { Title = "  " }).Window.Title);
    }

    [TestMethod]
    [TestCategory("Headless")]
    public void ClosingGivesTheOwnerBackTheFocus()
    {
        using var app = NewApplication();
        var owner = new XPlatWindow("owner", new XPlatPanel());
        var dialog = new XPlatColorDialog();
        var activated = 0;

        owner.AddActivatedHandler(_ => activated++);
        dialog.Window.AddShownHandler(_ => Click(dialog.Window, dialog.CancelButton));

        dialog.Show(owner);

        // The modal session took the keyboard; the owner has to be brought back afterwards or the
        // application receives no keys until the user clicks into it. AppKit is the platform where
        // that matters, and this is the model-level signal every backend raises.
        Assert.AreEqual(1, activated, "the owner is activated once the dialog closes");
    }

    [TestMethod]
    [TestCategory("Headless")]
    public void ClosingPutsTheKeyboardBackOnTheControlThatHadIt()
    {
        using var app = NewApplication();
        var button = new XPlatButton { Text = "Choose" };
        var owner = new XPlatWindow("owner", button);
        owner.Show();
        button.Focus();
        Assert.IsTrue(button.IsFocused, "the owner's button starts with the keyboard");

        var dialog = new XPlatColorDialog();
        var focusedWhileOpen = true;

        dialog.Window.AddShownHandler(_ =>
        {
            focusedWhileOpen = button.IsFocused;
            Click(dialog.Window, dialog.CancelButton);
        });

        dialog.Show(owner);

        // Disabling the owner's content is what blocks it, and it takes the keyboard off the
        // control that held it. Nothing puts it back by itself, so the caller would be left with a
        // window that answers to nothing until it was clicked - which is exactly what the demo did
        // on macOS before this.
        Assert.IsFalse(focusedWhileOpen, "the owner's button loses the keyboard while the dialog is open");
        Assert.IsTrue(button.IsFocused, "and has it back once the dialog closes");
        Assert.IsTrue(button.Enabled, "with the owner's content enabled again");
    }

    [TestMethod]
    [TestCategory("Headless")]
    public void TheDialogShowsOnce()
    {
        using var app = NewApplication();
        var owner = new XPlatWindow("owner", new XPlatPanel());
        var dialog = new XPlatColorDialog();

        dialog.Window.AddShownHandler(_ => Click(dialog.Window, dialog.CancelButton));
        dialog.Show(owner);

        Assert.ThrowsExactly<InvalidOperationException>(() => dialog.Show(owner));
    }

    /// <summary>Clicks a button the way a pointer does, at the button's own position in the host.</summary>
    private static void Click(XPlatWindow window, XPlatControl control)
    {
        var position = control.GetPositionInHost();
        var center = new SKPoint(
            position.X + (control.ClientRect.Width / 2f),
            position.Y + (control.ClientRect.Height / 2f));

        window.Host.ProcessMouseMove(center);
        window.Host.ProcessMouseDown(center, XPlatMouseButtons.Left, XPlatModifiers.None);
        window.Host.ProcessMouseUp(center, XPlatMouseButtons.Left, XPlatModifiers.None);
    }

    private static XPlatApplication NewApplication() =>
        new(new HeadlessApplicationBackend(), new XPlatApplicationOptions { ApplicationName = "tests" });
}
