using System.Diagnostics;
using SkiaSharp;
using XPlatUI;
using XPlatUI.Animations;
using XPlatUI.AutoComplete;
using XPlatUI.Backend.Headless;
using XPlatUI.DragDrop;
using XPlatUI.Multiline;
using XPlatUI.Spans;
using XPlatUI.Undo;
using XDock = XPlatUI.DockStyle;

namespace XPlatUI.Demo;

/// <summary>
/// The sample XPlat UI shown by the harnesses. It is kept separate from any host window so the
/// very same control tree is rendered by the Win32 harness, by the GTK4 harness, and headlessly
/// (see <see cref="RenderToPng"/>).
/// </summary>
public sealed class DemoUi
{
    private XPlatLabel _statusLabel = null!;
    private XPlatLabel _counterLabel = null!;
    private XPlatButton _primaryButton = null!;
    private XPlatListBox _navList = null!;
    private XPlatPanel _stage = null!;
    private XPlatControl[] _pages = null!;
    private XPlatControl? _shownPage;
    private XPlatControl? _menuDemoTarget;
    private XPlatTextBox? _undoFocusTarget;
    private int _clicks;

    // The inherited-appearance page. The fonts are the page's for the life of the window: controls
    // never dispose a font they were handed, and the theme keeps the face it is given.
    private XPlatPanel? _appearanceStage;
    private XPlatLabel? _appearanceInherited;
    private XPlatPanel? _appearanceNested;
    private XPlatLabel? _appearanceLabel;
    private XPlatLabel? _appearanceOverrides;
    private XPlatLabel? _appearanceReadout;
    private XPlatFont? _appearanceSubtreeFont;
    private XPlatFont? _appearanceBigFont;
    private XPlatFont? _appearanceFace;

    // The font dialog page. The fonts are the page's for the life of the window, like the
    // appearance page's: a control never disposes a font it was handed, and a paint may still be
    // using the previous one when the next choice arrives.
    private XPlatPanel? _fontDialogStage;
    private XPlatLabel? _fontDialogReadout;
    private XPlatFontSelection? _fontDialogSelection;
    private readonly List<XPlatFont> _fontDialogFonts = [];

    // The colour dialog page. The swatch is the current colour and the readout is the same value in
    // text, so a cancelled dialog can be told apart from a chosen one at a glance.
    private SKColor _colorDialogColor = new(0x33, 0x66, 0x99);
    private XPlatPanel? _colorDialogSwatch;
    private XPlatLabel? _colorDialogReadout;

    public DemoUi()
    {
        Root = BuildUi();

        // A page that has no menu of its own still has to answer a right-click: the host asks for a
        // menu only after the control under the pointer has declined, and the native menu paths show
        // nothing at all when nobody answers. This one is genuinely application-wide - it belongs to
        // the window, not to any one control - so it is the host's handler, and it runs last. The
        // selection's Copy menu comes after it, so a page menu is only offered while there is
        // nothing selected to copy. A text box keeps its own Cut/Copy/Paste menu, which is why the
        // page menu is not offered over one.
        OnHostAvailable(Root, host => host.AddContextMenuOpeningHandler((_, args) =>
        {
            if (host.Selection.IsEmpty && args.Control is not XPlatTextBox)
            {
                args.Menu = BuildPageContextMenu();
            }
        }));
    }

    /// <summary>The root of the sample control tree.</summary>
    public XPlatControl Root { get; }
    /// <summary>Shows the page that holds the first text field and puts the caret in it, which then blinks.</summary>
    public void FocusFirstTextBox()    {
        for (int index = 0; index < _pages.Length; index++)
        {
            if (FindTextBox(_pages[index]) is not { } box)
            {
                continue;
            }

            _navList.SelectedIndex = index;
            box.Focus();
            return;
        }
    }

    /// <summary>
    /// Puts the caret in the undo page's single-line field, which is what <c>--undo-focus</c> is for:
    /// a screenshot or a script needs the field ready to type without hunting for it with the
    /// pointer.
    /// </summary>
    public void FocusUndoDemo() => _undoFocusTarget?.Focus();

    private static XPlatTextBox? FindTextBox(XPlatControl control)
    {
        var pending = new Stack<XPlatControl>();
        pending.Push(control);

        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if (current is XPlatTextBox box)
            {
                return box;
            }

            foreach (var child in current.Controls)
            {
                pending.Push(child);
            }
        }

        return null;
    }

    /// <summary>Switches the stage to the page at <paramref name="index"/>; an index outside the pages is ignored.</summary>
    /// <param name="index">The page to show, in the order the navigation lists them.</param>
    public void SelectPage(int index)
    {
        if (index >= 0 && index < _pages.Length)
        {
            _navList.SelectedIndex = index;
        }
    }

    /// <summary>
    /// The menu a page offers when the control under the pointer has none of its own: the two moves
    /// a reader of a page wants, and a line about what the sample is. It is built per right-click so
    /// the page moves know which page they are on.
    /// </summary>
    private XPlatContextMenu BuildPageContextMenu()
    {
        int index = _navList.SelectedIndex;

        return new XPlatContextMenu()
            .Add(new XPlatMenuItem("&Previous page", () => SelectPage(index - 1)) { Enabled = index > 0 })
            .Add(new XPlatMenuItem("&Next page", () => SelectPage(index + 1))
            {
                Enabled = index < _pages.Length - 1,
            })
            .AddSeparator()
            .Add(new XPlatMenuItem("&About this sample", () => SetStatus(
                "Every control here is drawn by XPlatUI itself, so the same tree runs on Windows, macOS and Linux.")));
    }

    /// <summary>
    /// Renders the sample UI to a PNG without opening a window. Useful for visual regression tests
    /// and for verifying the render pipeline on CI.
    /// </summary>
    /// <remarks>
    /// This goes through the real application model - <see cref="XPlatApplication"/>,
    /// <see cref="XPlatWindow"/> and the headless backend - so a regression here is a regression in
    /// the model, not just in the demo. The window's client size is set explicitly, so the model's
    /// default sizing never runs and the output matches what the harnesses paint.
    /// </remarks>
    /// <param name="path">The file to write.</param>
    /// <param name="width">Client width in logical pixels.</param>
    /// <param name="height">Client height in logical pixels.</param>
    /// <param name="page">The page to show, or -1 for the first one.</param>
    /// <param name="menuBar">
    /// Builds the window's menu bar from the window and the sample it drives, or null to render
    /// without one. A screenshot should show the same strip the window does, so the harness passes
    /// its own bar in.
    /// </param>
    /// <param name="afterShown">
    /// Runs once the window is up and before the frame is written, which is the only moment a popup
    /// can be opened: a popup needs a host, and a page has none until it is attached. The menus page
    /// uses it to render with its context menu open.
    /// </param>
    public static void RenderToPng(
        string path,
        int width,
        int height,
        int page = -1,
        Func<XPlatWindow, DemoUi, XPlatMenuBar>? menuBar = null,
        Action<XPlatWindow, DemoUi>? afterShown = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var demo = new DemoUi();
        demo.SelectPage(page);

        var backend = new HeadlessApplicationBackend();
        using var application = new XPlatApplication(backend);
        var window = new XPlatWindow("XPlatUI sample", demo.Root)
        {
            ClientSize = new SKSize(width, height),
        };

        if (menuBar is not null)
        {
            window.MenuBar = menuBar(window, demo);
        }

        window.AddShownHandler(_ =>
        {
            afterShown?.Invoke(window, demo);
            backend.LastWindow!.SavePng(path);
        });
        window.Show();
        application.Run();
    }

    private XPlatControl BuildUi()
    {
        // The window background comes from the host's ClearColor, which follows the palette.
        var root = new XPlatPanel
        {
            ShowFrame = false,
            IsCard = false,
            BackColor = null,
            BorderWidth = 0f,
            CornerRadius = 0f,
            Padding = new XPlatThickness(0f),
        };

        root.Controls.Add(BuildHeader());
        root.Controls.Add(BuildFooter());
        root.Controls.Add(BuildContent());

        // The whole window takes drops, so files dragged in from the desktop and text dragged from
        // any application land here unless the control under the pointer handles drops itself.
        root.AllowDrop = true;
        root.AddDragDropHandler((_, e) => OnRootDrop(e));

        return root;
    }

    private XPlatPanel BuildHeader()
    {
        var header = new XPlatPanel
        {
            Dock = XDock.Top,
            Height = 100f,
            ShowFrame = false,
            IsCard = false,
            BackColor = null,
            BorderWidth = 0f,
            CornerRadius = 0f,
            Padding = new XPlatThickness(0f),
        };

        header.Controls.Add(BuildAppearanceButton());
        header.Controls.Add(BuildOwnedWindowButton());

        header.Controls.Add(new XPlatLabel("XPlat UI")
        {
            Dock = XDock.Top,
            AutoSize = false,
            Height = 44f,
            Font = XPlatTheme.TitleFont,
            TextAlign = XPlatContentAlignment.BottomLeft,
            Margin = new XPlatThickness(28f, 16f, 28f, 0f),
        });

        header.Controls.Add(new XPlatLabel("A lightweight, AOT-friendly, cross-platform control layer rendered with SkiaSharp.")
        {
            Dock = XDock.Top,
            AutoSize = false,
            Height = 24f,
            Font = XPlatTheme.CaptionFont,
            ForeColorRole = XPlatThemeColor.MutedForeground,
            TextAlign = XPlatContentAlignment.TopLeft,
            Margin = new XPlatThickness(28f, 4f, 28f, 0f),
        });

        return header;
    }

    /// <summary>
    /// Swaps the palette. The library repaints on its own (the host listens to
    /// <see cref="XPlatTheme.AddChangedHandler"/>), so this is the whole of the application's dark mode
    /// story.
    /// </summary>
    private XPlatButton BuildAppearanceButton()
    {
        var button = new XPlatButton("Switch appearance")
        {
            Dock = XDock.Right,
            Style = XPlatButtonStyle.Secondary,
            AutoSize = true,
            Margin = new XPlatThickness(8f, 20f, 28f, 0f),
        };

        button.AddClickHandler(_ => SetAppearance(XPlatTheme.IsDarkMode ? XPlatPalette.Light : XPlatPalette.Dark));

        return button;
    }

    /// <summary>
    /// Applies <paramref name="palette"/> and reports it in the status line, so the header button
    /// and the View menu describe the change the same way.
    /// </summary>
    public void SetAppearance(XPlatPalette palette)
    {
        XPlatTheme.Palette = palette;
        SetStatus(ReferenceEquals(palette, XPlatPalette.Dark)
            ? "Dark palette applied - every control recoloured on the next frame."
            : "Light palette applied - every control recoloured on the next frame.");
    }

    /// <summary>
    /// Opens a second window from the UI itself. Windows has no menu bar in this sample, so this is
    /// the only way to reach ownership without a command-line flag there.
    /// </summary>
    private XPlatButton BuildOwnedWindowButton()
    {
        var button = new XPlatButton("Owned window")
        {
            Dock = XDock.Right,
            Style = XPlatButtonStyle.Secondary,
            AutoSize = true,
            Margin = new XPlatThickness(8f, 20f, 8f, 0f),
        };

        button.AddClickHandler(_ => ShowOwnedWindow());

        return button;
    }

    private XPlatPanel BuildFooter()
    {
        var footer = new XPlatPanel
        {
            Dock = XDock.Bottom,
            Height = 64f,
            ShowFrame = true,
            IsCard = false,
            BorderWidth = 0f,
            CornerRadius = 0f,
            Padding = new XPlatThickness(0f),
        };

        _statusLabel = new XPlatLabel("Ready - click a button, or Tab/Enter to drive the UI from the keyboard.")
        {
            Dock = XDock.Fill,
            AutoSize = false,
            Font = XPlatTheme.CaptionFont,
            ForeColorRole = XPlatThemeColor.MutedForeground,
            TextAlign = XPlatContentAlignment.MiddleLeft,
            Margin = new XPlatThickness(28f, 0f, 12f, 0f),
        };

        var resetButton = new XPlatButton("Reset")
        {
            Dock = XDock.Right,
            Style = XPlatButtonStyle.Secondary,
            AutoSize = true,
            Margin = new XPlatThickness(8f, 16f, 28f, 16f),
        };
        resetButton.AddClickHandler(_ =>
        {
            _clicks = 0;
            UpdateCounter();
            SetStatus("Counter reset.");
        });

        var toggleButton = new XPlatButton("Disable primary")
        {
            Dock = XDock.Right,
            Style = XPlatButtonStyle.Secondary,
            AutoSize = true,
            Margin = new XPlatThickness(8f, 16f, 0f, 16f),
        };
        toggleButton.AddClickHandler(_ =>
        {
            _primaryButton.Enabled = !_primaryButton.Enabled;
            toggleButton.Text = _primaryButton.Enabled ? "Disable primary" : "Enable primary";
            SetStatus(_primaryButton.Enabled ? "Primary action enabled." : "Primary action disabled.");
        });

        footer.Controls.Add(_statusLabel);
        footer.Controls.Add(resetButton);
        footer.Controls.Add(toggleButton);
        return footer;
    }

    /// <summary>
    /// The stage pages, in the order the navigation lists them. The row text and the builder sit
    /// together, so a page cannot be added without a row to select it by.
    /// </summary>
    private static readonly (string Title, Func<DemoUi, XPlatControl> Build)[] Demos =
    {
        ("Split container", demo => Page(demo.BuildSplitDemo())),
        ("Stack panel", demo => Page(demo.BuildStackPanelDemo())),
        ("Text input", demo => Page(demo.BuildTextInputDemo())),
        ("Dialogs", demo => Page(demo.BuildDialogDemo())),
        ("Table layout", demo => Page(demo.BuildTableLayoutDemo())),
        ("Labels and text", demo => Page(demo.BuildLabelsCard())),
        ("Buttons and input", demo => Page(demo.BuildButtonsCard())),
        ("Check boxes and radios", demo => Page(demo.BuildToggleDemo())),
        ("Progress bars", demo => Page(demo.BuildProgressDemo())),
        ("Group boxes", demo => Page(demo.BuildGroupBoxDemo())),
        ("Rich text", demo => Page(demo.BuildRichTextCard())),
        ("Images and layout", demo => Page(BuildImagesCard())),
        ("Selection and clipboard", demo => Page(demo.BuildSelectionCard())),
        ("Scrolling", demo => Page(demo.BuildScrollingCard())),
        ("Lists", demo => Page(demo.BuildListBoxCard())),
        ("Performance: list box", demo => Page(demo.BuildPerformancePage())),
        ("Performance: controls", demo => Page(demo.BuildControlStackPage())),
        ("Combo boxes", demo => Page(demo.BuildComboBoxDemo())),
        ("Tab controls", demo => Page(demo.BuildTabControlDemo())),
        ("Tool tips", demo => Page(demo.BuildToolTipDemo())),
        ("List views", demo => Page(demo.BuildListViewDemo())),
        ("Tree view", demo => Page(demo.BuildTreeViewDemo())),
        ("Numeric up-down", demo => Page(demo.BuildNumericUpDownDemo())),
        ("Track bars", demo => Page(demo.BuildTrackBarDemo())),
        ("Link labels", demo => Page(demo.BuildLinkLabelDemo())),
        ("Status and tool strips", demo => Page(demo.BuildToolStripDemo())),
        ("Menus", demo => Page(demo.BuildMenuDemo())),
        ("Date and time", demo => Page(demo.BuildDateTimePickerDemo())),
        ("Text box autocomplete", demo => Page(demo.BuildAutoCompleteDemo())),
        ("Multi-line text", demo => Page(demo.BuildMultilineDemo())),
        ("Text box spans", demo => Page(demo.BuildSpanDemo())),
        ("Undo and redo", demo => Page(demo.BuildUndoDemo())),
        ("Fonts from a file", demo => Page(demo.BuildFontDemo())),
        ("Inherited appearance", demo => Page(demo.BuildAppearanceDemo())),
        ("Tab order", demo => Page(demo.BuildTabOrderDemo())),
        ("Animations", demo => Page(demo.BuildAnimationDemo())),
        ("Colour picker", demo => Page(demo.BuildColorPickerDemo())),
        ("Font dialog", demo => Page(demo.BuildFontDialogDemo())),
        ("Colour dialog", demo => Page(demo.BuildColorDialogDemo())),
    };

    /// <summary>
    /// A navigation list down the left and one page at a time on the right. The splitter between
    /// them is draggable, so the navigation can be widened when a demo name does not fit.
    /// </summary>
    private XPlatControl BuildContent()
    {
        _pages = new XPlatControl[Demos.Length];
        for (int index = 0; index < Demos.Length; index++)
        {
            _pages[index] = Demos[index].Build(this);
        }

        _stage = new XPlatPanel
        {
            Dock = XDock.Fill,
            ShowFrame = false,
            IsCard = false,
            BackColor = null,
            BorderWidth = 0f,
            CornerRadius = 0f,
            Padding = new XPlatThickness(0f),
        };

        _navList = new XPlatListBox
        {
            Dock = XDock.Fill,
            SelectionMode = XPlatSelectionMode.One,
            VerticalScroll = XPlatScrollStyle.Automatic,
            HorizontalScroll = XPlatScrollStyle.None,
            BorderWidth = 0f,
            ItemHeight = 32f,
        };

        foreach (var demo in Demos)
        {
            _navList.Items.Add(demo.Title);
        }

        _navList.AddSelectedIndexChangedHandler(_ => ShowPage(_navList.SelectedIndex));

        var nav = new XPlatPanel
        {
            Dock = XDock.Fill,
            ShowFrame = false,
            IsCard = false,
            BackColor = null,
            BorderWidth = 0f,
            CornerRadius = 0f,
            Padding = new XPlatThickness(20f, 16f, 12f, 16f),
        };
        nav.Controls.Add(_navList);

        // The navigation is window chrome rather than a demo, so it keeps no tab stop of its own:
        // Tab goes straight from the header into the list. The splitter still drags with the mouse.
        var split = new XPlatSplitContainer
        {
            Dock = XDock.Fill,
            Orientation = XPlatOrientation.Vertical,
            SplitterDistance = 210f,
            Panel1MinSize = 150f,
            Panel1MaxSize = 400f,
            Panel2MinSize = 400f,
            TabStop = false,
            BackColor = null,
            BorderWidth = 0f,
            CornerRadius = 0f,
        };

        foreach (var panel in new[] { split.Panel1, split.Panel2 })
        {
            panel.ShowFrame = false;
            panel.IsCard = false;
            panel.BackColor = null;
            panel.BorderWidth = 0f;
            panel.CornerRadius = 0f;
            panel.Padding = new XPlatThickness(0f);
        }

        split.Panel1.Controls.Add(nav);
        split.Panel2.Controls.Add(_stage);

        _navList.SelectedIndex = 0;
        return split;
    }

    /// <summary>
    /// Wraps a page body in the inset every page shares, so a page is only ever its content and
    /// the space around it is decided in one place.
    /// </summary>
    private static XPlatPanel Page(XPlatControl body)
    {
        var page = new XPlatPanel
        {
            Dock = XDock.Fill,
            ShowFrame = false,
            IsCard = false,
            BackColor = null,
            BorderWidth = 0f,
            CornerRadius = 0f,
            Padding = new XPlatThickness(20f, 16f, 20f, 16f),
        };
        page.Controls.Add(body);
        return page;
    }

    /// <summary>Swaps the page on the stage. Every page is built once and kept, so returning to one costs a re-parent.</summary>
    private void ShowPage(int index)
    {
        if (index < 0 || index >= _pages.Length)
        {
            return;
        }

        var page = _pages[index];
        if (ReferenceEquals(page, _shownPage))
        {
            return;
        }

        if (_shownPage is not null)
        {
            _stage.Controls.Remove(_shownPage);
        }

        _shownPage = page;
        _stage.Controls.Add(page);
    }

    private XPlatPanel BuildTextInputDemo()
    {
        var section = new XPlatPanel
        {
            // A row of fields is a caption plus a field tall, so the stage below it stays empty
            // rather than stretching the inputs to the height of the window.
            Dock = XDock.Top,
            Height = 54f,
            ShowFrame = false,
            IsCard = false,
            BackColor = null,
            BorderWidth = 0f,
            CornerRadius = 0f,
            Padding = new XPlatThickness(0f),
        };

        var nameBox = new XPlatTextBox
        {
            PlaceholderText = "Type your name",
            MaxLength = 24,
            AllowDrop = true,
        };
        nameBox.AddTextChangedHandler(_ => SetStatus(nameBox.Text.Length == 0
            ? "Name cleared."
            : $"Name: {nameBox.Text} ({nameBox.Text.Length}/{nameBox.MaxLength})"));

        var passwordBox = new XPlatTextBox
        {
            IsPassword = true,
            PlaceholderText = "Password",
        };
        passwordBox.AddTextChangedHandler(_ => SetStatus($"Password length: {passwordBox.Text.Length}"));

        var readOnlyBox = new XPlatTextBox
        {
            Text = "Read-only - select, then Ctrl+C",
            ReadOnly = true,
        };

        var row = new XPlatPanel
        {
            Dock = XDock.Fill,
            ShowFrame = false,
            IsCard = false,
            BackColor = null,
            BorderWidth = 0f,
            CornerRadius = 0f,
            Padding = new XPlatThickness(0f),
        };
        row.Controls.Add(Field("Name", nameBox, 320f));
        row.Controls.Add(Field("Password", passwordBox, 280f));
        row.Controls.Add(Field("Read only", readOnlyBox, 0f));
        section.Controls.Add(row);

        return section;
    }

    /// <summary>A captioned input: a small label above the control. A width of 0 docks the field to fill.</summary>
    private static XPlatPanel Field(string caption, XPlatTextBox input, float width)
    {
        bool fill = width <= 0f;
        var field = new XPlatPanel
        {
            Dock = fill ? XDock.Fill : XDock.Left,
            Width = fill ? 0f : width,
            ShowFrame = false,
            IsCard = false,
            BackColor = null,
            BorderWidth = 0f,
            CornerRadius = 0f,
            Padding = new XPlatThickness(0f, 0f, fill ? 0f : 16f, 0f),
        };

        field.Controls.Add(new XPlatLabel(caption)
        {
            Dock = XDock.Top,
            AutoSize = false,
            Height = 20f,
            Font = XPlatTheme.CaptionFont,
            ForeColorRole = XPlatThemeColor.MutedForeground,
            TextAlign = XPlatContentAlignment.TopLeft,
        });

        input.Dock = XDock.Fill;
        field.Controls.Add(input);

        return field;
    }

    private XPlatPanel BuildDialogDemo()
    {
        var section = new XPlatPanel
        {
            // The buttons are as tall as a button is, so the row keeps a height of its own rather
            // than stretching to the height of the stage.
            Dock = XDock.Top,
            Height = 44f,
            ShowFrame = false,
            IsCard = false,
            BackColor = null,
            BorderWidth = 0f,
            CornerRadius = 0f,
            Padding = new XPlatThickness(0f),
        };

        var row = new XPlatPanel
        {
            Dock = XDock.Fill,
            ShowFrame = false,
            IsCard = false,
            BackColor = null,
            BorderWidth = 0f,
            CornerRadius = 0f,
            Padding = new XPlatThickness(0f),
        };

        var messageBox = DialogButton("Message box", () => ShowMessageBox());
        row.Controls.Add(messageBox);
        row.Controls.Add(DialogButton("Open file", () => ShowFileDialog(DialogKind.Open)));
        row.Controls.Add(DialogButton("Save file", () => ShowFileDialog(DialogKind.Save)));
        row.Controls.Add(DialogButton("Pick folder", () => ShowFileDialog(DialogKind.Folder)));

        section.Controls.Add(row);
        return section;
    }

    private static XPlatButton DialogButton(string text, Action onClick)
    {
        var button = new XPlatButton(text)
        {
            Dock = XDock.Left,
            AutoSize = true,
            Style = XPlatButtonStyle.Secondary,
            Margin = new XPlatThickness(0f, 0f, 8f, 0f),
        };
        button.AddClickHandler(_ => onClick());
        return button;
    }

    private void ShowMessageBox()
    {
        if (OwnerWindow is not { } owner)
        {
            return;
        }

        var result = owner.Application.Dialogs.ShowMessage(
            new XPlatMessageBoxOptions
            {
                Message = "This box is drawn by the platform, not by XPlatUI.\n\n" +
                          "It is modal to the window and the sample keeps painting behind it.",
                Title = "XPlatUI sample",
                Buttons = XPlatMessageBoxButtons.YesNoCancel,
                Icon = XPlatMessageBoxIcon.Information,
                DefaultResult = XPlatDialogResult.Yes,
            },
            owner);

        SetStatus($"Message box returned {result}.");
    }

    private void ShowFileDialog(DialogKind kind)
    {
        if (OwnerWindow is not { } owner)
        {
            return;
        }

        var dialogs = owner.Application.Dialogs;

        var options = new XPlatFileDialogOptions
        {
            Title = kind switch
            {
                DialogKind.Open => "Open a file",
                DialogKind.Save => "Save a file",
                _ => "Pick a folder",
            },
            FileName = kind == DialogKind.Save ? "untitled.txt" : null,
            OverwritePrompt = true,
        };

        if (kind != DialogKind.Folder)
        {
            options.Filters.Add(new XPlatFileFilter("Text files", "*.txt", "*.md"));
            options.Filters.Add(new XPlatFileFilter("All files", "*.*"));
        }

        var path = kind switch
        {
            DialogKind.Open => dialogs.OpenFile(options, owner),
            DialogKind.Save => dialogs.SaveFile(options, owner),
            _ => dialogs.PickFolder(options, owner),
        };

        SetStatus(path is null ? "Dialog cancelled." : $"Chose {path}");
    }

    private enum DialogKind
    {
        Open,
        Save,
        Folder,
    }

    /// <summary>
    /// The window the demo is shown in, used to parent the dialogs. It is resolved from the root at
    /// call time rather than from a control on one page: only the page being viewed is attached to
    /// the window, so a control on another page has no <see cref="XPlatControl.Window"/> and would
    /// make a dialog opened from here silently do nothing.
    /// </summary>
    private XPlatWindow? OwnerWindow => Root.Window;

    /// <summary>
    /// Shows a small window owned by the window hosting the sample. Ownership is what makes a
    /// window behave like a child of another: it stays above its owner, minimizes with it, and
    /// closes when the owner closes.
    /// </summary>
    public void ShowOwnedWindow()
    {
        if (OwnerWindow is not { } owner)
        {
            return;
        }

        var panel = new XPlatPanel
        {
            Padding = new XPlatThickness(16f),
        };

        panel.Controls.Add(new XPlatLabel(
            "This window is owned by the main window: it stays above it, minimizes with it, and "
            + "closes when it closes.")
        {
            Dock = XDock.Fill,
            WrapText = true,
            TextAlign = XPlatContentAlignment.MiddleCenter,
        });

        var window = new XPlatWindow("Owned Window", panel)
        {
            ClientSize = new SKSize(440f, 170f),
            MinimumClientSize = new SKSize(300f, 130f),
        };

        window.Show(owner);
        SetStatus("Opened a window owned by this one - it stays above it and closes with it.");
    }

    private XPlatPanel BuildSplitDemo()
    {
        var section = new XPlatPanel
        {
            Dock = XDock.Fill,
            ShowFrame = false,
            IsCard = false,
            BackColor = null,
            BorderWidth = 0f,
            CornerRadius = 0f,
            Padding = new XPlatThickness(0f),
        };

        var split = new XPlatSplitContainer
        {
            Dock = XDock.Fill,
            Orientation = XPlatOrientation.Vertical,
            SplitterDistance = 260f,
            Panel1MinSize = 140f,
            Panel1MaxSize = 420f,
            Panel2MinSize = 180f,
        };
        split.AddSplitterMovedHandler(_ => SetStatus($"Splitter moved: panel 1 is {split.SplitterDistance:0} px wide."));

        split.Panel1.Controls.Add(Heading("Navigation"));
        split.Panel1.Controls.Add(DemoButton("Dashboard", () => SetStatus("Dashboard selected.")));
        split.Panel1.Controls.Add(DemoButton("Reports", () => SetStatus("Reports selected.")));
        split.Panel1.Controls.Add(DemoButton("Settings", () => SetStatus("Settings selected.")));

        var nested = new XPlatSplitContainer
        {
            Dock = XDock.Fill,
            Orientation = XPlatOrientation.Horizontal,
            SplitterDistance = 64f,
            Panel1MinSize = 40f,
            Panel2MinSize = 40f,
        };
        nested.Panel1.Controls.Add(Heading("Panel 1 of a horizontal split container"));
        nested.Panel2.Controls.Add(new XPlatLabel("Drag either splitter - the dashed band - to resize. The second panel always fills the remaining space.")
        {
            Dock = XDock.Fill,
            AutoSize = false,
            WrapText = true,
            Font = XPlatTheme.CaptionFont,
            ForeColorRole = XPlatThemeColor.MutedForeground,
            TextAlign = XPlatContentAlignment.TopLeft,
        });

        split.Panel2.Controls.Add(nested);
        section.Controls.Add(split);

        return section;
    }

    private static XPlatLabel Heading(string text) => new(text)
    {
        Dock = XDock.Top,
        AutoSize = false,
        Height = 26f,
        Font = XPlatTheme.EmphasizedFont,
        TextAlign = XPlatContentAlignment.BottomLeft,
        Margin = new XPlatThickness(0f, 0f, 0f, 8f),
    };

    private static XPlatButton DemoButton(string text, Action onClick)
    {
        var button = new XPlatButton(text)
        {
            Dock = XDock.Top,
            AutoSize = true,
            Style = XPlatButtonStyle.Secondary,
            Margin = new XPlatThickness(0f, 0f, 0f, 8f),
        };
        button.AddClickHandler(_ => onClick());
        return button;
    }

    /// <summary>
    /// One of each sizing style, so the difference between them is visible rather than described:
    /// column 0 is a fixed width, column 1 takes a percentage and column 2 sizes to its content,
    /// and the three rows do the same. The cells are outlined so the tracks can be read off.
    /// </summary>
    private XPlatPanel BuildTableLayoutDemo()
    {
        var table = new XPlatTableLayoutPanel
        {
            Dock = XDock.Fill,
            ShowFrame = false,
            IsCard = false,
            BackColor = null,
            BorderWidth = 0f,
            CornerRadius = 0f,
            Padding = new XPlatThickness(0f),
            CellPadding = new XPlatThickness(8f),
        };

        table.ColumnStyles.Add(new XPlatColumnStyle(XPlatSizeType.Absolute, 160f));
        table.ColumnStyles.Add(new XPlatColumnStyle(XPlatSizeType.Percent, 100f));
        table.ColumnStyles.Add(new XPlatColumnStyle(XPlatSizeType.AutoSize, 0f));

        table.RowStyles.Add(new XPlatRowStyle(XPlatSizeType.AutoSize, 0f));
        table.RowStyles.Add(new XPlatRowStyle(XPlatSizeType.Percent, 100f));
        table.RowStyles.Add(new XPlatRowStyle(XPlatSizeType.Absolute, 90f));

        table.Add(Cell("Absolute column\n160 px"), 0, 0);
        table.Add(Cell("Percent column\nall the space the other two leave"), 1, 0);
        table.Add(Cell("AutoSize column\nfollows its content"), 2, 0);

        table.Add(Cell("AutoSize row\nfollows its content"), 0, 1);
        table.Add(Cell("The percent row and the percent column absorb whatever the fixed and AutoSize tracks leave over, which is why this cell is the one that grows and shrinks with the window."), 1, 1);
        table.Add(Cell("Drag the splitter, or resize the window, and only this column keeps its width."), 2, 1);

        table.Add(Cell("Absolute row\n90 px"), 0, 2);
        table.Add(Cell("Percent row, percent column"), 1, 2);
        table.Add(Cell("AutoSize\nboth ways"), 2, 2);

        return table;
    }

    private static XPlatPanel Cell(string caption)
    {
        var cell = new XPlatPanel
        {
            Dock = XDock.Fill,
            BorderWidth = 1f,
            CornerRadius = 8f,
            Padding = new XPlatThickness(10f),
        };

        cell.Controls.Add(new XPlatLabel(caption)
        {
            Dock = XDock.Fill,
            AutoSize = false,
            WrapText = true,
            Font = XPlatTheme.CaptionFont,
            ForeColorRole = XPlatThemeColor.MutedForeground,
            TextAlign = XPlatContentAlignment.TopLeft,
        });

        return cell;
    }

    /// <summary>
    /// One control along one axis: children stretched across the stack by default, the three
    /// alignments that opt out of it, the same control turned sideways, and a stack that scrolls
    /// its own run with a docked header that stays put.
    /// </summary>
    private XPlatPanel BuildStackPanelDemo()
    {
        var card = Card();

        card.Controls.Add(ScrollCaption("Vertical - each child takes its own height and the width of the stack"));
        card.Controls.Add(BuildVerticalStack());

        card.Controls.Add(ScrollCaption("Across the stack - HorizontalAlignment on each child"));
        card.Controls.Add(BuildAlignedStack());

        card.Controls.Add(ScrollCaption("Horizontal - the same control with Orientation = Horizontal"));
        card.Controls.Add(BuildHorizontalStack());

        card.Controls.Add(ScrollCaption("VerticalScroll = Automatic - the run of children is the scrollable content"));
        card.Controls.Add(BuildScrollingStack());

        return card;
    }

    /// <summary>
    /// The framing the stacks on this page share: a bordered box, so the bounds the stack arranges
    /// its children in are visible.
    /// </summary>
    private static XPlatStackPanel StackBox() => new()
    {
        Dock = XDock.Top,
        ShowFrame = true,
        IsCard = true,
        CornerRadius = 8f,
        Padding = new XPlatThickness(8f),
        Spacing = 6f,
        Margin = new XPlatThickness(0f, 0f, 0f, 10f),
    };

    private XPlatStackPanel BuildVerticalStack()
    {
        var stack = StackBox();
        stack.Height = 130f;

        foreach (var text in new[] { "First", "Second", "Third" })
        {
            stack.Controls.Add(StackButton(text, XPlatAlignment.Stretch));
        }

        return stack;
    }

    private XPlatStackPanel BuildAlignedStack()
    {
        var stack = StackBox();
        stack.Height = 170f;

        stack.Controls.Add(StackButton("Near", XPlatAlignment.Near));
        stack.Controls.Add(StackButton("Center", XPlatAlignment.Center));
        stack.Controls.Add(StackButton("Far", XPlatAlignment.Far));
        stack.Controls.Add(StackButton("Stretch", XPlatAlignment.Stretch));

        return stack;
    }

    private XPlatStackPanel BuildHorizontalStack()
    {
        var stack = StackBox();
        stack.Orientation = XPlatStackOrientation.Horizontal;
        stack.Height = 60f;

        // A horizontal stack stretches its children down the stack, so the interesting property is
        // VerticalAlignment.
        foreach (var (text, alignment) in new[]
                 {
                     ("Top", XPlatAlignment.Near),
                     ("Middle", XPlatAlignment.Center),
                     ("Bottom", XPlatAlignment.Far),
                     ("Fill", XPlatAlignment.Stretch),
                 })
        {
            stack.Controls.Add(StackButton(text, alignment));
        }

        return stack;
    }

    private XPlatStackPanel BuildScrollingStack()
    {
        var stack = StackBox();
        stack.Dock = XDock.Fill;
        stack.VerticalScroll = XPlatScrollStyle.Automatic;
        stack.Spacing = 4f;

        // A docked child is not part of the run: it keeps the top of the panel while the rows
        // scroll underneath it.
        stack.Controls.Add(new XPlatLabel("Docked header - it stays put while the rows scroll")
        {
            Dock = XDock.Top,
            AutoSize = false,
            Height = 20f,
            Font = XPlatTheme.CaptionFont,
            ForeColorRole = XPlatThemeColor.MutedForeground,
            TextAlign = XPlatContentAlignment.MiddleLeft,
        });

        for (int index = 0; index < 40; index++)
        {
            var row = StackButton($"Row {index + 1}", XPlatAlignment.Stretch);
            row.AutoSize = false;
            row.Height = 28f;
            stack.Controls.Add(row);
        }

        return stack;
    }

    private XPlatButton StackButton(string text, XPlatAlignment alignment)
    {
        var button = new XPlatButton(text)
        {
            AutoSize = true,
            Style = XPlatButtonStyle.Secondary,
            HorizontalAlignment = alignment,
            VerticalAlignment = alignment,
        };
        button.AddClickHandler(_ => SetStatus($"{text} clicked - placed {alignment} across the stack."));
        return button;
    }

    private static XPlatPanel Card() => new()
    {
        Dock = XDock.Fill,
        BorderWidth = 1f,
        CornerRadius = 10f,
        Padding = new XPlatThickness(16f),
    };

    private XPlatPanel BuildLabelsCard()
    {
        var card = Card();

        card.Controls.Add(new XPlatLabel("Label")
        {
            Dock = XDock.Top,
            AutoSize = false,
            Height = 26f,
            Font = XPlatTheme.EmphasizedFont,
            TextAlign = XPlatContentAlignment.BottomLeft,
            Margin = new XPlatThickness(0f, 0f, 0f, 6f),
        });

        card.Controls.Add(new XPlatLabel("Labels wrap long text automatically, honour nine-way alignment and share the theme fonts.")
        {
            Dock = XDock.Top,
            AutoSize = false,
            Height = 72f,
            WrapText = true,
            Font = XPlatTheme.DefaultFont,
            ForeColorRole = XPlatThemeColor.MutedForeground,
            TextAlign = XPlatContentAlignment.TopLeft,
            Margin = new XPlatThickness(0f, 0f, 0f, 10f),
            Selectable = true,
        });

        _counterLabel = new XPlatLabel("Button clicks: 0")
        {
            Dock = XDock.Top,
            AutoSize = false,
            Height = 22f,
            Font = XPlatTheme.EmphasizedFont,
            ForeColorRole = XPlatThemeColor.Accent,
            TextAlign = XPlatContentAlignment.MiddleLeft,
            Margin = new XPlatThickness(0f, 0f, 0f, 6f),
            Selectable = true,
        };
        card.Controls.Add(_counterLabel);

        card.Controls.Add(new XPlatLabel("Multi-line text\nis supported too.")
        {
            Dock = XDock.Top,
            AutoSize = false,
            Height = 40f,
            Font = XPlatTheme.CaptionFont,
            ForeColorRole = XPlatThemeColor.MutedForeground,
            TextAlign = XPlatContentAlignment.TopLeft,
            Selectable = true,
        });

        return card;
    }

    private XPlatPanel BuildRichTextCard()
    {
        var card = Card();

        card.Controls.Add(new XPlatLabel("Rich label")
        {
            Dock = XDock.Top,
            AutoSize = false,
            Height = 26f,
            Font = XPlatTheme.EmphasizedFont,
            TextAlign = XPlatContentAlignment.BottomLeft,
            Margin = new XPlatThickness(0f, 0f, 0f, 6f),
        });

        var rich = new XPlatRichLabel
        {
            Dock = XDock.Top,
            AutoSize = true,
            WrapText = true,
            Font = XPlatTheme.DefaultFont,
            Selectable = true,
        };

        rich.AddText("One flow of ");
        rich.AddSpan(new XPlatTextSpan("bold") { Bold = true });
        rich.AddText(", ");
        rich.AddSpan(new XPlatTextSpan("italic") { Italic = true });
        rich.AddText(", ");
        rich.AddSpan(new XPlatTextSpan("underline test") { Underline = true });
        rich.AddText(", ");
        rich.AddSpan(new XPlatTextSpan("colour") { ThemeColor = XPlatThemeColor.Accent });
        rich.AddText(" and ");
        rich.AddSpan(new XPlatTextSpan("struck") { Strikethrough = true });
        rich.AddText(" text, plus a ");

        var link = rich.AddLink("clickable link", span => SetStatus($"Link span clicked: \"{span.Text}\" (tag: {span.Tag ?? "none"})."));
        link.Tag = "demo";
        link.AddMouseEnterHandler(_ => SetStatus("Pointer entered a link span - spans receive mouse events."));
        link.AddMouseLeaveHandler(_ => SetStatus("Pointer left the link span."));

        rich.AddText(". Only the glyphs of a span are hit-tested.");
        rich.AddText("\nLive chips in the flow, aligned per line: ");
        rich.AddInlineControl(Chip("top"), XPlatInlineAlignment.Top);
        rich.AddText(" ");
        rich.AddInlineControl(Chip("middle"), XPlatInlineAlignment.Center);
        rich.AddText(" ");
        rich.AddInlineControl(Chip("bottom"), XPlatInlineAlignment.Bottom);

        // Shown on right-click while nothing is selected; once a range is selected the host swaps
        // in the Copy menu instead, so both paths are reachable from this one control.
        rich.ContextMenu = BuildRichTextMenu();

        card.Controls.Add(rich);
        return card;
    }

    private XPlatContextMenu BuildRichTextMenu()
    {
        var formatting = new XPlatMenuItem("Show formatting", () =>
            SetStatus("'Show formatting' picked - a checked item shows a tick."))
        {
            Checked = true,
            Shortcut = new XPlatShortcut(XPlatKey.F, XPlatModifiers.Control | XPlatModifiers.Shift),
        };

        var menu = new XPlatContextMenu();

        menu.Add(new XPlatMenuItem("Copy rich text", () => SetStatus("'Copy rich text' picked."))
        {
            IsDefault = true,
            Shortcut = new XPlatShortcut(XPlatKey.C),
        });

        menu.Add(new XPlatMenuItem("Copy as plain text", () => SetStatus("'Copy as plain text' picked."))
        {
            Shortcut = new XPlatShortcut(XPlatKey.C, XPlatModifiers.Control | XPlatModifiers.Shift),
        });

        menu.AddSeparator();
        menu.Add(formatting);
        menu.Add(new XPlatMenuItem("Undo formatting", null) { Enabled = false, Shortcut = new XPlatShortcut(XPlatKey.Z) });
        return menu;
    }

    private XPlatButton Chip(string text)
    {
        var chip = new XPlatButton(text)
        {
            Style = XPlatButtonStyle.Secondary,
            AutoSize = true,
            Font = XPlatTheme.CaptionFont,
            Padding = new XPlatThickness(8f, 4f),
            MinimumSize = new SKSize(0f, 26f),
            CornerRadius = 13f,
        };
        chip.AddClickHandler(_ => SetStatus($"Inline chip '{text}' clicked."));
        return chip;
    }

    private XPlatPanel BuildSelectionCard()
    {
        var card = Card();

        card.Controls.Add(new XPlatLabel("Selection")
        {
            Dock = XDock.Top,
            AutoSize = false,
            Height = 24f,
            Font = XPlatTheme.EmphasizedFont,
            TextAlign = XPlatContentAlignment.BottomLeft,
            Margin = new XPlatThickness(0f, 0f, 0f, 4f),
        });

        // A drag starts on the label, passes over the whole-control button and ends in the text
        // box, so one gesture produces a partial, a whole and another partial range.
        card.Controls.Add(new XPlatLabel("Drag down from this label to start a selection.")
        {
            Dock = XDock.Top,
            AutoSize = false,
            Height = 40f,
            WrapText = true,
            Font = XPlatTheme.DefaultFont,
            ForeColorRole = XPlatThemeColor.MutedForeground,
            TextAlign = XPlatContentAlignment.TopLeft,
            Margin = new XPlatThickness(0f, 0f, 0f, 6f),
            Selectable = true,
        });

        var whole = new XPlatButton("Whole control")
        {
            Dock = XDock.Top,
            AutoSize = true,
            Style = XPlatButtonStyle.Secondary,
            Margin = new XPlatThickness(0f, 0f, 0f, 6f),
            Selectable = true,
        };
        whole.AddClickHandler(_ => SetStatus("Whole-control button clicked - a drag over it selects the button as one unit."));
        card.Controls.Add(whole);

        card.Controls.Add(new XPlatTextBox
        {
            Dock = XDock.Top,
            Height = 32f,
            Text = "...and finish in this box.",
            Margin = new XPlatThickness(0f, 0f, 0f, 6f),
        });

        card.Controls.Add(new XPlatLabel("Right-click the selection for the native Copy menu.")
        {
            Dock = XDock.Fill,
            AutoSize = false,
            WrapText = true,
            Font = XPlatTheme.CaptionFont,
            ForeColorRole = XPlatThemeColor.MutedForeground,
            TextAlign = XPlatContentAlignment.TopLeft,
        });

        return card;
    }

    /// <summary>
    /// Check boxes on the left, radio buttons on the right: the checks cover the three states and
    /// the disabled look, and the radios show two groups on one card separated only by
    /// <see cref="XPlatRadioButton.GroupName"/> rather than by a container each.
    /// </summary>
    private XPlatPanel BuildToggleDemo()
    {
        var section = new XPlatPanel
        {
            Dock = XDock.Fill,
            ShowFrame = false,
            IsCard = false,
            BackColor = null,
            BorderWidth = 0f,
            CornerRadius = 0f,
            Padding = new XPlatThickness(0f),
        };

        var checks = Card();
        checks.Dock = XDock.Left;
        checks.Width = 320f;
        checks.Margin = new XPlatThickness(0f, 0f, 16f, 0f);
        checks.Controls.Add(Heading("Check boxes"));

        foreach (string caption in new[] { "Auto-save", "Show line numbers", "Wrap long lines" })
        {
            var box = new XPlatCheckBox(caption)
            {
                Dock = XDock.Top,
                Margin = new XPlatThickness(0f, 0f, 0f, 6f),
            };
            box.AddCheckedChangedHandler(_ => SetStatus($"{box.Text} is {Describe(box.CheckState)}."));
            checks.Controls.Add(box);
        }

        // ThreeState is set before CheckState because the initializer runs in order: a check box
        // that cannot hold the indeterminate state would drop it on the way in.
        var threeState = new XPlatCheckBox("Three state")
        {
            Dock = XDock.Top,
            ThreeState = true,
            CheckState = XPlatCheckState.Indeterminate,
            Margin = new XPlatThickness(0f, 12f, 0f, 2f),
        };
        checks.Controls.Add(threeState);

        var stateReadout = ScrollCaption("Indeterminate");
        checks.Controls.Add(stateReadout);
        threeState.AddCheckedChangedHandler(_ => stateReadout.Text = Describe(threeState.CheckState));

        checks.Controls.Add(new XPlatCheckBox("Disabled, checked")
        {
            Dock = XDock.Top,
            Checked = true,
            Enabled = false,
            Margin = new XPlatThickness(0f, 12f, 0f, 0f),
        });

        var radios = Card();
        radios.Controls.Add(Heading("Radio buttons"));
        radios.Controls.Add(new XPlatLabel("Both groups are on this card: GroupName decides which radio buttons answer to each other.")
        {
            Dock = XDock.Top,
            AutoSize = false,
            Height = 34f,
            WrapText = true,
            Font = XPlatTheme.CaptionFont,
            ForeColorRole = XPlatThemeColor.MutedForeground,
            TextAlign = XPlatContentAlignment.TopLeft,
            Margin = new XPlatThickness(0f, 0f, 0f, 6f),
        });

        AddRadioGroup(radios, "Size", "size", ["Small", "Medium", "Large"], "Medium");
        AddRadioGroup(radios, "Alignment", "align", ["Left", "Centre", "Right"], "Left");

        radios.Controls.Add(new XPlatRadioButton("Disabled")
        {
            Dock = XDock.Top,
            Enabled = false,
            Margin = new XPlatThickness(0f, 12f, 0f, 0f),
        });

        section.Controls.Add(checks);
        section.Controls.Add(radios);
        return section;
    }

    /// <summary>Adds a captioned run of radio buttons that form one group by name.</summary>
    private void AddRadioGroup(XPlatPanel card, string caption, string groupName, string[] options, string selected)
    {
        var heading = ScrollCaption(caption);
        heading.Margin = new XPlatThickness(0f, 10f, 0f, 2f);
        card.Controls.Add(heading);

        foreach (string option in options)
        {
            var radio = new XPlatRadioButton(option)
            {
                Dock = XDock.Top,
                GroupName = groupName,
                Checked = option == selected,
                Margin = new XPlatThickness(0f, 0f, 0f, 4f),
            };
            radio.AddCheckedChangedHandler(_ =>
            {
                if (radio.Checked)
                {
                    SetStatus($"{caption} is {option}.");
                }
            });
            card.Controls.Add(radio);
        }
    }

    private static string Describe(XPlatCheckState state) => state switch
    {
        XPlatCheckState.Checked => "checked",
        XPlatCheckState.Indeterminate => "indeterminate",
        _ => "unchecked",
    };

    /// <summary>
    /// The three <see cref="XPlatProgressBarStyle"/>s, a disabled bar, and a Step button, so the
    /// fill is watched rather than described. Only the marquee bar animates, and only while the
    /// window is running a frame loop to tick it.
    /// </summary>
    private XPlatPanel BuildProgressDemo()
    {
        var card = Card();
        card.Controls.Add(Heading("Progress bars"));

        var readout = ScrollCaption("35%");
        card.Controls.Add(readout);

        var continuous = new XPlatProgressBar
        {
            Dock = XDock.Top,
            Height = 18f,
            Value = 35f,
            Margin = new XPlatThickness(0f, 0f, 0f, 16f),
        };
        card.Controls.Add(continuous);

        card.Controls.Add(ScrollCaption("Blocks"));
        card.Controls.Add(new XPlatProgressBar
        {
            Dock = XDock.Top,
            Height = 18f,
            Value = 60f,
            Style = XPlatProgressBarStyle.Blocks,
            Margin = new XPlatThickness(0f, 0f, 0f, 16f),
        });

        card.Controls.Add(ScrollCaption("Marquee (no speed set, so it follows the display)"));
        card.Controls.Add(new XPlatProgressBar
        {
            Dock = XDock.Top,
            Height = 18f,
            Style = XPlatProgressBarStyle.Marquee,
            Margin = new XPlatThickness(0f, 0f, 0f, 16f),
        });

        card.Controls.Add(ScrollCaption("Disabled"));
        card.Controls.Add(new XPlatProgressBar
        {
            Dock = XDock.Top,
            Height = 18f,
            Value = 70f,
            Enabled = false,
            Margin = new XPlatThickness(0f, 0f, 0f, 16f),
        });

        var buttons = new XPlatPanel
        {
            Dock = XDock.Top,
            Height = 32f,
            ShowFrame = false,
            IsCard = false,
            BackColor = null,
            BorderWidth = 0f,
            CornerRadius = 0f,
            Padding = new XPlatThickness(0f),
            Margin = new XPlatThickness(0f, 0f, 0f, 12f),
        };

        var step = new XPlatButton("Step")
        {
            Dock = XDock.Left,
            AutoSize = true,
            Style = XPlatButtonStyle.Secondary,
            Margin = new XPlatThickness(0f, 0f, 8f, 0f),
        };
        step.AddClickHandler(_ =>
        {
            if (continuous.Value >= continuous.Maximum)
            {
                continuous.Value = continuous.Minimum;
            }

            continuous.PerformStep();
            readout.Text = $"{continuous.Value:0}%";
            SetStatus($"Continuous bar is at {continuous.Value:0} of {continuous.Maximum:0}.");
        });
        buttons.Controls.Add(step);

        var reset = new XPlatButton("Reset")
        {
            Dock = XDock.Left,
            AutoSize = true,
            Style = XPlatButtonStyle.Secondary,
        };
        reset.AddClickHandler(_ =>
        {
            continuous.Value = 0f;
            readout.Text = "0%";
            SetStatus("Continuous bar reset.");
        });
        buttons.Controls.Add(reset);

        card.Controls.Add(buttons);
        card.Controls.Add(new XPlatLabel("Value is clamped to Minimum..Maximum, so PerformStep wraps only because this page resets it at the top.")
        {
            Dock = XDock.Top,
            AutoSize = false,
            Height = 34f,
            WrapText = true,
            Font = XPlatTheme.CaptionFont,
            ForeColorRole = XPlatThemeColor.MutedForeground,
            TextAlign = XPlatContentAlignment.TopLeft,
        });

        return card;
    }

    /// <summary>
    /// <see cref="XPlatNumericUpDown"/>: the spinner is the whole point, so the fields on this page
    /// cover a plain integer, a money amount with two decimals and a coarse step, a percentage
    /// whose increment is larger than one, and a disabled field for comparison.
    /// </summary>
    private XPlatPanel BuildNumericUpDownDemo()
    {
        var card = Card();
        card.Controls.Add(Heading("Numeric up-down"));

        var readout = ScrollCaption("Quantity 3 x 12.50 = 37.50");
        card.Controls.Add(readout);

        var quantity = new XPlatNumericUpDown
        {
            Minimum = 1m,
            Maximum = 99m,
            Value = 3m,
        };

        var price = new XPlatNumericUpDown
        {
            Minimum = 0m,
            Maximum = 9999.99m,
            DecimalPlaces = 2,
            Increment = 0.05m,
            Value = 12.5m,
        };

        quantity.AddValueChangedHandler(_ => UpdateTotal());
        price.AddValueChangedHandler(_ => UpdateTotal());

        void UpdateTotal() => readout.Text =
            $"Quantity {quantity.Value:0} x {price.Value:0.00} = {quantity.Value * price.Value:0.00}";

        card.Controls.Add(NumericRow("Quantity (1-99, step 1)", quantity));
        card.Controls.Add(NumericRow("Unit price (0-9999.99, step 0.05)", price));

        card.Controls.Add(NumericRow("Percentage (step 5, typed value snaps on Enter)", new XPlatNumericUpDown
        {
            Minimum = 0m,
            Maximum = 100m,
            Increment = 5m,
            Value = 40m,
        }));

        card.Controls.Add(NumericRow("Large value (two decimals, step 0.01)", new XPlatNumericUpDown
        {
            Minimum = 0m,
            Maximum = 999999.99m,
            DecimalPlaces = 2,
            Increment = 0.01m,
            Value = 987654.32m,
        }));

        card.Controls.Add(NumericRow("Disabled", new XPlatNumericUpDown
        {
            Minimum = 0m,
            Maximum = 10m,
            Value = 5m,
            Enabled = false,
        }));

        card.Controls.Add(new XPlatLabel(
            "Arrow keys step by Increment, Page Up/Down by ten times that, the wheel steps one detent per notch, " +
            "and a held spinner button repeats after a short delay. Typing is free-form: the value follows the text, " +
            "and the text is rewritten from the value on Enter or when the focus leaves.")
        {
            Dock = XDock.Top,
            AutoSize = false,
            Height = 62f,
            WrapText = true,
            Font = XPlatTheme.CaptionFont,
            ForeColorRole = XPlatThemeColor.MutedForeground,
            TextAlign = XPlatContentAlignment.TopLeft,
            Margin = new XPlatThickness(0f, 6f, 0f, 0f),
        });

        return card;
    }

    /// <summary>A caption with a fixed-width field on the right, so the fields line up.</summary>
    private static XPlatPanel NumericRow(string caption, XPlatControl field)
    {
        var row = new XPlatPanel
        {
            Dock = XDock.Top,
            Height = 34f,
            ShowFrame = false,
            IsCard = false,
            BackColor = null,
            BorderWidth = 0f,
            CornerRadius = 0f,
            Padding = new XPlatThickness(0f),
            Margin = new XPlatThickness(0f, 0f, 0f, 10f),
        };

        row.Controls.Add(new XPlatLabel(caption)
        {
            Dock = XDock.Fill,
            AutoSize = false,
            Font = XPlatTheme.DefaultFont,
            TextAlign = XPlatContentAlignment.MiddleLeft,
            Margin = new XPlatThickness(0f, 0f, 12f, 0f),
        });

        field.Dock = XDock.Right;
        field.Width = 170f;
        row.Controls.Add(field);

        return row;
    }

    /// <summary>
    /// <see cref="XPlatTrackBar"/>: a horizontal bar whose value drives a vertical one, so the two
    /// orientations can be compared against the same number, plus a bar with no ticks and a
    /// disabled one.
    /// </summary>
    private XPlatPanel BuildTrackBarDemo()
    {
        var card = Card();
        card.Controls.Add(Heading("Track bars"));

        var readout = ScrollCaption("Value 30 of 100");
        card.Controls.Add(readout);

        var horizontal = new XPlatTrackBar
        {
            Dock = XDock.Top,
            Height = 28f,
            Maximum = 100f,
            Value = 30f,
            SmallChange = 1f,
            LargeChange = 10f,
            TickFrequency = 10f,
            Margin = new XPlatThickness(0f, 0f, 0f, 12f),
        };

        var vertical = new XPlatTrackBar
        {
            Dock = XDock.Left,
            Width = 44f,
            Orientation = XPlatOrientation.Vertical,
            Maximum = 100f,
            Value = 30f,
            SmallChange = 1f,
            LargeChange = 10f,
            TickFrequency = 10f,
            Margin = new XPlatThickness(0f, 0f, 16f, 0f),
        };

        horizontal.AddValueChangedHandler(_ =>
        {
            vertical.Value = horizontal.Value;
            readout.Text = $"Value {horizontal.Value:0} of 100";
        });

        card.Controls.Add(horizontal);

        var row = new XPlatPanel
        {
            Dock = XDock.Top,
            Height = 150f,
            ShowFrame = false,
            IsCard = false,
            BackColor = null,
            BorderWidth = 0f,
            CornerRadius = 0f,
            Padding = new XPlatThickness(0f),
            Margin = new XPlatThickness(0f, 0f, 0f, 12f),
        };

        row.Controls.Add(new XPlatLabel("The same value, drawn vertically: the knob sits at the bottom for Minimum, so dragging up raises the value.")
        {
            Dock = XDock.Fill,
            AutoSize = false,
            WrapText = true,
            Font = XPlatTheme.CaptionFont,
            ForeColorRole = XPlatThemeColor.MutedForeground,
            TextAlign = XPlatContentAlignment.TopLeft,
        });
        row.Controls.Add(vertical);
        card.Controls.Add(row);

        card.Controls.Add(ScrollCaption("No tick marks (TickFrequency 0), fine steps"));

        var fine = new XPlatTrackBar
        {
            Dock = XDock.Top,
            Height = 28f,
            Minimum = -50f,
            Maximum = 50f,
            Value = 0f,
            SmallChange = 0.5f,
            LargeChange = 5f,
            TickFrequency = 0f,
            Margin = new XPlatThickness(0f, 0f, 0f, 12f),
        };

        var fineReadout = ScrollCaption("0.0");
        fine.AddValueChangedHandler(_ => fineReadout.Text = $"{fine.Value:0.0}");
        card.Controls.Add(fine);
        card.Controls.Add(fineReadout);

        card.Controls.Add(ScrollCaption("Disabled"));

        card.Controls.Add(new XPlatTrackBar
        {
            Dock = XDock.Top,
            Height = 28f,
            Maximum = 100f,
            Value = 65f,
            TickFrequency = 25f,
            Enabled = false,
            Margin = new XPlatThickness(0f, 0f, 0f, 12f),
        });

        card.Controls.Add(new XPlatLabel(
            "Pressing the knob starts a drag and the value follows the pointer; pressing the track pages by " +
            "LargeChange toward the press. Arrow keys step by SmallChange, Page Up/Down by LargeChange, " +
            "Home and End jump to the ends, and the wheel steps one SmallChange per detent.")
        {
            Dock = XDock.Top,
            AutoSize = false,
            Height = 62f,
            WrapText = true,
            Font = XPlatTheme.CaptionFont,
            ForeColorRole = XPlatThemeColor.MutedForeground,
            TextAlign = XPlatContentAlignment.TopLeft,
        });

        return card;
    }

    /// <summary>
    /// The in-library menus. The bar across the top of the window is drawn by the library on
    /// Windows and Linux and reachable with Alt plus a mnemonic; this page adds a context menu that
    /// uses every row shape the renderer knows - icon, check mark, submenu, shortcut hint, disabled
    /// entry and a hosted control.
    /// </summary>
    private XPlatPanel BuildMenuDemo()
    {
        var card = Card();
        card.Controls.Add(Heading("Menus"));

        var readout = ScrollCaption(
            "Alt+V and Alt+W open the window's own bar. Right-click the box for a context menu.");
        readout.Margin = new XPlatThickness(0f, 0f, 0f, 12f);
        card.Controls.Add(readout);

        var target = new XPlatPanel
        {
            Dock = XDock.Top,
            Height = 96f,
            BorderWidth = 1f,
            CornerRadius = 8f,
            Margin = new XPlatThickness(0f, 0f, 0f, 12f),
        };

        target.Controls.Add(new XPlatLabel("Right-click anywhere in this box")
        {
            Dock = XDock.Fill,
            AutoSize = false,
            TextAlign = XPlatContentAlignment.MiddleCenter,
            ForeColorRole = XPlatThemeColor.MutedForeground,
            Font = XPlatTheme.CaptionFont,
        });

        target.ContextMenu = BuildMenuDemoContextMenu(readout);
        _menuDemoTarget = target;
        card.Controls.Add(target);

        // The bar belongs to the window rather than to a control, so opening one of its menus is a
        // host-level call - which is also how Alt plus a mnemonic gets there.
        XPlatButton? openView = null;
        openView = DemoButton("Open the View menu", () =>
        {
            var view = openView?.Host?.MenuBar;
            if (view?.MenuBar is not { } bar || bar.Menus.Count == 0)
            {
                readout.Text = "This backend has no in-window menu bar - it uses the native one.";
                return;
            }

            view.OpenMenu(0);
        });

        card.Controls.Add(openView);

        card.Controls.Add(new XPlatLabel(
            "The bar is an ordinary control in the window's own tree, so it scrolls, resizes and repaints " +
            "with everything else; a drop-down is a popup surface that holds an XPlatMenuView. Mnemonics come " +
            "from an & in the text, submenus open to the side of their parent row and nest as deeply as the " +
            "application likes - Recent > Program.cs is two of them - and a menu taller than the room it has " +
            "scrolls its rows behind arrows at either end. A row may host a control instead of a label: the " +
            "zoom slider is an XPlatTrackBar living inside the menu.")
        {
            Dock = XDock.Top,
            AutoSize = false,
            Height = 92f,
            WrapText = true,
            Font = XPlatTheme.CaptionFont,
            ForeColorRole = XPlatThemeColor.MutedForeground,
            TextAlign = XPlatContentAlignment.TopLeft,
        });

        return card;
    }

    /// <summary>
    /// Opens the menus page's context menu as if the box had been right-clicked, which is what a
    /// screenshot of the page needs in order to show a menu at all.
    /// </summary>
    /// <param name="window">The window the sample is showing in.</param>
    public void OpenMenuDemo(XPlatWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);

        if (_menuDemoTarget?.ContextMenu is not { } menu)
        {
            return;
        }

        // Nothing has painted yet, so the page has no bounds until something lays it out.
        window.Host.PerformLayout();

        var location = SKPoint.Empty;
        for (var control = _menuDemoTarget; control is not null; control = control.Parent)
        {
            location.Offset(control.Bounds.Left, control.Bounds.Top);
        }

        // Shown through the presenter rather than through the menu itself, because a screenshot
        // wants the row under the pointer and its submenu too, and only the view can open those.
        var view = new XPlatMenuView(menu);
        XPlatMenuPresenter.ShowMenu(window.Host, view, new SKPoint(location.X + 48f, location.Y + 24f));

        // Follow the first row that opens a submenu all the way down, so the picture shows the
        // whole chain rather than just the level the presenter opened.
        for (int depth = 0, limit = 4; depth < limit; depth++)
        {
            var level = view.DeepestMenu;
            int index = -1;
            for (int i = 0; i < level.Items.Count && index < 0; i++)
            {
                index = level.Items[i].HasSubMenu ? i : -1;
            }

            if (index < 0)
            {
                break;
            }

            level.Highlight(index);
            if (!level.OpenSubMenu(index))
            {
                break;
            }
        }
    }

    /// <summary>
    /// The context menu the menus page shows: one row of every shape the renderer supports, so a
    /// screenshot of the open menu covers the whole of it, plus a submenu inside a submenu and one
    /// menu with more rows than any display can show at once.
    /// </summary>
    private XPlatContextMenu BuildMenuDemoContextMenu(XPlatLabel readout)
    {
        var grid = new XPlatMenuItem("Show &grid", () =>
            readout.Text = "'Show grid' picked - the tick is drawn in the check column.")
        {
            Checked = true,
        };

        var recent = new XPlatMenuItem("&Recent");

        // A submenu inside a submenu, so the chain is exercised at a depth a single level cannot
        // reach: Recent > Program.cs > a place in the file.
        var program = new XPlatMenuItem("&Program.cs");
        foreach (var place in new[] { "The top of the file", "Main", "RenderSample" })
        {
            var spot = place;
            program.SubItems.Add(new XPlatMenuItem(
                spot, () => readout.Text = $"'Recent > Program.cs > {spot}' picked."));
        }

        recent.SubItems.Add(program);

        foreach (var name in new[] { "DemoUi.cs", "Program.cs (older)" })
        {
            var file = name;
            recent.SubItems.Add(new XPlatMenuItem(file, () => readout.Text = $"'Recent > {file}' picked."));
        }

        // More rows than fit on any display, so the menu keeps the height it is given and scrolls
        // its rows instead of growing past the edge - which is what the arrows at either end, the
        // wheel and the keyboard keeping the highlight in view are for.
        var scrolling = new XPlatMenuItem("&Scroll test");
        for (int number = 1; number <= 320; number++)
        {
            int picked = number;
            scrolling.SubItems.Add(new XPlatMenuItem(
                $"Item {picked:000}",
                () => readout.Text = $"'Scroll test > Item {picked:000}' picked."));
        }

        var zoom = new XPlatTrackBar
        {
            Minimum = 50f,
            Maximum = 200f,
            Value = 100f,
            TickFrequency = 25f,
            Margin = new XPlatThickness(0f),
        };

        zoom.AddValueChangedHandler(_ =>
            readout.Text = $"Zoom is {zoom.Value:0}% - a control hosted in a menu row.");

        return new XPlatContextMenu()
            .Add(new XPlatMenuItem("&New file", () => readout.Text = "'New file' picked.")
            {
                Image = MenuGlyph((canvas, paint) => canvas.DrawRoundRect(SKRect.Create(7f, 4f, 18f, 24f), 3f, 3f, paint)),
                Shortcut = new XPlatShortcut(XPlatKey.N, XPlatModifiers.Control),
            })
            .Add(new XPlatMenuItem("&Open...", () => readout.Text = "'Open' picked.")
            {
                Image = MenuGlyph((canvas, paint) =>
                {
                    paint.Style = SKPaintStyle.Stroke;
                    paint.StrokeWidth = 2.5f;
                    canvas.DrawRect(SKRect.Create(5f, 10f, 22f, 16f), paint);
                    canvas.DrawLine(5f, 10f, 11f, 5f, paint);
                    canvas.DrawLine(11f, 5f, 16f, 10f, paint);
                }),
                Shortcut = new XPlatShortcut(XPlatKey.O, XPlatModifiers.Control),
            })
            .AddSeparator()
            .Add(recent)
            .Add(scrolling)
            .Add(grid)
            .AddSeparator()
            .Add(new XPlatMenuItem("Zoom") { Control = zoom })
            .AddSeparator()
            .Add(new XPlatMenuItem("&Delete", null)
            {
                Enabled = false,
                Shortcut = new XPlatShortcut(XPlatKey.Delete),
            });
    }

    /// <summary>
    /// A monochrome glyph for a menu row, drawn in code so the sample needs no image files. The
    /// renderer fits whatever it is given into the icon column, so the source only has to be large
    /// enough to stay crisp.
    /// </summary>
    private static SKImage MenuGlyph(Action<SKCanvas, SKPaint> draw)
    {
        using var surface = SKSurface.Create(new SKImageInfo(32, 32, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var paint = new SKPaint { IsAntialias = true, Color = XPlatTheme.Foreground };
        draw(surface.Canvas, paint);
        surface.Canvas.Flush();
        return surface.Snapshot();
    }

    /// <summary>
    /// <see cref="XPlatLinkLabel"/>: two links that hand their URI to the desktop, one that
    /// navigates in the application instead, a sentence with a link inside it, and a disabled
    /// link - which greys out rather than staying the accent colour.
    /// </summary>
    private XPlatPanel BuildLinkLabelDemo()
    {
        var card = Card();
        card.Controls.Add(Heading("Link labels"));

        var readout = ScrollCaption("Click a link, or Tab to one and press Enter.");
        card.Controls.Add(readout);

        var home = new XPlatLinkLabel("Project home", "https://github.com/drysart/XPlatUI")
        {
            Dock = XDock.Top,
            Margin = new XPlatThickness(0f, 0f, 0f, 10f),
        };

        home.AddLinkClickedHandler((_, e) =>
            readout.Text = e.Opened ? $"Handed {e.Url} to the desktop" : $"Could not open {e.Url}");

        var issue = new XPlatLinkLabel("Report an issue", "https://github.com/drysart/XPlatUI/issues")
        {
            Dock = XDock.Top,
            Margin = new XPlatThickness(0f, 0f, 0f, 10f),
        };

        issue.AddLinkClickedHandler((_, e) =>
            readout.Text = e.Opened ? $"Handed {e.Url} to the desktop" : $"Could not open {e.Url}");

        // No Url at all: the label is only a click target, and the handler decides what happens.
        var inApp = new XPlatLinkLabel("What's new (navigates inside the app)")
        {
            Dock = XDock.Top,
            AutoLaunch = false,
            Margin = new XPlatThickness(0f, 0f, 0f, 16f),
        };

        inApp.AddLinkClickedHandler((_, e) => readout.Text = $"In-app navigation from \"{e.Span?.Text}\"");

        // A sentence with a link inside it: the label is a rich label, so the link is just one span
        // among the plain ones around it.
        var sentence = new XPlatLinkLabel
        {
            Dock = XDock.Top,
            AutoSize = false,
            Height = 44f,
            WrapText = true,
            AutoLaunch = false,
            Margin = new XPlatThickness(0f, 0f, 0f, 16f),
        };

        sentence.AddText("Every control in this library draws itself with Skia and owns no native handle, so ");
        sentence.AddLink("the whole set", span => readout.Text = $"Span \"{span.Text}\" clicked");
        sentence.AddText(" works the same way on Windows, macOS and Linux.");

        card.Controls.Add(home);
        card.Controls.Add(issue);
        card.Controls.Add(inApp);
        card.Controls.Add(sentence);

        card.Controls.Add(new XPlatLinkLabel("A disabled link", "https://example.com")
        {
            Dock = XDock.Top,
            Enabled = false,
            Margin = new XPlatThickness(0f, 0f, 0f, 16f),
        });

        card.Controls.Add(new XPlatLabel(
            "A link is an XPlatRichLabel whose text is a link span, so AddText and AddLink build a sentence around it. " +
            "Clicking one, or pressing Enter or Space while it has the focus, raises LinkClicked; with AutoLaunch on " +
            "and a Url set, the label also hands the URI to the desktop through XPlatLauncher.")
        {
            Dock = XDock.Top,
            AutoSize = false,
            Height = 62f,
            WrapText = true,
            Font = XPlatTheme.CaptionFont,
            ForeColorRole = XPlatThemeColor.MutedForeground,
            TextAlign = XPlatContentAlignment.TopLeft,
        });

        return card;
    }

    /// <summary>
    /// <see cref="XPlatToolStrip"/> and <see cref="XPlatStatusStrip"/>: docked bars that hold items
    /// rather than child controls. The first card is a mock editor whose tool bar and status bar
    /// are strips; the second drives a status strip's progress item from a row of buttons.
    /// </summary>
    private XPlatPanel BuildToolStripDemo()
    {
        var card = Card();
        card.Controls.Add(Heading("Tool strip and status strip"));

        var readout = ScrollCaption("Click an item, or Tab to a strip and use the arrow keys.");
        readout.Margin = new XPlatThickness(0f, 0f, 0f, 12f);
        card.Controls.Add(readout);

        var revert = new XPlatToolStripButton("Revert") { Enabled = false };
        revert.AddClickHandler(_ => readout.Text = "Tool strip: Revert");

        XPlatToolStripButton ToolItem(string text, string message)
        {
            var item = new XPlatToolStripButton(text);
            item.AddClickHandler(_ =>
            {
                readout.Text = message;
                revert.Enabled = true;
            });

            return item;
        }

        var fit = ToolItem("Fit", "Tool strip: Fit");
        fit.Alignment = XPlatToolStripItemAlignment.Far;

        var farSeparator = new XPlatToolStripSeparator { Alignment = XPlatToolStripItemAlignment.Far };

        var toolbar = new XPlatToolStrip();
        toolbar.Items.Add(ToolItem("New", "Tool strip: New"));
        toolbar.Items.Add(ToolItem("Open", "Tool strip: Open"));
        toolbar.Items.Add(new XPlatToolStripSeparator());
        toolbar.Items.Add(ToolItem("Save", "Tool strip: Save"));
        toolbar.Items.Add(revert);
        toolbar.Items.Add(new XPlatToolStripSpring());
        toolbar.Items.Add(new XPlatToolStripLabel("Zoom: 100%"));
        toolbar.Items.Add(farSeparator);
        toolbar.Items.Add(fit);

        var editorStatus = new XPlatStatusStrip();
        editorStatus.Items.Add(new XPlatToolStripLabel("Ready")
        {
            ForeColor = XPlatThemeColor.MutedForeground,
        });
        editorStatus.Items.Add(new XPlatToolStripSpring());
        editorStatus.Items.Add(new XPlatToolStripLabel("UTF-8")
        {
            ForeColor = XPlatThemeColor.MutedForeground,
        });
        editorStatus.Items.Add(new XPlatToolStripSeparator());
        editorStatus.Items.Add(new XPlatToolStripProgressBar { Width = 90f, Value = 65f });

        var editor = new XPlatPanel
        {
            Dock = XDock.Top,
            Height = 156f,
            CornerRadius = 8f,
            BorderWidth = 1f,
            Padding = new XPlatThickness(0f),
            Margin = new XPlatThickness(0f, 0f, 0f, 20f),
        };

        editor.Controls.Add(toolbar);
        editor.Controls.Add(editorStatus);
        editor.Controls.Add(new XPlatLabel(
            "The tool bar and the status bar are one control each, and every item in them is a plain object " +
            "rather than a child control: nine items on the top bar and five on the bottom cost one paint pass " +
            "between them. The spring takes the slack, which is what pins Zoom and Fit to the right-hand end, " +
            "and Revert stays disabled until one of the other items is used.")
        {
            Dock = XDock.Fill,
            AutoSize = false,
            WrapText = true,
            Padding = new XPlatThickness(14f, 10f),
            Font = XPlatTheme.CaptionFont,
            ForeColorRole = XPlatThemeColor.MutedForeground,
            TextAlign = XPlatContentAlignment.TopLeft,
        });

        card.Controls.Add(editor);
        card.Controls.Add(Heading("Progress in a status strip"));

        var percentLabel = new XPlatToolStripLabel("0%")
        {
            ForeColor = XPlatThemeColor.MutedForeground,
        };

        var progress = new XPlatToolStripProgressBar { Width = 220f };

        var copyStatus = new XPlatStatusStrip();
        copyStatus.Items.Add(new XPlatToolStripLabel("Copying files..."));
        copyStatus.Items.Add(new XPlatToolStripSeparator());
        copyStatus.Items.Add(progress);
        copyStatus.Items.Add(percentLabel);
        copyStatus.Items.Add(new XPlatToolStripSpring());

        var clear = new XPlatToolStripButton("Clear") { Alignment = XPlatToolStripItemAlignment.Far };
        clear.AddClickHandler(_ =>
        {
            progress.Value = 0f;
            SyncPercent();
            readout.Text = "Status strip: Clear";
        });

        copyStatus.Items.Add(clear);

        void SyncPercent() => percentLabel.Text = $"{(int)MathF.Round(progress.Percent * 100f)}%";

        var statusBox = new XPlatPanel
        {
            Dock = XDock.Top,
            Height = 92f,
            CornerRadius = 8f,
            BorderWidth = 1f,
            Padding = new XPlatThickness(0f),
            Margin = new XPlatThickness(0f, 0f, 0f, 16f),
        };

        statusBox.Controls.Add(copyStatus);
        statusBox.Controls.Add(new XPlatLabel(
            "The bar, the percentage and the Clear button are items of one status strip, so the strip is a " +
            "single tab stop: the arrow keys move between the items and Enter activates the one under them.")
        {
            Dock = XDock.Fill,
            AutoSize = false,
            WrapText = true,
            Padding = new XPlatThickness(14f, 10f),
            Font = XPlatTheme.CaptionFont,
            ForeColorRole = XPlatThemeColor.MutedForeground,
            TextAlign = XPlatContentAlignment.TopLeft,
        });

        var row = ButtonRow();

        var start = new XPlatButton("Start")
        {
            Dock = XDock.Left,
            AutoSize = true,
            Style = XPlatButtonStyle.Secondary,
            Margin = new XPlatThickness(0f, 0f, 6f, 0f),
        };
        start.AddClickHandler(_ =>
        {
            progress.Value = 0f;
            SyncPercent();
            readout.Text = "Copy started.";
        });

        var step = new XPlatButton("Step 10")
        {
            Dock = XDock.Left,
            AutoSize = true,
            Style = XPlatButtonStyle.Secondary,
            Margin = new XPlatThickness(0f, 0f, 6f, 0f),
        };
        step.AddClickHandler(_ =>
        {
            progress.Value = MathF.Min(progress.Maximum, progress.Value + 10f);
            SyncPercent();
            readout.Text = $"Copy at {percentLabel.Text}";
        });

        var finish = new XPlatButton("Finish")
        {
            Dock = XDock.Left,
            AutoSize = true,
            Style = XPlatButtonStyle.Secondary,
            Margin = new XPlatThickness(0f, 0f, 6f, 0f),
        };
        finish.AddClickHandler(_ =>
        {
            progress.Value = progress.Maximum;
            SyncPercent();
            readout.Text = "Copy finished.";
        });

        row.Controls.Add(start);
        row.Controls.Add(step);
        row.Controls.Add(finish);

        card.Controls.Add(row);
        card.Controls.Add(statusBox);
        return card;
    }

    /// <summary>
    /// <see cref="XPlatGroupBox"/>: the caption sits in a break in the top edge and the content is
    /// laid out below it. The right-hand box scrolls, which is where the caption band earns its
    /// keep - the rows are clipped below the caption instead of sliding under it.
    /// </summary>
    private XPlatPanel BuildGroupBoxDemo()
    {
        var section = new XPlatPanel
        {
            Dock = XDock.Fill,
            ShowFrame = false,
            IsCard = false,
            BackColor = null,
            BorderWidth = 0f,
            CornerRadius = 0f,
            Padding = new XPlatThickness(0f),
        };

        var column = new XPlatPanel
        {
            Dock = XDock.Left,
            Width = 320f,
            ShowFrame = false,
            IsCard = false,
            BackColor = null,
            BorderWidth = 0f,
            CornerRadius = 0f,
            Padding = new XPlatThickness(0f),
            Margin = new XPlatThickness(0f, 0f, 16f, 0f),
        };

        var appearance = new XPlatGroupBox("Appearance")
        {
            Dock = XDock.Top,
            Height = 168f,
            Margin = new XPlatThickness(0f, 0f, 0f, 16f),
        };
        foreach (string caption in new[] { "Show grid", "Snap to grid", "Show rulers" })
        {
            var box = new XPlatCheckBox(caption)
            {
                Dock = XDock.Top,
                Checked = caption != "Show rulers",
                Margin = new XPlatThickness(0f, 0f, 0f, 4f),
            };
            box.AddCheckedChangedHandler(_ => SetStatus($"{box.Text} is {Describe(box.CheckState)}."));
            appearance.Controls.Add(box);
        }

        column.Controls.Add(appearance);

        // Disabling the group box disables everything inside it, so the caption, the frame and the
        // check boxes all grey out together.
        var advanced = new XPlatGroupBox("Advanced (disabled)")
        {
            Dock = XDock.Top,
            Height = 116f,
            Enabled = false,
        };

        foreach (string caption in new[] { "Trace layout", "Cache layers" })
        {
            advanced.Controls.Add(new XPlatCheckBox(caption)
            {
                Dock = XDock.Top,
                Checked = caption == "Trace layout",
            });
        }

        column.Controls.Add(advanced);

        section.Controls.Add(column);

        var rows = new XPlatGroupBox("Rows")
        {
            Dock = XDock.Fill,
        };

        // A group box scrolls its own content below the caption; the rows go in a panel of their
        // own so they can start at its top edge and scroll as one block.
        var list = new XPlatPanel
        {
            Dock = XDock.Fill,
            ShowFrame = false,
            IsCard = false,
            BackColor = null,
            BorderWidth = 0f,
            CornerRadius = 0f,
            Padding = new XPlatThickness(0f),
            VerticalScroll = XPlatScrollStyle.Automatic,
            HorizontalScroll = XPlatScrollStyle.None,
        };

        for (int i = 0; i < 24; i++)
        {
            var row = new XPlatButton($"Row {i + 1}")
            {
                AutoSize = false,
                Style = XPlatButtonStyle.Secondary,
                Bounds = new SKRect(0f, i * 30f, 160f, (i * 30f) + 26f),
            };
            row.AddClickHandler(_ => SetStatus($"Row {i + 1} clicked inside a group box."));
            list.Controls.Add(row);
        }

        rows.Controls.Add(list);
        section.Controls.Add(rows);
        return section;
    }

    /// <summary>
    /// An <see cref="XPlatListBox"/> in <see cref="XPlatSelectionMode.MultiExtended"/>: click to
    /// select, Ctrl+click to toggle, Shift+click to extend. The button cycles the mode so all four
    /// behave the same way as the WinForms control.
    /// </summary>
    private XPlatPanel BuildListBoxCard()
    {
        var card = Card();

        var modeCaption = ScrollCaption("MultiExtended");
        card.Controls.Add(modeCaption);

        var list = new XPlatListBox
        {
            Dock = XDock.Top,
            Height = 108f,
            SelectionMode = XPlatSelectionMode.MultiExtended,
            VerticalScroll = XPlatScrollStyle.Automatic,
            Margin = new XPlatThickness(0f, 0f, 0f, 6f),
        };

        list.Items.AddRange(new object[]
        {
            "Overview", "Labels", "Buttons", "Text input", "Rich text", "Images",
            "Scrolling", "Lists", "Dialogs", "Split container", "Selection", "Drag and drop",
            "Menus", "Owned windows",
        });

        list.SelectedIndex = 0;
        card.Controls.Add(list);

        var selectionLabel = ScrollCaption("1 selected");
        selectionLabel.Margin = new XPlatThickness(0f, 0f, 0f, 6f);
        card.Controls.Add(selectionLabel);

        list.AddSelectedIndexChangedHandler(_ =>
        {
            int count = list.SelectedIndices.Count;
            selectionLabel.Text = count switch
            {
                0 => "no selection",
                1 => $"\"{list.GetItemText(list.SelectedIndex)}\" selected",
                _ => $"{count} rows",
            };
        });

        list.AddItemActivatedHandler(_ =>
            SetStatus($"Activated \"{list.GetItemText(list.SelectedIndex)}\"."));

        var modeButton = new XPlatButton("Next mode")
        {
            Dock = XDock.Top,
            AutoSize = true,
            Style = XPlatButtonStyle.Secondary,
        };

        modeButton.AddClickHandler(_ =>
        {
            var mode = list.SelectionMode switch
            {
                XPlatSelectionMode.None => XPlatSelectionMode.One,
                XPlatSelectionMode.One => XPlatSelectionMode.MultiSimple,
                XPlatSelectionMode.MultiSimple => XPlatSelectionMode.MultiExtended,
                _ => XPlatSelectionMode.None,
            };

            list.SelectionMode = mode;
            modeCaption.Text = mode.ToString();
            SetStatus($"List selection mode is {mode}.");
        });

        card.Controls.Add(modeButton);

        return card;
    }

    /// <summary>
    /// Two list views over the same shape of data: one in <see cref="XPlatView.Details"/> with
    /// sortable columns and grid lines, one in <see cref="XPlatView.List"/>. Both only paint the
    /// rows on screen, so the row count is there to make that claim checkable.
    /// </summary>
    private XPlatPanel BuildListViewDemo()
    {
        var card = Card();

        var sortCaption = ScrollCaption("Click a column header to sort; drag a header edge to resize");
        card.Controls.Add(sortCaption);

        var details = new XPlatListView
        {
            Dock = XDock.Top,
            Height = 200f,
            View = XPlatView.Details,
            GridLines = true,
            SelectionMode = XPlatSelectionMode.MultiExtended,
            VerticalScroll = XPlatScrollStyle.Automatic,
            HorizontalScroll = XPlatScrollStyle.Automatic,
            Margin = new XPlatThickness(0f, 0f, 0f, 6f),
        };

        // The Size cells read "812 KB" and "4 KB", which is the wrong way round as text, so the column
        // compares the byte count the rows keep in their Tag instead.
        var sizeColumn = new XPlatListViewColumn("Size", 76f, XPlatContentAlignment.MiddleRight)
        {
            Comparer = (left, right) => BytesOf(left).CompareTo(BytesOf(right)),
        };

        details.Columns.Add(new XPlatListViewColumn("Name", 168f));
        details.Columns.Add(new XPlatListViewColumn("Kind", 104f));
        details.Columns.Add(sizeColumn);
        details.Columns.Add(new XPlatListViewColumn("Modified", 118f));

        foreach (var row in SampleFiles)
        {
            details.Items.Add(new XPlatListViewItem(row.Name, row.Kind, FormatSize(row.Bytes), row.Modified)
            {
                Tag = row.Bytes,
            });
        }

        details.SelectedIndex = 0;
        card.Controls.Add(details);

        var selectionCaption = ScrollCaption("1 selected");
        card.Controls.Add(selectionCaption);

        details.AddSelectedIndexChangedHandler(_ =>
        {
            int count = details.SelectedIndices.Count;
            selectionCaption.Text = count switch
            {
                0 => "no selection",
                1 => $"{details.Items[details.SelectedIndex].Text} selected",
                _ => $"{count} rows selected",
            };
        });

        details.AddItemActivatedHandler(_ =>
            SetStatus($"Opened {details.Items[details.SelectedIndex].Text}."));

        details.AddColumnClickHandler((_, e) =>
            sortCaption.Text = $"Sorted by {e.Column.Text}, {details.SortOrder}");

        var icons = new XPlatListView
        {
            Dock = XDock.Top,
            Height = 148f,
            View = XPlatView.List,
            SelectionMode = XPlatSelectionMode.One,
            VerticalScroll = XPlatScrollStyle.Automatic,
            HorizontalScroll = XPlatScrollStyle.Automatic,
            Margin = new XPlatThickness(0f, 0f, 0f, 6f),
        };

        foreach (var row in SampleFiles)
        {
            icons.Items.Add(new XPlatListViewItem(row.Name));
        }

        icons.SelectedIndex = 2;
        card.Controls.Add(icons);

        var iconsCaption = ScrollCaption("List view");
        iconsCaption.Margin = new XPlatThickness(0f, 0f, 0f, 6f);
        card.Controls.Add(iconsCaption);

        var buttons = ButtonRow();

        var clearSort = new XPlatButton("Clear sort")
        {
            Dock = XDock.Left,
            AutoSize = true,
            Style = XPlatButtonStyle.Secondary,
            Margin = new XPlatThickness(0f, 0f, 6f, 0f),
        };

        clearSort.AddClickHandler(_ =>
        {
            details.SortOrder = XPlatSortOrder.None;
            sortCaption.Text = "Click a column header to sort";
            SetStatus("Sorting cleared.");
        });

        var grow = new XPlatButton("Add rows")
        {
            Dock = XDock.Left,
            AutoSize = true,
            Style = XPlatButtonStyle.Secondary,
        };

        grow.AddClickHandler(_ =>
        {
            details.BeginUpdate();
            try
            {
                for (int index = 0; index < 500; index++)
                {
                    long bytes = (index + 1) * 1_024L;
                    details.Items.Add(new XPlatListViewItem(
                        $"generated-{details.Items.Count:D4}.bin",
                        "File",
                        FormatSize(bytes),
                        "today")
                    {
                        Tag = bytes,
                    });
                }
            }
            finally
            {
                details.EndUpdate();
            }

            SetStatus($"{details.Items.Count} rows; only the visible ones are drawn.");
        });

        buttons.Controls.Add(clearSort);
        buttons.Controls.Add(grow);

        var lockWidths = new XPlatButton("Lock widths")
        {
            Dock = XDock.Left,
            AutoSize = true,
            Style = XPlatButtonStyle.Secondary,
            Margin = new XPlatThickness(6f, 0f, 0f, 0f),
        };

        lockWidths.AddClickHandler(_ =>
        {
            details.AllowColumnResize = !details.AllowColumnResize;
            lockWidths.Text = details.AllowColumnResize ? "Lock widths" : "Unlock widths";
            SetStatus(details.AllowColumnResize
                ? "Header edges can be dragged again."
                : "Header edges are locked; the columns cannot be resized.");
        });

        buttons.Controls.Add(lockWidths);
        card.Controls.Add(buttons);

        var countCaption = ScrollCaption($"{details.Items.Count} rows");
        card.Controls.Add(countCaption);

        return card;
    }

    private static long BytesOf(XPlatListViewItem item) => item.Tag is long bytes ? bytes : 0L;

    private static string FormatSize(long bytes) => bytes >= 1024 ? $"{bytes / 1024} KB" : $"{bytes} B";

    private static readonly (string Name, string Kind, long Bytes, string Modified)[] SampleFiles =
    {
        ("readme.txt", "Text", 4_096, "2024-05-02 09:14"),
        ("XPlatControl.cs", "Source", 62_464, "2024-05-11 18:02"),
        ("XPlatListView.cs", "Source", 31_744, "2024-05-12 07:45"),
        ("theme-light.json", "Data", 2_048, "2024-04-28 12:30"),
        ("theme-dark.json", "Data", 2_048, "2024-04-28 12:30"),
        ("render-target.png", "Image", 151_552, "2024-05-09 22:11"),
        ("harness.exe", "Binary", 98_304, "2024-05-10 08:00"),
        ("LICENSE", "Text", 1_024, "2024-01-04 00:00"),
        ("build.log", "Log", 831_488, "2024-05-12 06:59"),
        ("a-very-long-file-name-that-should-be-trimmed-with-an-ellipsis.txt", "Text", 7_168, "2024-05-01 11:20"),
    };

    /// <summary>
    /// A tree with check boxes over a small source-tree shape. The rows are virtualized the same way
    /// the lists are - the control only knows the nodes whose ancestors are open - so the branch the
    /// "Add leaves" button grows costs nothing until it is opened.
    /// </summary>
    private XPlatPanel BuildTreeViewDemo()
    {
        var card = Card();

        var selectionCaption = ScrollCaption("Select a node");
        card.Controls.Add(selectionCaption);

        var tree = new XPlatTreeView
        {
            Dock = XDock.Top,
            Height = 260f,
            CheckBoxes = true,
            SelectionMode = XPlatSelectionMode.One,
            VerticalScroll = XPlatScrollStyle.Automatic,
            HorizontalScroll = XPlatScrollStyle.Automatic,
            Margin = new XPlatThickness(0f, 0f, 0f, 6f),
        };

        var src = new XPlatTreeNode("src");

        var core = new XPlatTreeNode("Core");
        core.Nodes.Add(new XPlatTreeNode("XPlatControl.cs"));
        core.Nodes.Add(new XPlatTreeNode("XPlatPainter.cs"));
        core.Nodes.Add(new XPlatTreeNode("XPlatTheme.cs"));
        src.Nodes.Add(core);

        var controls = new XPlatTreeNode("Controls");
        controls.Nodes.Add(new XPlatTreeNode("XPlatButton.cs"));
        controls.Nodes.Add(new XPlatTreeNode("XPlatListView.cs"));
        controls.Nodes.Add(new XPlatTreeNode("XPlatTreeView.cs"));
        src.Nodes.Add(controls);

        var platforms = new XPlatTreeNode("Platform");
        platforms.Nodes.Add(new XPlatTreeNode("Win32"));
        platforms.Nodes.Add(new XPlatTreeNode("AppKit"));
        platforms.Nodes.Add(new XPlatTreeNode("Gtk4"));
        src.Nodes.Add(platforms);

        tree.Nodes.Add(src);
        tree.Nodes.Add(new XPlatTreeNode("README.md"));
        tree.Nodes.Add(new XPlatTreeNode("LICENSE"));

        src.Expand();
        core.Expand();
        tree.SelectedNode = core.Nodes[0];

        // Two of the three files under Controls are checked, so that branch reads as indeterminate.
        controls.Nodes[0].Checked = true;
        controls.Nodes[1].Checked = true;

        card.Controls.Add(tree);

        tree.AddSelectedIndexChangedHandler(_ =>
            selectionCaption.Text = tree.SelectedNode is { } node
                ? $"{node.Text}, level {node.Level}"
                : "no selection");

        var buttons = ButtonRow();

        var expand = new XPlatButton("Expand all")
        {
            Dock = XDock.Left,
            AutoSize = true,
            Style = XPlatButtonStyle.Secondary,
            Margin = new XPlatThickness(0f, 0f, 6f, 0f),
        };

        expand.AddClickHandler(_ =>
        {
            tree.ExpandAll();
            SetStatus($"{tree.ItemCount} rows visible.");
        });

        var collapse = new XPlatButton("Collapse all")
        {
            Dock = XDock.Left,
            AutoSize = true,
            Style = XPlatButtonStyle.Secondary,
            Margin = new XPlatThickness(0f, 0f, 6f, 0f),
        };

        collapse.AddClickHandler(_ =>
        {
            tree.CollapseAll();
            SetStatus($"{tree.ItemCount} rows visible.");
        });

        var grow = new XPlatButton("Add 5,000 leaves")
        {
            Dock = XDock.Left,
            AutoSize = true,
            Style = XPlatButtonStyle.Secondary,
        };

        grow.AddClickHandler(_ =>
        {
            // The branch is built while it is still detached, so filling it costs nothing; adding it
            // to the tree is the one rebuild.
            var generated = new XPlatTreeNode("generated");
            for (int index = 0; index < 5000; index++)
            {
                generated.Nodes.Add(new XPlatTreeNode($"leaf-{index:D4}.cs"));
            }

            tree.Nodes.Add(generated);
            generated.Expand();

            SetStatus($"{tree.Nodes.Count} roots, {tree.ItemCount} rows visible; only those are drawn.");
        });

        buttons.Controls.Add(expand);
        buttons.Controls.Add(collapse);
        buttons.Controls.Add(grow);
        card.Controls.Add(buttons);

        var checkCaption = ScrollCaption(
            $"{CountChecked(tree.Nodes)} of {CountNodes(tree.Nodes)} nodes checked");
        card.Controls.Add(checkCaption);

        tree.AddAfterCheckHandler(_ => checkCaption.Text =
            $"{CountChecked(tree.Nodes)} of {CountNodes(tree.Nodes)} nodes checked");

        return card;
    }

    private static int CountNodes(XPlatTreeView.XPlatTreeNodeCollection nodes)
    {
        int count = 0;
        foreach (var node in nodes)
        {
            count += 1 + CountNodes(node.Nodes);
        }

        return count;
    }

    private static int CountChecked(XPlatTreeView.XPlatTreeNodeCollection nodes)
    {
        int count = 0;
        foreach (var node in nodes)
        {
            if (node.Checked)
            {
                count++;
            }

            count += CountChecked(node.Nodes);
        }

        return count;
    }

    /// <summary>
    /// A list of a chosen size, with what the last frame cost to draw it. The list only paints the
    /// rows that are on screen, so scrolling a hundred thousand items should cost about what
    /// scrolling ten does; the readout is there to show that rather than claim it.
    /// </summary>
    private XPlatPanel BuildPerformancePage()
    {
        var list = new XPlatListBox
        {
            Dock = XDock.Fill,
            SelectionMode = XPlatSelectionMode.One,
            VerticalScroll = XPlatScrollStyle.Automatic,
            HorizontalScroll = XPlatScrollStyle.None,
        };

        return PerformanceCard(list, "items", count =>
        {
            list.BeginUpdate();
            try
            {
                list.Items.Clear();

                if (count > 0)
                {
                    var items = new object[count];
                    for (int index = 0; index < count; index++)
                    {
                        items[index] = $"Item {index + 1:N0}";
                    }

                    list.Items.AddRange(items);
                }
            }
            finally
            {
                list.EndUpdate();
            }

            // A smaller list cannot stay scrolled to where a larger one was.
            list.ScrollTo(0f, 0f);
        });
    }

    /// <summary>
    /// The same measurement over real controls rather than rows: a stack panel holding one button
    /// per entry, stacked down it and scrolled by the panel itself. Nothing here is virtualised, so
    /// the panel arranges and walks every button it holds on every frame - which is the cost the
    /// list page avoids, and the reason to have both.
    /// </summary>
    private XPlatPanel BuildControlStackPage()
    {
        const float ButtonHeight = 32f;
        const float ButtonPitch = 36f;

        var panel = new XPlatStackPanel
        {
            Dock = XDock.Fill,
            VerticalScroll = XPlatScrollStyle.Automatic,
            HorizontalScroll = XPlatScrollStyle.None,
            Spacing = ButtonPitch - ButtonHeight,
            ShowFrame = true,
            IsCard = true,
        };

        return PerformanceCard(panel, "buttons", count =>
        {
            // Every child added lays the panel out again, so filling it one button at a time would
            // cost a pass over a growing child list per button. Suspending layout turns that into
            // the single pass ResumeLayout runs at the end.
            panel.SuspendLayout();
            try
            {
                var previous = panel.Controls.ToArray();
                panel.Controls.Clear();
                foreach (var child in previous)
                {
                    child.Dispose();
                }

                for (int index = 0; index < count; index++)
                {
                    // A fixed height with the stack's stretch keeps the button out of the measure
                    // pass altogether: the stack only has to place it.
                    panel.Controls.Add(new XPlatButton($"Button {index + 1:N0}")
                    {
                        AutoSize = false,
                        Height = ButtonHeight,
                    });
                }
            }
            finally
            {
                panel.ResumeLayout();
            }

            panel.ScrollTo(0f, 0f);
        });
    }

    /// <summary>
    /// The chrome both performance pages share: a row of buttons that load a chosen number of
    /// entries into <paramref name="content"/>, and a readout of what the last frame cost.
    /// </summary>
    /// <param name="content">The control the buttons fill, and the one the readout reports on.</param>
    /// <param name="noun">What one entry is called, for the count and the status line.</param>
    /// <param name="fill">Replaces everything in <paramref name="content"/> with the given count.</param>
    private XPlatPanel PerformanceCard(XPlatControl content, string noun, Action<int> fill)
    {
        var card = Card();

        var countLabel = new XPlatLabel($"no {noun}")
        {
            Dock = XDock.Right,
            AutoSize = false,
            Width = 110f,
            Font = XPlatTheme.CaptionFont,
            ForeColorRole = XPlatThemeColor.MutedForeground,
            TextAlign = XPlatContentAlignment.BottomRight,
            Margin = new XPlatThickness(0f),
        };

        var frameLabel = new XPlatLabel("Last frame -")
        {
            Dock = XDock.Fill,
            AutoSize = false,
            Font = XPlatTheme.CaptionFont,
            ForeColorRole = XPlatThemeColor.MutedForeground,
            TextAlign = XPlatContentAlignment.BottomLeft,
            Margin = new XPlatThickness(0f),
        };

        // Docking is applied in declaration order, so the count takes the right edge before the
        // frame time fills what is left of the row.
        var readout = Row(20f);
        readout.Controls.Add(countLabel);
        readout.Controls.Add(frameLabel);

        var buttons = Row(32f);
        buttons.Controls.Add(LoadButton("Clear", () => Load(0)));

        foreach (int size in new[] { 1, 10, 100, 1000, 10000, 100000 })
        {
            int count = size;
            buttons.Controls.Add(LoadButton(size.ToString("N0"), () => Load(count)));
        }

        card.Controls.Add(buttons);
        card.Controls.Add(readout);
        card.Controls.Add(content);

        // The readout is the one thing on the page that is not the content, and it is painted into
        // the frame it reports on, so a naive update would measure itself: the text changes, the
        // label repaints, that repaint is the next frame, and the page never goes idle. Only
        // assigning a changed string and only reading the host every so often lets the loop die
        // once the label is all that is left repainting.
        const long ReadoutIntervalMs = 100;
        long lastReadout = long.MinValue / 2;
        string shownFrame = string.Empty;

        OnHostAvailable(content, host => host.AddFramePaintedHandler(_ =>
        {
            long now = Environment.TickCount64;
            if (now - lastReadout < ReadoutIntervalMs)
            {
                return;
            }

            lastReadout = now;

            var area = host.LastFrameArea;
            string text = $"Last frame {host.LastFrameTime.TotalMilliseconds:0.0} ms " +
                          $"over {area.Width} x {area.Height} px";

            if (text != shownFrame)
            {
                shownFrame = text;
                frameLabel.Text = text;
            }
        }));

        return card;

        XPlatButton LoadButton(string text, Action onClick)
        {
            var button = new XPlatButton(text)
            {
                Dock = XDock.Left,
                AutoSize = true,
                Style = XPlatButtonStyle.Secondary,
                Margin = new XPlatThickness(0f, 0f, 6f, 0f),
            };
            button.AddClickHandler(_ => onClick());
            return button;
        }

        void Load(int count)
        {
            long started = Stopwatch.GetTimestamp();

            fill(count);

            countLabel.Text = count == 0 ? $"no {noun}" : $"{count:N0} {noun}";
            SetStatus($"Loaded {count:N0} {noun} in {Stopwatch.GetElapsedTime(started).TotalMilliseconds:0.0} ms.");
        }
    }

    /// <summary>An invisible full-width strip a page uses to lay a row of controls out in.</summary>
    private static XPlatPanel Row(float height) => new()
    {
        Dock = XDock.Top,
        Height = height,
        ShowFrame = false,
        IsCard = false,
        BackColor = null,
        BorderWidth = 0f,
        CornerRadius = 0f,
        Padding = new XPlatThickness(0f),
        Margin = new XPlatThickness(0f, 0f, 0f, 8f),
    };

    /// <summary>
    /// Runs <paramref name="action"/> the first time <paramref name="control"/> has a host, which
    /// is the first moment there is one to subscribe to. Pages are built before they are attached,
    /// and attaching lays the tree out straight away, so the first bounds change is the earliest
    /// point at which <see cref="XPlatControl.Host"/> resolves. The handler stays registered and
    /// does nothing after that rather than disposing itself from inside its own invocation.
    /// </summary>
    private static void OnHostAvailable(XPlatControl control, Action<XPlatUiHost> action)
    {
        bool done = false;

        control.AddBoundsChangedHandler(_ =>
        {
            if (done || control.Host is not { } host)
            {
                return;
            }

            done = true;
            action(host);
        });
    }

    private XPlatPanel BuildScrollingCard()
    {
        var card = Card();

        card.Controls.Add(ScrollCaption("VerticalScroll = Automatic"));
        card.Controls.Add(BuildAutoScrollList());
        card.Controls.Add(ScrollCaption("HorizontalScroll = Always"));
        card.Controls.Add(BuildAlwaysScrollStrip());

        return card;
    }

    private static XPlatLabel ScrollCaption(string text) => new(text)
    {
        Dock = XDock.Top,
        AutoSize = false,
        Height = 16f,
        Font = XPlatTheme.CaptionFont,
        ForeColorRole = XPlatThemeColor.MutedForeground,
        TextAlign = XPlatContentAlignment.BottomLeft,
        Margin = new XPlatThickness(0f, 0f, 0f, 2f),
    };

    /// <summary>An invisible strip that a row of buttons docks into, left to right.</summary>
    private static XPlatPanel ButtonRow() => new()
    {
        Dock = XDock.Top,
        Height = 30f,
        ShowFrame = false,
        IsCard = false,
        BackColor = null,
        BorderWidth = 0f,
        CornerRadius = 0f,
        Padding = new XPlatThickness(0f),
        Margin = new XPlatThickness(0f, 0f, 0f, 6f),
    };

    /// <summary>
    /// An <see cref="XPlatScrollStyle.Automatic"/> panel: the vertical bar appears only because the
    /// rows overflow. Only children with <see cref="DockStyle.None"/> scroll, so each row is placed
    /// explicitly instead of being docked.
    /// </summary>
    private XPlatPanel BuildAutoScrollList()
    {
        var list = new XPlatPanel
        {
            Dock = XDock.Top,
            Height = 90f,
            VerticalScroll = XPlatScrollStyle.Automatic,
            Padding = new XPlatThickness(4f),
            Margin = new XPlatThickness(0f, 0f, 0f, 6f),
        };

        for (int i = 0; i < 10; i++)
        {
            var row = new XPlatButton($"Row {i + 1}")
            {
                AutoSize = false,
                Style = XPlatButtonStyle.Secondary,
                Bounds = new SKRect(4f, 4f + (i * 24f), 128f, 26f + (i * 24f)),
            };
            row.AddClickHandler(_ => SetStatus($"Row {i + 1} clicked - hit testing follows the scroll offset."));
            list.Controls.Add(row);
        }

        return list;
    }

    /// <summary>An <see cref="XPlatScrollStyle.Always"/> panel: the bar is shown even when it fits.</summary>
    private XPlatPanel BuildAlwaysScrollStrip()
    {
        var strip = new XPlatPanel
        {
            Dock = XDock.Top,
            Height = 36f,
            HorizontalScroll = XPlatScrollStyle.Always,
            Padding = new XPlatThickness(4f),
            Margin = new XPlatThickness(0f, 0f, 0f, 6f),
        };

        for (int i = 0; i < 10; i++)
        {
            strip.Controls.Add(new XPlatLabel($"Chip {i + 1}")
            {
                AutoSize = false,
                BackColor = XPlatTheme.Secondary,
                ForeColorRole = XPlatThemeColor.MutedForeground,
                TextAlign = XPlatContentAlignment.MiddleCenter,
                Bounds = new SKRect(4f + (i * 92f), 4f, 88f + (i * 92f), 16f),
            });
        }

        return strip;
    }

    private XPlatPanel BuildButtonsCard()
    {
        var card = Card();

        _primaryButton = new XPlatButton("Primary action")
        {
            Dock = XDock.Top,
            AutoSize = true,
            Style = XPlatButtonStyle.Primary,
            Margin = new XPlatThickness(0f, 0f, 0f, 10f),
        };
        _primaryButton.AddClickHandler(_ =>
        {
            _clicks++;
            UpdateCounter();
            SetStatus($"Primary action clicked {_clicks} time(s).");
        });
        card.Controls.Add(_primaryButton);

        var secondary = new XPlatButton("Secondary action")
        {
            Dock = XDock.Top,
            AutoSize = true,
            Style = XPlatButtonStyle.Secondary,
            Margin = new XPlatThickness(0f, 0f, 0f, 10f),
        };
        secondary.AddClickHandler(_ => SetStatus("Secondary action clicked."));
        card.Controls.Add(secondary);

        var ghost = new XPlatButton("Ghost action")
        {
            Dock = XDock.Top,
            AutoSize = true,
            Style = XPlatButtonStyle.Ghost,
            Margin = new XPlatThickness(0f, 0f, 0f, 10f),
        };
        ghost.AddClickHandler(_ => SetStatus("Ghost action clicked."));
        card.Controls.Add(ghost);

        card.Controls.Add(new XPlatLabel("Tab moves focus between controls; Enter or Space activates the focused button.")
        {
            Dock = XDock.Top,
            AutoSize = false,
            Height = 40f,
            WrapText = true,
            Font = XPlatTheme.CaptionFont,
            ForeColorRole = XPlatThemeColor.MutedForeground,
            TextAlign = XPlatContentAlignment.TopLeft,
        });

        return card;
    }

    private static XPlatPanel BuildImagesCard()
    {
        var card = Card();

        // Opacity fades a control and everything inside it as a single group, so the gradient and
        // the panel it sits in are composited once over the card instead of each blending on its
        // own. A faded control is not opaque, which is what tells the card that repainting it has
        // to bring back what shows through.
        var faded = new XPlatPanel
        {
            Dock = XDock.Fill,
            ShowFrame = false,
            IsCard = false,
            BackColor = null,
            BorderWidth = 0f,
            CornerRadius = 0f,
            Padding = new XPlatThickness(0f),
            Opacity = 0.6f,
            Margin = new XPlatThickness(0f, 0f, 0f, 8f),
        };

        var image = new XPlatImage
        {
            Dock = XDock.Fill,
            SizeMode = XPlatSizeMode.Zoom,
            ImageAlign = XPlatContentAlignment.MiddleCenter,
            CornerRadius = 8f,
            DisposeImageWithControl = true,
        };
        image.Image = CreateGradientImage(320, 200);
        faded.Controls.Add(image);
        card.Controls.Add(faded);

        card.Controls.Add(new XPlatLabel("A gradient drawn with SkiaSharp, shown with XPlatImage (Zoom) at Opacity 0.6.")
        {
            Dock = XDock.Bottom,
            AutoSize = false,
            Height = 34f,
            WrapText = true,
            Font = XPlatTheme.CaptionFont,
            ForeColorRole = XPlatThemeColor.MutedForeground,
            TextAlign = XPlatContentAlignment.TopLeft,
        });

        return card;
    }

    private void UpdateCounter() => _counterLabel.Text = $"Button clicks: {_clicks}";

    /// <summary>
    /// Drop-down lists, including the two cases the popup layer exists for: a list longer than its
    /// owner and one near the bottom of the window, which hangs past the edge of the window instead
    /// of being clipped by it.
    /// </summary>
    private XPlatPanel BuildComboBoxDemo()
    {
        var card = Card();
        card.Controls.Add(Heading("Combo boxes"));

        card.Controls.Add(ScrollCaption("A few items"));

        var sizes = new XPlatComboBox
        {
            Dock = XDock.Top,
            Height = 32f,
            Margin = new XPlatThickness(0f, 0f, 0f, 12f),
        };

        foreach (string size in new[] { "Small", "Medium", "Large", "Extra large" })
        {
            sizes.Items.Add(size);
        }

        sizes.SelectedIndex = 1;
        sizes.AddSelectedIndexChangedHandler(_ => SetStatus($"Size is {sizes.SelectedText}."));
        card.Controls.Add(sizes);

        card.Controls.Add(ScrollCaption("No selection, so the empty text shows"));

        var optional = new XPlatComboBox
        {
            Dock = XDock.Top,
            Height = 32f,
            EmptyText = "Nothing selected",
            Margin = new XPlatThickness(0f, 0f, 0f, 12f),
        };

        optional.Items.Add("Alpha");
        optional.Items.Add("Beta");
        optional.Items.Add("Gamma");
        card.Controls.Add(optional);

        card.Controls.Add(ScrollCaption("Longer than its owner, so the list scrolls"));

        var languages = new XPlatComboBox
        {
            Dock = XDock.Top,
            Height = 32f,
            MaxDropDownHeight = 160f,
            Margin = new XPlatThickness(0f, 0f, 0f, 12f),
        };

        foreach (string language in new[]
                 {
                     "C#", "F#", "Rust", "Go", "Swift", "Kotlin", "TypeScript", "Python",
                     "Ruby", "Elixir", "Haskell", "OCaml", "Zig", "Nim", "D", "Julia",
                 })
        {
            languages.Items.Add(language);
        }

        languages.SelectedIndex = 0;
        card.Controls.Add(languages);

        card.Controls.Add(ScrollCaption("Disabled"));

        var disabled = new XPlatComboBox
        {
            Dock = XDock.Top,
            Height = 32f,
            Enabled = false,
            Margin = new XPlatThickness(0f, 0f, 0f, 12f),
        };

        disabled.Items.Add("Cannot be opened");
        disabled.SelectedIndex = 0;
        card.Controls.Add(disabled);

        card.Controls.Add(new XPlatLabel(
            "A drop-down is drawn in a window of its own when the platform layer provides one, so it can " +
            "hang past the edges of the window. Without one it is painted inside the window instead.")
        {
            Dock = XDock.Top,
            AutoSize = false,
            Height = 54f,
            WrapText = true,
            Font = XPlatTheme.CaptionFont,
            ForeColorRole = XPlatThemeColor.MutedForeground,
            TextAlign = XPlatContentAlignment.TopLeft,
        });

        return card;
    }

    /// <summary>
    /// <see cref="XPlatDateTimePicker"/>: the four formats, a bounded one, an empty one and a
    /// disabled one, plus a readout so committing typed text can be watched.
    /// </summary>
    private XPlatPanel BuildDateTimePickerDemo()
    {
        var card = Card();
        card.Controls.Add(Heading("Date and time"));

        var readout = ScrollCaption("Value: 2026-11-04 09:30");
        card.Controls.Add(readout);

        var shortPicker = new XPlatDateTimePicker(new DateTime(2026, 11, 4, 9, 30, 0));
        shortPicker.AddValueChangedHandler(_ =>
            readout.Text = $"Value: {shortPicker.Value:yyyy-MM-dd HH:mm}");
        card.Controls.Add(CaptionRow("Short (typed or picked)", shortPicker));

        card.Controls.Add(CaptionRow("Long", new XPlatDateTimePicker(new DateTime(2026, 11, 4))
        {
            Format = XPlatDateTimePickerFormat.Long,
        }));

        card.Controls.Add(CaptionRow("Time", new XPlatDateTimePicker(new DateTime(2026, 11, 4, 9, 30, 0))
        {
            Format = XPlatDateTimePickerFormat.Time,
        }));

        card.Controls.Add(CaptionRow(
            "Custom (yyyy-MM-dd HH:mm)",
            new XPlatDateTimePicker(new DateTime(2026, 11, 4, 9, 30, 0))
            {
                Format = XPlatDateTimePickerFormat.CustomFormat,
                CustomFormat = "yyyy-MM-dd HH:mm",
            }));

        DateTime today = DateTime.Today;
        card.Controls.Add(CaptionRow("Bounded (today +/- 7 days)", new XPlatDateTimePicker(today)
        {
            MinDate = today.AddDays(-7),
            MaxDate = today.AddDays(7),
        }));

        card.Controls.Add(CaptionRow("Empty (placeholder)", new XPlatDateTimePicker
        {
            PlaceholderText = "Pick a date",
        }));

        card.Controls.Add(CaptionRow("Disabled", new XPlatDateTimePicker(new DateTime(2026, 11, 4))
        {
            Enabled = false,
        }));

        card.Controls.Add(new XPlatLabel(
            "Click the calendar button, or press Alt+Down, to pick a date. Typing is parsed against the " +
            "current culture when the edit is committed - on Enter, when the focus leaves, or when the " +
            "calendar opens - and an entry that cannot be parsed reverts to the last good value. " +
            "Left/Right move between the segments, Up/Down step the segment under the caret, and " +
            "Page Up/Down step the month.")
        {
            Dock = XDock.Top,
            AutoSize = false,
            Height = 78f,
            WrapText = true,
            Font = XPlatTheme.CaptionFont,
            ForeColorRole = XPlatThemeColor.MutedForeground,
            TextAlign = XPlatContentAlignment.TopLeft,
            Margin = new XPlatThickness(0f, 6f, 0f, 0f),
        });

        return card;
    }

    /// <summary>
    /// <see cref="XPlatTextBox"/> autocomplete: the three modes over a custom source, the history
    /// source, and a disabled field. The readout follows whichever field was typed in last, so the
    /// completed text can be watched while the suffix is selected.
    /// </summary>
    private XPlatPanel BuildAutoCompleteDemo()
    {
        var card = Card();
        card.Controls.Add(Heading("Text box autocomplete"));

        var readout = ScrollCaption("SuggestAppend: type \"ap\" to see \"Apple\" completed and listed");
        card.Controls.Add(readout);

        string[] fruits =
        [
            "Apple", "Apricot", "Banana", "Blackberry", "Blueberry", "Cherry", "Cranberry", "Grape",
            "Lemon", "Lime", "Mango", "Orange", "Peach", "Pear", "Pineapple", "Plum", "Raspberry",
            "Strawberry",
        ];

        var suggest = AutoCompleteBox(XPlatAutoCompleteMode.Suggest, fruits);
        suggest.PlaceholderText = "Type a letter";
        suggest.AddTextChangedHandler(_ => readout.Text = $"Suggest: \"{suggest.Text}\"");
        card.Controls.Add(CaptionRow("Suggest (CustomSource)", suggest));

        var suggestAppend = AutoCompleteBox(XPlatAutoCompleteMode.SuggestAppend, fruits);
        suggestAppend.PlaceholderText = "Type a letter";
        suggestAppend.AddTextChangedHandler(_ => readout.Text =
            $"SuggestAppend: \"{suggestAppend.Text}\" ({suggestAppend.SelectionLength} selected)");
        card.Controls.Add(CaptionRow("SuggestAppend (CustomSource)", suggestAppend));

        var append = AutoCompleteBox(XPlatAutoCompleteMode.Append, fruits);
        append.PlaceholderText = "Type a letter";
        append.AddTextChangedHandler(_ => readout.Text =
            $"Append: \"{append.Text}\" ({append.SelectionLength} selected)");
        card.Controls.Add(CaptionRow("Append (CustomSource)", append));

        var history = new XPlatTextBox
        {
            AutoCompleteMode = XPlatAutoCompleteMode.Suggest,
            AutoCompleteSource = XPlatAutoCompleteSource.HistoryList,
            PlaceholderText = "Enter a value, then type it again",
        };
        history.AddTextChangedHandler(_ => readout.Text = $"HistoryList: \"{history.Text}\"");
        card.Controls.Add(CaptionRow("HistoryList (Suggest)", history));

        var disabled = AutoCompleteBox(XPlatAutoCompleteMode.Suggest, fruits);
        disabled.Enabled = false;
        disabled.Text = "Cannot be suggested";
        card.Controls.Add(CaptionRow("Disabled", disabled));

        card.Controls.Add(new XPlatLabel(
            "Typing opens the list under the caret. Up and Down move the highlight, Enter takes it, " +
            "Escape closes the list without clearing the text, and the box keeps the caret the whole " +
            "time. In the two append modes the rest of the first match is appended and selected, so " +
            "the next keystroke replaces it instead of adding to it.")
        {
            Dock = XDock.Top,
            AutoSize = false,
            Height = 66f,
            WrapText = true,
            Font = XPlatTheme.CaptionFont,
            ForeColorRole = XPlatThemeColor.MutedForeground,
            TextAlign = XPlatContentAlignment.TopLeft,
            Margin = new XPlatThickness(0f, 6f, 0f, 0f),
        });

        return card;
    }

    /// <summary>
    /// <see cref="XPlatTextBox"/> with <c>Multiline</c> on: a wrapped field holding more text than
    /// fits, an unwrapped one that scrolls sideways, and a wrapped one with autocomplete. The
    /// readout follows the caret's line and column, so the keyboard can be watched working.
    /// </summary>
    private XPlatPanel BuildMultilineDemo()
    {
        var card = Card();
        card.Controls.Add(Heading("Multi-line text"));

        var readout = ScrollCaption("Line 1, column 1 - type, press Enter, then move with the arrows");
        card.Controls.Add(readout);

        var wrapped = new XPlatTextBox
        {
            Dock = XDock.Top,
            Height = 96f,
            Multiline = true,
            PlaceholderText = "Write something",
            Text =
                "A multi-line text box wraps at word boundaries and lays its caret out on the visual " +
                "line it is drawn on rather than on a single logical line.\n\n" +
                "Enter inserts a line break and Ctrl+Enter is left for the form around it, because a " +
                "field that spans lines still needs an accept gesture. Tab still moves the focus. Up " +
                "and Down keep the column they started in, even across a short line, and Home and End " +
                "work on the visual line rather than on the whole value.\n\n" +
                "Drag the pointer across two lines to select them, then Ctrl+C and Ctrl+V: the line " +
                "breaks survive the round trip. The bar on the right appears because this text is " +
                "taller than the field, and the wheel scrolls it.",
        };

        wrapped.AddTextChangedHandler(_ => readout.Text = DescribeCaret(wrapped));
        wrapped.AddKeyUpHandler((_, _) => readout.Text = DescribeCaret(wrapped));
        wrapped.AddMouseUpHandler((_, _) => readout.Text = DescribeCaret(wrapped));
        card.Controls.Add(Caption("Wrapped (WordWrap = true)"));
        card.Controls.Add(wrapped);

        var unwrapped = new XPlatTextBox
        {
            Dock = XDock.Top,
            Height = 62f,
            Multiline = true,
            WordWrap = false,
            Text =
                "WordWrap = false keeps every logical line on one visual line and scrolls the field " +
                "sideways instead, the way a code editor does: this sentence is deliberately long so " +
                "that the horizontal bar at the bottom of the field has somewhere to go when the " +
                "caret reaches the end of it.",
        };

        unwrapped.AddTextChangedHandler(_ => readout.Text = DescribeCaret(unwrapped));
        unwrapped.AddKeyUpHandler((_, _) => readout.Text = DescribeCaret(unwrapped));
        card.Controls.Add(Caption("Unwrapped (WordWrap = false)"));
        card.Controls.Add(unwrapped);

        var completing = new XPlatTextBox
        {
            Dock = XDock.Top,
            Height = 78f,
            Multiline = true,
            AutoCompleteMode = XPlatAutoCompleteMode.SuggestAppend,
            AutoCompleteSource = XPlatAutoCompleteSource.CustomSource,
            PlaceholderText = "Type \"ap\" on any line",
        };

        foreach (string fruit in new[] { "Apple", "Apricot", "Banana", "Blueberry", "Cherry", "Mango", "Peach" })
        {
            completing.AutoCompleteCustomSource.Add(fruit);
        }

        card.Controls.Add(Caption("Autocomplete on a wrapped field"));
        card.Controls.Add(completing);

        card.Controls.Add(new XPlatLabel(
            "The suggestion list hangs from the caret's own line, so it opens where the user is typing " +
            "rather than under the bottom of a tall field. Taking a suggestion replaces the text on that " +
            "line and leaves every other line alone, and the append modes complete nothing while the " +
            "field spans lines, because a suffix cannot cross a line break.")
        {
            Dock = XDock.Top,
            AutoSize = false,
            Height = 52f,
            WrapText = true,
            Font = XPlatTheme.CaptionFont,
            ForeColorRole = XPlatThemeColor.MutedForeground,
            TextAlign = XPlatContentAlignment.TopLeft,
            Margin = new XPlatThickness(0f, 8f, 0f, 0f),
        });

        return card;
    }

    /// <summary>A small muted caption sitting above a field.</summary>
    private static XPlatLabel Caption(string text) => new(text)
    {
        Dock = XDock.Top,
        AutoSize = false,
        Height = 20f,
        Font = XPlatTheme.CaptionFont,
        ForeColorRole = XPlatThemeColor.MutedForeground,
        TextAlign = XPlatContentAlignment.BottomLeft,
    };

    /// <summary>The caret's logical line and column, and how much is selected, for the readout.</summary>
    private static string DescribeCaret(XPlatTextBox box)
    {
        var text = box.Text;
        int caret = Math.Clamp(box.CaretIndex, 0, text.Length);
        int line = 0;
        int lineStart = 0;

        for (int i = 0; i < caret; i++)
        {
            if (text[i] == '\n')
            {
                line++;
                lineStart = i + 1;
            }
        }

        int column = caret - lineStart + 1;
        int lines = 1;
        foreach (char c in text)
        {
            if (c == '\n')
            {
                lines++;
            }
        }

        return $"Line {line + 1} of {lines}, column {column} - {text.Length} characters, " +
            $"{box.SelectionLength} selected";
    }

    /// <summary>
    /// <see cref="XPlatTextBox"/> with annotation spans: a single-line field with three marked
    /// words, and a wrapped multi-line field whose longest span crosses a soft wrap so it is drawn
    /// on two visual lines. Right-clicking a squiggle reports the span that was hit, both in the
    /// context menu and in the readout above the fields.
    /// </summary>
    private XPlatPanel BuildSpanDemo()
    {
        var card = Card();
        card.Controls.Add(Heading("Text box spans"));

        var readout = ScrollCaption("Right-click a squiggle - the menu and this line name the span that was hit");
        card.Controls.Add(readout);

        const string sentence =
            "The quick brown fox jumpped over the lazzy dog, and a grammer checker noticed.";
        var single = new XPlatTextBox
        {
            Dock = XDock.Top,
            Height = 32f,
            Text = sentence,
            Spans =
            [
                Spelling(sentence, "jumpped"),
                Spelling(sentence, "lazzy"),
                Grammar(sentence, "grammer"),
            ],
        };
        AttachSpanMenu(single, readout);
        card.Controls.Add(Caption("Single line - two spelling spans and one grammar span"));
        card.Controls.Add(single);

        const string paragraph =
            "Spans are caller-owned: the box keeps the list by reference, never edits it, and clamps " +
            "a stale span where it is drawn instead of dropping it. This whole sentence is one " +
            "annotation, so its squiggly underline has to break at the end of the first visual line " +
            "and pick up again at the start of the next one - that is what a wrapped multi-line " +
            "field does, and it is why the marker is drawn once per visual line rather than once per " +
            "annotation.\n" +
            "Teh line below holds a short span of its own.";

        var wrapped = new XPlatTextBox
        {
            Dock = XDock.Top,
            Height = 122f,
            Multiline = true,
            Text = paragraph,
            Spans =
            [
                FromTo(paragraph, "This whole sentence", "a wrapped multi-line"),
                Grammar(paragraph, "Teh"),
            ],
        };
        card.Controls.Add(Caption("Multi-line and wrapped - the long span crosses a soft wrap"));
        card.Controls.Add(wrapped);
        AttachSpanMenu(wrapped, readout);

        card.Controls.Add(new XPlatLabel(
            "A span declares a start index, a length and how its squiggle is drawn - colour, stroke " +
            "weight, wave height and wavelength - and is meant to be subclassed, so a checker can hang " +
            "its suggestion list or rule id on it. Assigning Spans replaces the whole set by reference, " +
            "which is what a checker wants on every keystroke. The box never rewrites a span when the " +
            "text changes: an out-of-range one is clamped where it is drawn and an empty one is " +
            "skipped. The context menu event reports the span the right-click landed in through its " +
            "Span property, correctly after scrolling and after re-wrapping.")
        {
            Dock = XDock.Top,
            AutoSize = false,
            Height = 78f,
            WrapText = true,
            Font = XPlatTheme.CaptionFont,
            ForeColorRole = XPlatThemeColor.MutedForeground,
            TextAlign = XPlatContentAlignment.TopLeft,
            Margin = new XPlatThickness(0f, 8f, 0f, 0f),
        });

        return card;
    }

    /// <summary>A span over one word, marked the way a spelling checker marks it.</summary>
    private static XPlatTextBoxSpan Spelling(string text, string word) =>
        new(text.IndexOf(word, StringComparison.Ordinal), word.Length) { Tag = word };

    /// <summary>A span over one word in the accent colour, the way a grammar checker marks it.</summary>
    private static XPlatTextBoxSpan Grammar(string text, string word) =>
        new(text.IndexOf(word, StringComparison.Ordinal), word.Length)
        {
            Tag = word,
            ThemeColor = XPlatThemeColor.Accent,
        };

    /// <summary>A span from the start of one word to the end of another, for a run of text.</summary>
    private static XPlatTextBoxSpan FromTo(string text, string from, string to)
    {
        int start = text.IndexOf(from, StringComparison.Ordinal);
        int end = text.IndexOf(to, StringComparison.Ordinal) + to.Length;

        return new(start, Math.Max(0, end - start)) { Tag = text[start..end] };
    }

    /// <summary>
    /// <see cref="XPlatTextBox"/> with the undo history on. The page is built so the three things
    /// the feature promises can be seen: a burst of typing undoes as one step while a pause splits
    /// it, a replaced selection comes back with the selection it replaced, and the caret and the
    /// multi-line view return to where the edit was made. The readout follows
    /// <c>CanUndo</c>/<c>CanRedo</c> and the two counts, so a keystroke that did or did not join the
    /// run before it is visible.
    /// </summary>
    private XPlatPanel BuildUndoDemo()
    {
        var card = Card();
        card.Controls.Add(Heading("Undo and redo"));

        var readout = ScrollCaption("Type, pause for more than five seconds, type again - then Ctrl+Z twice, Ctrl+Y once (Cmd on macOS)");
        card.Controls.Add(readout);

        // Text first, then the history, so the page opens with an empty one and the readout starts
        // at "Undo 0" - the assignment itself would otherwise be the first entry.
        var single = new XPlatTextBox
        {
            Dock = XDock.Top,
            Height = 32f,
            Text = "The quick brown fox",
            UndoEnabled = true,
        };

        _undoFocusTarget = single;

        void Sync()
        {
            readout.Text =
                $"Undo {single.UndoCount} - Redo {single.RedoCount} - caret {single.CaretIndex}, " +
                $"{single.SelectionLength} selected - CanUndo {(single.CanUndo ? "yes" : "no")}, " +
                $"CanRedo {(single.CanRedo ? "yes" : "no")}";
        }

        single.AddTextChangedHandler(_ => Sync());
        single.AddKeyUpHandler((_, _) => Sync());
        single.AddMouseUpHandler((_, _) => Sync());

        // The history tells the page when it changed, which is what a real application would use to
        // enable its Undo and Redo commands - including when the change came from Ctrl+Z rather
        // than from a keystroke the page can see.
        single.AddUndoStateChangedHandler(_ => Sync());
        Sync();

        card.Controls.Add(Caption("Single line - a typing run, a pause, then a replaced selection"));
        card.Controls.Add(single);

        var row = ButtonRow();
        row.Controls.Add(CommandButton("Undo", () =>
        {
            SetStatus(single.Undo()
                ? "Undo applied - the caret and the selection came back with the text."
                : "Nothing to undo.");
            Sync();
        }));
        row.Controls.Add(CommandButton("Redo", () =>
        {
            SetStatus(single.Redo()
                ? "Redo applied - the caret went back to where the change left it."
                : "Nothing to redo.");
            Sync();
        }));
        row.Controls.Add(CommandButton("Select \"brown\"", () =>
        {
            single.Select(single.Text.IndexOf("brown", StringComparison.Ordinal), 5);
            SetStatus("Selected \"brown\" - type over it and undo puts the word, and the selection, back.");
            Sync();
        }));
        row.Controls.Add(CommandButton("Clear history", () =>
        {
            single.ClearUndoStack();
            SetStatus("ClearUndoStack() dropped both sides; the text is untouched.");
            Sync();
        }));
        row.Controls.Add(CommandButton("Lock field", () =>
        {
            single.ReadOnly = !single.ReadOnly;
            SetStatus(single.ReadOnly
                ? "Read-only: the history is kept, but CanUndo and CanRedo report no."
                : "Editable again: the history that was kept is available.");
            Sync();
        }));
        card.Controls.Add(row);

        // The rows are part of the control's own editing block, so a right-click shows them next to
        // Cut, Copy and Paste, and re-opening the menu after an undo is how the page shows Redo
        // lighting up.
        card.Controls.Add(Caption(
            "Right-click the field: Undo and Redo lead the editing block, disabled while the stack " +
            "is empty. Undo from the menu, then open it again - Redo is offered."));

        // A multi-line document: the history spans line breaks like any other character, and an
        // entry carries the view's scroll offsets with it, so undo comes back to the line it left.
        var document = new XPlatTextBox
        {
            Dock = XDock.Top,
            Height = 104f,
            Multiline = true,
            Text =
                "A multi-line field records an entry the same way, with the view's two scroll " +
                "offsets on either side of the change.\n\n" +
                "Type at the end of this line, scroll away with the wheel, then press Ctrl+Z: the " +
                "text loses the characters and the view returns to where they were typed.\n\n" +
                "Undo and redo arrive as ordinary text changes, so anything watching the value - a " +
                "checker, a binding, the autocomplete list - sees them the way it sees typing.\n\n" +
                "- A run survives a pause shorter than five seconds.\n" +
                "- A longer pause starts a new entry.\n" +
                "- Backspace coalesces on a run of its own.\n" +
                "- Typing over a selection is always one entry.\n" +
                "- The caret and the selection travel with the entry.\n" +
                "- Turning Multiline off drops the history.\n\n" +
                "The bar on the right is there because this document is taller than the field, which " +
                "is what makes the restored scroll offset visible.",
            UndoEnabled = true,
        };

        var documentReadout = Caption(DescribeCaret(document) + " - scroll 0");
        card.Controls.Add(documentReadout);
        document.AddTextChangedHandler(_ =>
            documentReadout.Text = $"{DescribeCaret(document)} - scroll {document.VerticalOffset:0}");
        document.AddKeyUpHandler((_, _) =>
            documentReadout.Text = $"{DescribeCaret(document)} - scroll {document.VerticalOffset:0}");
        card.Controls.Add(document);

        // The span contract in one line of code: the box never rewrites the caller's spans, so a
        // checker re-marks on the change - and an undo is a change like any other.
        const string marked = "The quick brown fox jumpped over the lazzy dog.";
        var spelled = new XPlatTextBox
        {
            Dock = XDock.Top,
            Height = 32f,
            Text = marked,
            UndoEnabled = true,
        };
        Remark(spelled);
        spelled.AddTextChangedHandler(_ => Remark(spelled));
        card.Controls.Add(Caption("Spans are re-marked after an undo - the checker runs on the change"));
        card.Controls.Add(spelled);

        var locked = new XPlatTextBox
        {
            Dock = XDock.Top,
            Height = 32f,
            Text = "Read-only: the history is kept, but CanUndo reports no",
            ReadOnly = true,
            UndoEnabled = true,
        };
        card.Controls.Add(Caption("Read-only"));
        card.Controls.Add(locked);

        card.Controls.Add(new XPlatLabel(
            "Each entry holds the caret and the selection on both sides of the change, so undo and " +
            "redo put the insertion point back where the user left it instead of at position 0 or " +
            "at the end. Characters typed within five seconds of the previous one join that " +
            "character's entry; a longer pause starts a new one; Backspace coalesces on a run of its " +
            "own, so typing and deleting never merge; and a change over a non-zero selection is " +
            "always one entry, so undo restores the replaced text rather than un-replacing one " +
            "character at a time. The history keeps the newest 100 entries and a bounded amount of " +
            "text, and it runs no timer: the window is measured from a clock, not from a tick.")
        {
            Dock = XDock.Top,
            AutoSize = false,
            Height = 78f,
            WrapText = true,
            Font = XPlatTheme.CaptionFont,
            ForeColorRole = XPlatThemeColor.MutedForeground,
            TextAlign = XPlatContentAlignment.TopLeft,
            Margin = new XPlatThickness(0f, 8f, 0f, 0f),
        });

        return card;
    }

    /// <summary>A button that docks into a <see cref="ButtonRow"/>.</summary>
    private static XPlatButton CommandButton(string text, Action onClick)
    {
        var button = new XPlatButton(text)
        {
            Dock = XDock.Left,
            AutoSize = true,
            Style = XPlatButtonStyle.Secondary,
            Margin = new XPlatThickness(0f, 0f, 6f, 0f),
        };

        button.AddClickHandler(_ => onClick());
        return button;
    }

    /// <summary>The words this page's miniature checker marks, standing in for a real one.</summary>
    private static readonly string[] Misspellings = ["jumpped", "lazzy", "grammer", "teh"];

    /// <summary>
    /// Re-computes the squiggles from the current text and hands the list over. This is the whole
    /// of the application's side of the span contract, and it is what makes an undo look right:
    /// the box never edits the list, so the checker assigns a new one whenever the text changes.
    /// </summary>
    private static void Remark(XPlatTextBox box)
    {
        var text = box.Text;
        List<XPlatTextBoxSpan> spans = [];

        foreach (string word in Misspellings)
        {
            int at = text.IndexOf(word, StringComparison.Ordinal);
            if (at >= 0)
            {
                spans.Add(new XPlatTextBoxSpan(at, word.Length) { Tag = word });
            }
        }

        // An empty list removes the feature again, so a corrected field costs nothing.
        box.Spans = spans;
    }

    /// <summary>
    /// Gives one field the right-click behaviour of a text box that carries annotations, the way an
    /// application would write it: the handler is attached to the control, so nothing outside the
    /// control has to know which field was pressed, and it only <em>adds</em> to the menu the box
    /// contributed. The result reads like an editor: the suggestions for the marked run, a
    /// separator, then the ordinary Cut/Copy/Paste/Select All. The page-level menu stays a host
    /// concern because it belongs to the window rather than to any one control.
    /// </summary>
    private void AttachSpanMenu(XPlatTextBox box, XPlatLabel readout)
    {
        box.AddContextMenuOpeningHandler(args =>
        {
            if (args.Span is not { } span || span.Tag is not string marked)
            {
                readout.Text = "The press missed every span - Span was null, so only the editing commands are offered";
                return;
            }

            string word = marked.Length <= 24 ? marked : marked[..21] + "...";
            readout.Text = $"Right-click hit the span on \"{word}\" - start {span.Start}, length {span.Length}";

            var menu = args.Menu ??= new XPlatContextMenu();

            // In front of the editing block, in reading order, then one separator between the two.
            menu.Insert(0, new XPlatMenuItem("&Add to dictionary", () => SetStatus($"\"{word}\" added to the dictionary.")));
            menu.Insert(0, new XPlatMenuItem($"&Ignore \"{word}\"", () => SetStatus($"\"{word}\" added to the ignore list.")));
            menu.Insert(0, new XPlatMenuItem($"&Replace \"{word}\"", () => SetStatus($"Replace picked for \"{word}\".")));
            menu.Insert(3, XPlatMenuItem.Separator);
        });
    }

    /// <summary>
    /// Right-clicks a squiggle on the spans page as if the pointer had gone there, which is what a
    /// screenshot needs in order to show the context menu - and the readout - naming the span. It
    /// goes through the same host entry point the platform uses, so it proves the dispatch, not a
    /// shortcut around it.
    /// </summary>
    /// <param name="window">The window the sample is showing in.</param>
    public void OpenSpanDemo(XPlatWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);

        // Nothing has painted yet, so the page has no bounds until something lays it out.
        window.Host.PerformLayout();

        if (FindSpannedBox(Root) is not { Spans: { Count: > 0 } spans } box)
        {
            return;
        }

        // The last visual-line segment rather than the first, so the picture proves that a span
        // which crosses a soft wrap still resolves from the line it wrapped onto.
        var rects = new List<SKRect>();
        box.GetSelectionRects(spans[0].Start, spans[0].Length, rects);
        if (rects.Count == 0)
        {
            return;
        }

        var segment = rects[^1];
        var origin = box.GetPositionInHost();
        window.Host.ProcessContextMenu(new SKPoint(origin.X + segment.MidX, origin.Y + segment.MidY));
    }

    /// <summary>
    /// The field the screenshot path presses in: the wrapped one when the page has one, so a span
    /// that crosses a soft wrap is the one that gets resolved, and any annotated field otherwise.
    /// </summary>
    private static XPlatTextBox? FindSpannedBox(XPlatControl control)
    {
        var found = Search(control, multilineOnly: true) ?? Search(control, multilineOnly: false);
        return found;

        static XPlatTextBox? Search(XPlatControl node, bool multilineOnly)
        {
            if (node is XPlatTextBox { Spans: { Count: > 0 } } box && (!multilineOnly || box.Multiline))
            {
                return box;
            }

            foreach (var child in node.Controls)
            {
                if (Search(child, multilineOnly) is { } match)
                {
                    return match;
                }
            }

            return null;
        }
    }

    /// <summary>A text box over a custom source, with the values the suggestion list filters.</summary>
    private static XPlatTextBox AutoCompleteBox(XPlatAutoCompleteMode mode, IEnumerable<string> values)
    {
        var box = new XPlatTextBox
        {
            AutoCompleteMode = mode,
            AutoCompleteSource = XPlatAutoCompleteSource.CustomSource,
        };

        foreach (string value in values)
        {
            box.AutoCompleteCustomSource.Add(value);
        }

        return box;
    }

    /// <summary>A caption with a wide field on the right, so a column of fields lines up.</summary>
    private static XPlatPanel CaptionRow(string caption, XPlatControl field)
    {
        var row = new XPlatPanel
        {
            Dock = XDock.Top,
            Height = 34f,
            ShowFrame = false,
            IsCard = false,
            BackColor = null,
            BorderWidth = 0f,
            CornerRadius = 0f,
            Padding = new XPlatThickness(0f),
            Margin = new XPlatThickness(0f, 0f, 0f, 10f),
        };

        row.Controls.Add(new XPlatLabel(caption)
        {
            Dock = XDock.Fill,
            AutoSize = false,
            Font = XPlatTheme.DefaultFont,
            TextAlign = XPlatContentAlignment.MiddleLeft,
            Margin = new XPlatThickness(0f, 0f, 12f, 0f),
        });

        field.Dock = XDock.Right;
        field.Width = 240f;
        row.Controls.Add(field);

        return row;
    }

    /// <summary>
    /// A tab control whose pages hold real controls, plus a second one with more tabs than fit so
    /// the strip has to scroll.
    /// </summary>
    private XPlatPanel BuildTabControlDemo()
    {
        var card = Card();
        card.Controls.Add(Heading("Tab controls"));

        var readout = ScrollCaption("Selected: General");
        card.Controls.Add(readout);

        var tabs = new XPlatTabControl
        {
            Dock = XDock.Top,
            Height = 220f,
            Margin = new XPlatThickness(0f, 0f, 0f, 16f),
        };

        var general = new XPlatTabPage("General");
        general.Controls.Add(new XPlatCheckBox("Show the welcome screen")
        {
            Dock = XDock.Top,
            Checked = true,
            Margin = new XPlatThickness(0f, 0f, 0f, 6f),
        });
        general.Controls.Add(new XPlatCheckBox("Reopen the last page")
        {
            Dock = XDock.Top,
            Margin = new XPlatThickness(0f, 0f, 0f, 6f),
        });
        general.Controls.Add(new XPlatLabel("Pages are ordinary panels: they dock, scroll and lay out like any other.")
        {
            Dock = XDock.Fill,
            AutoSize = false,
            WrapText = true,
            Font = XPlatTheme.CaptionFont,
            ForeColorRole = XPlatThemeColor.MutedForeground,
            TextAlign = XPlatContentAlignment.TopLeft,
        });

        var appearance = new XPlatTabPage("Appearance");
        appearance.Controls.Add(new XPlatComboBox
        {
            Dock = XDock.Top,
            Height = 32f,
            Margin = new XPlatThickness(0f, 0f, 0f, 8f),
        });
        appearance.Controls.Add(new XPlatProgressBar
        {
            Dock = XDock.Top,
            Height = 18f,
            Value = 45f,
            Margin = new XPlatThickness(0f, 0f, 0f, 8f),
        });
        appearance.Controls.Add(DemoButton("A button on a page", () => SetStatus("A button on the Appearance page.")));

        var advanced = new XPlatTabPage("Advanced");
        advanced.Controls.Add(new XPlatTextBox { Dock = XDock.Top, Height = 32f, Margin = new XPlatThickness(0f, 0f, 0f, 8f) });
        advanced.Controls.Add(new XPlatLabel("Tab and the arrow keys move between pages.")
        {
            Dock = XDock.Fill,
            AutoSize = false,
            WrapText = true,
            Font = XPlatTheme.CaptionFont,
            ForeColorRole = XPlatThemeColor.MutedForeground,
            TextAlign = XPlatContentAlignment.TopLeft,
        });

        tabs.TabPages.Add(general);
        tabs.TabPages.Add(appearance);
        tabs.TabPages.Add(advanced);
        tabs.SelectedIndex = 0;
        tabs.AddSelectedIndexChangedHandler(_ => readout.Text = $"Selected: {tabs.SelectedTab?.Text ?? "none"}");
        card.Controls.Add(tabs);

        card.Controls.Add(ScrollCaption("More tabs than fit, so the strip scrolls with the wheel"));

        var crowded = new XPlatTabControl
        {
            Dock = XDock.Fill,
            Margin = new XPlatThickness(0f, 0f, 0f, 0f),
        };

        foreach (string name in new[]
                 {
                     "One", "Two", "Three", "Four", "Five", "Six", "Seven", "Eight",
                     "Nine", "Ten", "Eleven", "Twelve",
                 })
        {
            var page = new XPlatTabPage(name);
            page.Controls.Add(new XPlatLabel($"Page {name}")
            {
                Dock = XDock.Fill,
                AutoSize = false,
                TextAlign = XPlatContentAlignment.MiddleCenter,
            });
            crowded.TabPages.Add(page);
        }

        card.Controls.Add(crowded);
        return card;
    }

    /// <summary>
    /// One <see cref="XPlatToolTip"/> shared by every control on the card, which is the shape the
    /// Windows Forms component has: the tooltip is not a child of anything, it follows the pointer.
    /// </summary>
    private XPlatPanel BuildToolTipDemo()
    {
        var card = Card();
        card.Controls.Add(Heading("Tool tips"));

        var toolTip = new XPlatToolTip { InitialDelay = 400, AutoPopDelay = 5000 };

        card.Controls.Add(ScrollCaption("Hover a control to see its tip"));

        var primary = new XPlatButton("Save")
        {
            Dock = XDock.Top,
            AutoSize = true,
            Style = XPlatButtonStyle.Primary,
            Margin = new XPlatThickness(0f, 0f, 0f, 8f),
        };
        toolTip.SetToolTip(primary, "Write the document to disk.");
        card.Controls.Add(primary);

        var secondary = new XPlatButton("Save as")
        {
            Dock = XDock.Top,
            AutoSize = true,
            Style = XPlatButtonStyle.Secondary,
            Margin = new XPlatThickness(0f, 0f, 0f, 8f),
        };
        toolTip.SetToolTip(secondary, "Write the document to a new file.");
        card.Controls.Add(secondary);

        var longTip = new XPlatButton("A long tip")
        {
            Dock = XDock.Top,
            AutoSize = true,
            Style = XPlatButtonStyle.Ghost,
            Margin = new XPlatThickness(0f, 0f, 0f, 12f),
        };

        // The tooltip measures itself and wraps at MaximumWidth, so a long string stays readable
        // instead of becoming one very wide line.
        toolTip.SetToolTip(longTip, "This tip is long enough that it has to wrap onto more than one line, which is what MaximumWidth is for.");
        card.Controls.Add(longTip);

        var field = new XPlatTextBox
        {
            Dock = XDock.Top,
            Height = 32f,
            Text = "Hover me",
            Margin = new XPlatThickness(0f, 0f, 0f, 12f),
        };
        toolTip.SetToolTip(field, "Any control can carry a tip, not only buttons.");
        card.Controls.Add(field);

        var toggled = new XPlatCheckBox("Disable the tips")
        {
            Dock = XDock.Top,
            Margin = new XPlatThickness(0f, 0f, 0f, 12f),
        };
        toggled.AddCheckedChangedHandler(_ => toolTip.Active = !toggled.Checked);
        card.Controls.Add(toggled);

        card.Controls.Add(new XPlatLabel(
            "A tip is shown in a popup that never takes the pointer, so the control under it keeps the hover.")
        {
            Dock = XDock.Fill,
            AutoSize = false,
            WrapText = true,
            Font = XPlatTheme.CaptionFont,
            ForeColorRole = XPlatThemeColor.MutedForeground,
            TextAlign = XPlatContentAlignment.TopLeft,
        });

        return card;
    }

    /// <summary>Shows <paramref name="message"/> in the footer's status line.</summary>
    /// <param name="message">The text to show.</param>
    /// <remarks>
    /// Public so a host can report something the sample UI did not cause itself, such as a second
    /// launch being handed to the running window.
    /// </remarks>
    public void SetStatus(string message) => _statusLabel.Text = message;

    /// <summary>
    /// Reports a drop on the window itself: the files that were dragged in from the desktop, or the
    /// text that was dragged in from another application.
    /// </summary>
    private void OnRootDrop(XPlatDragEventArgs e)
    {
        var files = e.Data.GetFiles();
        if (files.Length > 0)
        {
            var names = string.Join(", ", files.Select(Path.GetFileName));
            SetStatus($"Dropped {files.Length} file(s): {names}");
            e.Effect = XPlatDragDropEffects.Copy;
            return;
        }

        var text = e.Data.GetText();
        if (!string.IsNullOrEmpty(text))
        {
            SetStatus($"Dropped text: {Preview(text)}");
            e.Effect = XPlatDragDropEffects.Copy;
            return;
        }

        SetStatus("Dropped something this sample cannot read.");
        e.Effect = XPlatDragDropEffects.None;
    }

    /// <summary>Shortens dropped text to one status-line's worth.</summary>
    private static string Preview(string text)
    {
        var single = text.ReplaceLineEndings(" ");
        return single.Length <= 60 ? single : string.Concat(single.AsSpan(0, 60), "...");
    }

    private static SKImage CreateGradientImage(int width, int height)
    {
        var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        var canvas = surface.Canvas;

        using (var shader = SKShader.CreateLinearGradient(
                   new SKPoint(0f, 0f),
                   new SKPoint(width, height),
                   new[] { new SKColor(0xFF2F6FED), new SKColor(0xFF7C3AED), new SKColor(0xFFEC4899) },
                   SKShaderTileMode.Clamp))
        using (var paint = new SKPaint { Shader = shader, IsAntialias = true })
        {
            canvas.DrawRect(new SKRect(0f, 0f, width, height), paint);
        }

        using (var paint = new SKPaint { Color = new SKColor(0x33FFFFFF), IsAntialias = true })
        {
            canvas.DrawCircle(width * 0.22f, height * 0.3f, height * 0.28f, paint);
            canvas.DrawCircle(width * 0.78f, height * 0.72f, height * 0.36f, paint);
        }

        using (var font = new SKFont(SKTypeface.Default, 30f) { Edging = SKFontEdging.Antialias })
        using (var paint = new SKPaint { Color = SKColors.White, IsAntialias = true })
        {
            canvas.DrawText("SkiaSharp", 22f, height - 26f, SKTextAlign.Left, font, paint);
        }

        canvas.Flush();
        return surface.Snapshot();
    }

    /// <summary>
    /// A face that travels with the application instead of coming from the machine: every control on
    /// this page draws with IBM Plex Sans Regular read out of the assembly's resources, and the
    /// readout compares its measurements with the machine's own face.
    /// </summary>
    private XPlatPanel BuildFontDemo()
    {
        const string sample = "XPlatUI";

        // The page owns these fonts for the life of the window: a control never disposes a font it was
        // handed, and the theme fonts are held the same way.
        var loaded = XPlatFont.FromBytes(DemoFont.Data);
        var system = new XPlatFont(loaded.Size);

        var card = Card();
        card.Controls.Add(Heading("Fonts from a file"));
        card.Controls.Add(ScrollCaption(
            $"{loaded.FamilyName}, {F(loaded.Size)} pt, read from this app's embedded resources - not a family this machine has"));

        card.Controls.Add(Caption("A label"));
        card.Controls.Add(new XPlatLabel(
            "This label draws with the face in the file. Its glyph advances, line height and baseline " +
            "are the same on Windows, macOS and Linux, which a family name cannot promise: the same name " +
            "resolves to a different physical face on each desktop.")
        {
            Dock = XDock.Top,
            AutoSize = false,
            Height = 62f,
            WrapText = true,
            Font = loaded,
            TextAlign = XPlatContentAlignment.TopLeft,
        });

        card.Controls.Add(Caption("A multi-line text box"));
        card.Controls.Add(new XPlatTextBox
        {
            Dock = XDock.Top,
            Height = 108f,
            Multiline = true,
            Font = loaded,
            Text =
                "The caret, the wrap points and the selection geometry all come from the same face, so a " +
                "test that pins this file sees the same layout on every platform.\n\n" +
                "Nothing is looked up by name here: the bytes are the face. That is what makes a golden " +
                "image possible, and what lets a trimmed application carry its own brand.",
        });

        card.Controls.Add(Caption("A rich label: spans change size and colour and stay on this face"));
        var rich = new XPlatRichLabel
        {
            Dock = XDock.Top,
            AutoSize = true,
            WrapText = true,
            Font = loaded,
        };

        rich.AddText("The same face in ");
        rich.AddSpan(new XPlatTextSpan("a larger size") { FontSize = 17f });
        rich.AddText(", in ");
        rich.AddSpan(new XPlatTextSpan("the accent colour") { ThemeColor = XPlatThemeColor.Accent });
        rich.AddText(", and in ");
        rich.AddSpan(new XPlatTextSpan("a bold span") { Bold = true });
        rich.AddText(
            " - which stays this face rather than falling back to a system font, because one file holds " +
            "one face and no weight is invented.");
        card.Controls.Add(rich);

        card.Controls.Add(new XPlatLabel(
            $"Measured here: \"{sample}\" is {F(loaded.MeasureWidth(sample))} px in the loaded face and " +
            $"{F(system.MeasureWidth(sample))} px in the system face ({system.FamilyName}); line height " +
            $"{F(loaded.LineHeight)} against {F(system.LineHeight)}.")
        {
            Dock = XDock.Fill,
            AutoSize = false,
            WrapText = true,
            Font = XPlatTheme.CaptionFont,
            ForeColorRole = XPlatThemeColor.MutedForeground,
            TextAlign = XPlatContentAlignment.TopLeft,
            Margin = new XPlatThickness(0f, 10f, 0f, 0f),
        });

        return card;

        static string F(float value) => value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The inherited appearance page: one panel that assigns a font and a foreground, what its
    /// descendants make of it, one control that overrides both inside the subtree, and a label that
    /// paints nothing on top of an ancestor that does. The buttons change the appearance of a live
    /// tree - the panel's font, or the whole theme's face - and the text re-lays itself out.
    /// </summary>
    private XPlatPanel BuildAppearanceDemo()
    {
        // The page owns these for the life of the window: a control never disposes a font it was
        // handed, and the theme keeps the face it is given.
        _appearanceSubtreeFont ??= XPlatFont.FromBytes(DemoFont.Data, 14f);
        _appearanceBigFont ??= XPlatFont.FromBytes(DemoFont.Data, 21f);
        _appearanceFace ??= XPlatFont.FromBytes(DemoFont.Data);

        var card = Card();
        card.Controls.Add(Heading("Inherited appearance"));
        card.Controls.Add(new XPlatLabel(
            "Font, foreground and background are inherited down the tree, the way they are in WinForms. " +
            "A control that assigns one answers for itself and for everything inside it; one that " +
            "assigns none asks its parent and then falls back to its class default. Nothing is captured " +
            "when a control is built, so changing the theme - or one panel's font - moves a live tree.")
        {
            Dock = XDock.Top,
            AutoSize = false,
            Height = 56f,
            WrapText = true,
            Font = XPlatTheme.CaptionFont,
            ForeColorRole = XPlatThemeColor.MutedForeground,
            TextAlign = XPlatContentAlignment.TopLeft,
            Margin = new XPlatThickness(0f, 0f, 0f, 10f),
        });

        var buttons = ButtonRow();
        buttons.Height = 32f;

        var grow = new XPlatButton("Grow the panel's font")
        {
            Dock = XDock.Left,
            AutoSize = true,
            Style = XPlatButtonStyle.Secondary,
            Margin = new XPlatThickness(0f, 0f, 8f, 0f),
        };
        grow.AddClickHandler(_ => SetAppearanceDemo(size: 21f));

        var themeFace = new XPlatButton("Use the bundled face everywhere")
        {
            Dock = XDock.Left,
            AutoSize = true,
            Style = XPlatButtonStyle.Secondary,
            Margin = new XPlatThickness(0f, 0f, 8f, 0f),
        };
        themeFace.AddClickHandler(_ =>
        {
            XPlatTheme.UseFont(_appearanceFace);
            UpdateAppearanceReadout();
        });

        var reset = new XPlatButton("Reset")
        {
            Dock = XDock.Left,
            AutoSize = true,
            Style = XPlatButtonStyle.Secondary,
        };
        reset.AddClickHandler(_ =>
        {
            _appearanceStage?.ClearFont();
            XPlatTheme.FontFamily = null;
            UpdateAppearanceReadout();
        });

        buttons.Controls.Add(grow);
        buttons.Controls.Add(themeFace);
        buttons.Controls.Add(reset);
        card.Controls.Add(buttons);

        _appearanceReadout = new XPlatLabel
        {
            Dock = XDock.Top,
            AutoSize = false,
            Height = 62f,
            WrapText = true,
            Font = XPlatTheme.CaptionFont,
            ForeColorRole = XPlatThemeColor.MutedForeground,
            TextAlign = XPlatContentAlignment.TopLeft,
            Margin = new XPlatThickness(0f, 0f, 0f, 8f),
        };
        card.Controls.Add(_appearanceReadout);

        // The subtree: a panel that assigns a font, a foreground colour and a background role. Every
        // control added below inherits all three unless it says otherwise.
        var stage = new XPlatPanel
        {
            Dock = XDock.Top,
            Height = 214f,
            Padding = new XPlatThickness(12f, 8f),
            CornerRadius = 8f,
            BorderWidth = 1f,
            Font = _appearanceSubtreeFont,
            Foreground = XPlatTheme.Accent,
            Background = XPlatBackground.FromThemeColor(XPlatThemeColor.Secondary),
        };
        _appearanceStage = stage;

        var inherited = new XPlatLabel("Inherits the panel's font, colour and background role.")
        {
            Dock = XDock.Top,
            AutoSize = false,
            Height = 34f,
        };
        _appearanceInherited = inherited;

        // A panel that paints a background of its own and assigns nothing: it takes the colour its
        // parent assigned instead of its own surface, which is the inheritance doing its work.
        var nested = new XPlatPanel
        {
            Dock = XDock.Top,
            Height = 76f,
            Padding = new XPlatThickness(8f, 6f),
            CornerRadius = 6f,
            BorderWidth = 1f,
        };
        _appearanceNested = nested;

        var noBackground = new XPlatLabel(
            "This label paints nothing at all, so the colour behind it - the panel's, inherited one " +
            "level up - is what shows. Its background resolves to None, not to a colour of its own.")
        {
            Dock = XDock.Fill,
            AutoSize = false,
            WrapText = true,
            TextAlign = XPlatContentAlignment.TopLeft,
        };
        _appearanceLabel = noBackground;
        nested.Controls.Add(noBackground);

        // The one control that overrides the subtree it sits in.
        var overrides = new XPlatLabel("This one assigns its own font and colour.")
        {
            Dock = XDock.Top,
            AutoSize = false,
            Height = 24f,
            Font = XPlatTheme.CaptionFont,
            ForeColorRole = XPlatThemeColor.MutedForeground,
        };
        _appearanceOverrides = overrides;

        var button = new XPlatButton("A button under the same panel")
        {
            Dock = XDock.Top,
            AutoSize = true,
            Style = XPlatButtonStyle.Ghost,
            Margin = new XPlatThickness(0f, 6f, 0f, 0f),
        };

        stage.Controls.Add(inherited);
        stage.Controls.Add(nested);
        stage.Controls.Add(overrides);
        stage.Controls.Add(button);
        card.Controls.Add(stage);

        card.Controls.Add(new XPlatLabel(
            "Three background states, because two would be wrong: unset means \"ask my parent\", a " +
            "colour answers for this control and everything under it, and \"paint nothing\" is a value " +
            "of its own - a label keeps painting nothing wherever it sits, so it never gains a box it " +
            "did not ask for. Only a colour is inherited: a layout container that paints nothing must " +
            "not take the card inside it away with it.\n\n" +
            "Nothing here is captured at construction. The effective font is read when text is " +
            "measured and painted; what the parent chain contributes is walked once and cached, and " +
            "dropped when an ancestor assigns a value or a control changes parent. Palette roles are " +
            "resolved at paint time, so a palette swap is still just a repaint - but a font is the " +
            "size of the text, so a font change lays the tree out again before the next frame.")
        {
            Dock = XDock.Fill,
            AutoSize = false,
            WrapText = true,
            Font = XPlatTheme.CaptionFont,
            ForeColorRole = XPlatThemeColor.MutedForeground,
            TextAlign = XPlatContentAlignment.TopLeft,
            Margin = new XPlatThickness(0f, 12f, 0f, 0f),
        });

        UpdateAppearanceReadout();
        return card;
    }

    /// <summary>
    /// Changes the font of the panel the demo subtree hangs off, which is the runtime half of the
    /// page: the controls already exist, and their text is measured again at the new size.
    /// </summary>
    /// <param name="size">The size to give the subtree, or null to let it inherit again.</param>
    public void SetAppearanceDemo(float? size)
    {
        if (!size.HasValue)
        {
            _appearanceStage?.ClearFont();
        }
        else if (_appearanceStage is not null && _appearanceBigFont is not null)
        {
            _appearanceStage.Font = size.Value >= 21f ? _appearanceBigFont : _appearanceSubtreeFont!;
        }

        UpdateAppearanceReadout();
    }

    /// <summary>Switches the application's font to the bundled face, or back to the system's.</summary>
    /// <param name="bundled">True for the face the application carries, false for the platform's.</param>
    public void SetAppearanceDemoFace(bool bundled)
    {
        if (bundled && _appearanceFace is not null)
        {
            XPlatTheme.UseFont(_appearanceFace);
        }
        else
        {
            XPlatTheme.FontFamily = null;
        }

        UpdateAppearanceReadout();
    }

    /// <summary>
    /// Prints what the tree resolves to right now: the background state of each level, the font size
    /// the labels ended up with, and the size of the text - which is what changes when a font does.
    /// </summary>
    private void UpdateAppearanceReadout()
    {
        if (_appearanceReadout is null)
        {
            return;
        }

        _appearanceReadout.Text =
            $"panel: {Describe(_appearanceStage)}   |   " +
            $"inherited label: {Describe(_appearanceInherited)}   |   " +
            $"nested panel: {Describe(_appearanceNested)}\n" +
            $"label that paints nothing: {Describe(_appearanceLabel)}\n" +
            $"override inside the subtree: {Describe(_appearanceOverrides)}   |   " +
            $"inherited label text: {S(_appearanceInherited is { } label ? label.Font.MeasureWidth(label.Text) : null)} px wide, " +
            $"{S(_appearanceInherited?.Font.LineHeight)} px line height";

        static string Describe(XPlatControl? control)
        {
            if (control is null)
            {
                return "-";
            }

            string background = control.Background switch
            {
                { IsNone: true } => "none",
                { ThemeColor: { } role } => role.ToString(),
                { Color: { } color } => $"#{color.Alpha:X2}{color.Red:X2}{color.Green:X2}{color.Blue:X2}",
                _ => "inherit",
            };

            return $"{control.Font.Size:0.#} pt, {background}";
        }

        static string S(float? value) =>
            value is { } v ? v.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) : "-";
    }

    /// <summary>
    /// A page whose only job is to be checked by hand. Creation order and <c>TabIndex</c> order
    /// disagree on purpose, a nested container orders its own children, a focusable container is
    /// entered as a unit, two controls are interleaved by index but meant to be skipped, two share
    /// an index, and the readout names whatever has the focus so a person can Tab and read the
    /// order rather than guess it.
    /// </summary>
    private XPlatPanel BuildTabOrderDemo()
    {
        var section = new XPlatPanel
        {
            Dock = XDock.Fill,
            ShowFrame = false,
            IsCard = false,
            BackColor = null,
            BorderWidth = 0f,
            CornerRadius = 0f,
            Padding = new XPlatThickness(0f),
        };

        // The stops in the order Tab should reach them, filled in after the form is built. The
        // readout reads this list, so if the walk ever disagrees with it the page says "it is meant
        // to be skipped" instead of quietly describing whatever the library did.
        var expected = new List<(XPlatControl Control, string Name)>();
        var skipped = new List<(XPlatControl Control, string Name)>();

        var readout = new XPlatLabel("Focus: nothing yet - press Tab to land on 1 Country.")
        {
            Dock = XDock.Top,
            AutoSize = false,
            Height = 20f,
            TextAlign = XPlatContentAlignment.MiddleLeft,
            Margin = new XPlatThickness(0f, 0f, 0f, 6f),
        };

        void Refresh()
        {
            foreach (var (control, name) in skipped)
            {
                if (control.IsFocused)
                {
                    readout.Text = $"Focus: {name} - it is meant to be skipped!";
                    return;
                }
            }

            for (int i = 0; i < expected.Count; i++)
            {
                if (expected[i].Control.IsFocused)
                {
                    readout.Text =
                        $"Focus: {expected[i].Name} - TabIndex {expected[i].Control.TabIndex} - " +
                        $"tab stop {i + 1} of {expected.Count}";
                    return;
                }
            }

            readout.Text = "Focus: nothing on this page - press Tab to land on 1 Country.";
        }

        void Watch(XPlatControl control, string name, bool isSkipped = false)
        {
            (isSkipped ? skipped : expected).Add((control, name));
            control.AddGotFocusHandler(_ => Refresh());
            control.AddLostFocusHandler(_ => Refresh());
        }

        // A label and its control on one line, both placed by hand. A docked row panel would be a
        // container of its own, and its index - not the control's - would decide the order, which is
        // exactly the thing this page exists to show correctly.
        static void Place(XPlatPanel parent, string caption, XPlatControl control, float y)
        {
            parent.Controls.Add(new XPlatLabel(caption)
            {
                AutoSize = false,
                Bounds = new SKRect(0f, y, 330f, y + 28f),
                Font = XPlatTheme.CaptionFont,
                ForeColorRole = XPlatThemeColor.MutedForeground,
                TextAlign = XPlatContentAlignment.MiddleLeft,
            });

            control.Bounds = new SKRect(338f, y, 628f, y + 28f);
            parent.Controls.Add(control);
        }

        var form = Card();
        form.Dock = XDock.Left;
        form.Width = 660f;
        form.Margin = new XPlatThickness(0f, 0f, 16f, 0f);
        form.Controls.Add(Heading("Tab order"));
        form.Controls.Add(new XPlatLabel(
            "Press Tab repeatedly and watch the readout - and Shift+Tab to walk the same order " +
            "backwards. The order is Country, Street, Check address, Email, Standard shipping, Full " +
            "name, the scrollable container, Inside the container, Save. That is not the order these " +
            "controls were created in: they were created top to bottom, and Tab follows TabIndex. " +
            "After Save the walk leaves the page for the window's own controls and wraps back to Country.")
        {
            Dock = XDock.Top,
            AutoSize = false,
            Height = 60f,
            WrapText = true,
            Font = XPlatTheme.CaptionFont,
            ForeColorRole = XPlatThemeColor.MutedForeground,
            TextAlign = XPlatContentAlignment.TopLeft,
            Margin = new XPlatThickness(0f, 0f, 0f, 6f),
        });
        form.Controls.Add(readout);

        // Every stop is a direct child of this panel, placed by hand: layout must not invent
        // containers that the Tab walk would then have to order.
        var rows = new XPlatPanel
        {
            Dock = XDock.Top,
            Height = 436f,
            ShowFrame = false,
            IsCard = false,
            BackColor = null,
            BorderWidth = 0f,
            CornerRadius = 0f,
            Padding = new XPlatThickness(0f),
        };
        form.Controls.Add(rows);

        // Created first, visited sixth.
        var fullName = new XPlatTextBox { TabIndex = 30 };
        Place(rows, "Created 1st - tab 6 - Full name (TabIndex 30)", fullName, 0f);

        // Created second, visited first.
        var country = new XPlatComboBox { TabIndex = 10 };
        country.Items.Add("United Kingdom");
        country.Items.Add("United States");
        country.Items.Add("Germany");
        Place(rows, "Created 2nd - tab 1 - Country (TabIndex 10)", country, 34f);

        // Equal indices: this one is created before the radio button, so the tie-break puts it first.
        var email = new XPlatCheckBox("Email me a receipt") { AutoSize = false, TabIndex = 20 };
        Place(rows, "Created 3rd - tab 4 - Email (TabIndex 20, tie)", email, 68f);

        var standard = new XPlatRadioButton("Standard shipping") { AutoSize = false, TabIndex = 20 };
        Place(rows, "Created 4th - tab 5 - Standard (TabIndex 20, tie)", standard, 102f);

        // A nested container: not a stop itself, so it takes the place its own index gives it in the
        // outer order (15) and its children order among themselves.
        var addressPanel = new XPlatPanel
        {
            TabIndex = 15,
            Bounds = new SKRect(0f, 136f, 628f, 232f),
            IsCard = true,
            ShowFrame = true,
            BorderWidth = 1f,
            CornerRadius = 8f,
            Padding = new XPlatThickness(8f),
        };
        rows.Controls.Add(addressPanel);
        addressPanel.Controls.Add(new XPlatLabel(
            "Created 5th - TabIndex 15 - a nested container: its children order among themselves, 10 then 20.")
        {
            AutoSize = false,
            Bounds = new SKRect(0f, 0f, 612f, 18f),
            Font = XPlatTheme.CaptionFont,
            ForeColorRole = XPlatThemeColor.MutedForeground,
            TextAlign = XPlatContentAlignment.TopLeft,
        });

        // Created in the reverse of the order they are visited, so the container's own indices - not
        // creation order - explain the result.
        var checkAddress = new XPlatButton("Check address") { AutoSize = false, TabIndex = 20 };
        var street = new XPlatTextBox { TabIndex = 10 };

        addressPanel.Controls.Add(new XPlatLabel("Created 5a - tab 3 - Check address (TabIndex 20)")
        {
            AutoSize = false,
            Bounds = new SKRect(0f, 22f, 300f, 50f),
            Font = XPlatTheme.CaptionFont,
            ForeColorRole = XPlatThemeColor.MutedForeground,
            TextAlign = XPlatContentAlignment.MiddleLeft,
        });
        checkAddress.Bounds = new SKRect(308f, 22f, 540f, 50f);
        addressPanel.Controls.Add(checkAddress);

        addressPanel.Controls.Add(new XPlatLabel("Created 5b - tab 2 - Street (TabIndex 10)")
        {
            AutoSize = false,
            Bounds = new SKRect(0f, 56f, 300f, 84f),
            Font = XPlatTheme.CaptionFont,
            ForeColorRole = XPlatThemeColor.MutedForeground,
            TextAlign = XPlatContentAlignment.MiddleLeft,
        });
        street.Bounds = new SKRect(308f, 56f, 540f, 84f);
        addressPanel.Controls.Add(street);

        // Created here, but their indices put them between real stops - and they are skipped, which
        // is the point. One line so the page still fits above the fold.
        var notATabStop = new XPlatButton("Not a tab stop") { AutoSize = false, TabStop = false, TabIndex = 25 };
        var disabled = new XPlatButton("Disabled") { AutoSize = false, Enabled = false, TabIndex = 45 };
        rows.Controls.Add(new XPlatLabel("Created 6th and 7th - never - skipped (TabIndex 25, 45)")
        {
            AutoSize = false,
            Bounds = new SKRect(0f, 236f, 330f, 264f),
            Font = XPlatTheme.CaptionFont,
            ForeColorRole = XPlatThemeColor.MutedForeground,
            TextAlign = XPlatContentAlignment.MiddleLeft,
        });
        notATabStop.Bounds = new SKRect(338f, 236f, 470f, 264f);
        disabled.Bounds = new SKRect(478f, 236f, 610f, 264f);
        rows.Controls.Add(notATabStop);
        rows.Controls.Add(disabled);

        // A focusable container: a tab stop of its own, and its children are walked straight after it.
        rows.Controls.Add(new XPlatLabel(
            "Created 8th - tabs 7 and 8 - a scrollable panel that is itself a tab stop (TabIndex 40): " +
            "Tab lands on the container, and its child comes straight after it with TabIndex 0.")
        {
            AutoSize = false,
            Bounds = new SKRect(0f, 268f, 628f, 298f),
            WrapText = true,
            Font = XPlatTheme.CaptionFont,
            ForeColorRole = XPlatThemeColor.MutedForeground,
            TextAlign = XPlatContentAlignment.TopLeft,
        });

        var scroller = new FocusRingPanel
        {
            TabIndex = 40,
            TabStop = true,
            Bounds = new SKRect(0f, 300f, 628f, 396f),
            VerticalScroll = XPlatScrollStyle.Automatic,
            IsCard = true,
            ShowFrame = true,
            BorderWidth = 1f,
            CornerRadius = 8f,
            Padding = new XPlatThickness(8f),
        };
        rows.Controls.Add(scroller);

        for (int i = 0; i < 4; i++)
        {
            scroller.Controls.Add(new XPlatLabel($"Filler row {i + 1} - the wheel scrolls this container")
            {
                AutoSize = false,
                Bounds = new SKRect(4f, 38f + (i * 22f), 420f, 58f + (i * 22f)),
                Font = XPlatTheme.CaptionFont,
                ForeColorRole = XPlatThemeColor.MutedForeground,
            });
        }

        var inside = new XPlatButton("Inside the container") { AutoSize = false, TabIndex = 0 };
        inside.Bounds = new SKRect(4f, 4f, 260f, 30f);

        // The rows below the fold are what make the panel scroll, and focusing one is the page's
        // demonstration of what an application does with a scrolling panel.
        inside.AddGotFocusHandler(_ => scroller.ScrollControlIntoView(inside));
        scroller.Controls.Add(inside);

        // Created last, visited last: the one row whose creation and tab position agree.
        var save = new XPlatButton("Save") { AutoSize = false, Style = XPlatButtonStyle.Primary, TabIndex = 50 };
        Place(rows, "Created 9th - tab 9 - Save (TabIndex 50)", save, 408f);

        // The order this page expects, written in the order the walk should produce it. Nothing
        // else on the page derives from this list.
        Watch(country, "Country");
        Watch(street, "Street");
        Watch(checkAddress, "Check address");
        Watch(email, "Email me a receipt");
        Watch(standard, "Standard shipping");
        Watch(fullName, "Full name");
        Watch(scroller, "the scrollable container");
        Watch(inside, "Inside the container");
        Watch(save, "Save");
        Watch(notATabStop, "Not a tab stop", isSkipped: true);
        Watch(disabled, "Disabled", isSkipped: true);

        var notes = Card();
        notes.Controls.Add(Heading("What the order shows"));
        notes.Controls.Add(Caption("Click any control and press Tab: the walk continues from there."));
        notes.Controls.Add(Note(
            "Creation order is top to bottom; Tab follows TabIndex, so it starts at Country, the " +
            "lowest index, not at the first control created."));
        notes.Controls.Add(Note(
            "Per container: the address panel's children order among themselves (10, then 20), " +
            "where the panel's own index, 15, puts them."));
        notes.Controls.Add(Note(
            "Equal indices keep tree order: the check box and the radio button are both 20, and the " +
            "check box was created first."));
        notes.Controls.Add(Note(
            "A focused container is one step: Tab stops on the scrollable panel (40), then enters " +
            "it - the child at index 0 - then continues to Save."));
        notes.Controls.Add(Note(
            "Not a tab stop (25) and Disabled (45) sit between real stops by index and never take " +
            "focus; the readout says so if one does."));

        section.Controls.Add(form);
        section.Controls.Add(notes);
        return section;
    }

    /// <summary>A caption block on the Tab order page, one paragraph of explanation.</summary>
    private static XPlatLabel Note(string text) => new(text)
    {
        Dock = XDock.Top,
        AutoSize = false,
        Height = 66f,
        WrapText = true,
        Font = XPlatTheme.CaptionFont,
        ForeColorRole = XPlatThemeColor.MutedForeground,
        TextAlign = XPlatContentAlignment.TopLeft,
        Margin = new XPlatThickness(0f, 0f, 0f, 4f),
    };

    /// <summary>
    /// The animation feature: one timeline with five animations on it, driven by the application's
    /// single frame clock. A block eases across its track, a bar blends two colours while it eases,
    /// and three tiles arrive in a stagger because each one's start time is later than the one
    /// before. Start, Pause, Resume, Restart and End all act on that one set.
    /// </summary>
    /// <remarks>
    /// The set is built with the page and started by a button, so a headless render - which builds
    /// every page and starts nothing - draws it in its rest position and costs no timer at all. The
    /// readout is itself an animation: a linear 0-to-1 value whose callback writes a percentage, so
    /// the timeline's progress can be watched and the "callback assigns the current value" contract
    /// is visible in something other than a control's bounds.
    /// </remarks>
    private XPlatPanel BuildAnimationDemo()
    {
        var card = Card();
        card.Controls.Add(Heading("Animations"));

        card.Controls.Add(new XPlatLabel(
            "One AnimationSet, one frame clock. The block eases across its track, the bar blends " +
            "blue into pink while it eases in, and the three tiles start 150 ms apart so they " +
            "arrive in a stagger. Start, Pause, Resume, Restart and End all act on the same set, " +
            "and the clock stops as soon as it is not running.")
        {
            Dock = XDock.Top,
            AutoSize = false,
            Height = 58f,
            WrapText = true,
            Font = XPlatTheme.CaptionFont,
            ForeColorRole = XPlatThemeColor.MutedForeground,
            TextAlign = XPlatContentAlignment.TopLeft,
            Margin = new XPlatThickness(0f, 0f, 0f, 8f),
        });

        var readout = new XPlatLabel("Not started - press Start")
        {
            Dock = XDock.Top,
            AutoSize = false,
            Height = 20f,
            TextAlign = XPlatContentAlignment.MiddleLeft,
            Margin = new XPlatThickness(0f, 0f, 0f, 6f),
        };

        var stage = new XPlatPanel
        {
            Dock = XDock.Fill,
            ShowFrame = false,
            IsCard = false,
            BackColor = null,
            BorderWidth = 0f,
            CornerRadius = 0f,
            Padding = new XPlatThickness(0f),
        };

        var eastward = new SKColor(0xFF2F6FED);
        var westward = new SKColor(0xFFEC4899);

        var runner = new XPlatPanel
        {
            Bounds = new SKRect(8f, 14f, 52f, 58f),
            BackColor = eastward,
            CornerRadius = 8f,
        };
        stage.Controls.Add(runner);

        stage.Controls.Add(new XPlatPanel
        {
            Bounds = new SKRect(8f, 62f, 348f, 64f),
            BackColor = XPlatTheme.Border,
            CornerRadius = 1f,
        });

        var bar = new XPlatPanel
        {
            Bounds = new SKRect(8f, 82f, 348f, 102f),
            BackColor = eastward,
            CornerRadius = 4f,
        };
        stage.Controls.Add(bar);

        var tiles = new List<XPlatPanel>();
        SKColor[] tileColors =
        [
            new(0xFF2F6FED),
            new(0xFF7C3AED),
            new(0xFFEC4899),
        ];

        // The tiles begin below their resting place and fully transparent, so the first frame of a
        // run is what brings them in rather than a value the page was built at.
        for (int index = 0; index < tileColors.Length; index++)
        {
            var tile = new XPlatPanel
            {
                Bounds = new SKRect(8f + (index * 76f), 186f, 72f + (index * 76f), 250f),
                BackColor = tileColors[index],
                CornerRadius = 10f,
                Opacity = 0f,
            };
            tiles.Add(tile);
            stage.Controls.Add(tile);
        }

        TimeSpan timeline = TimeSpan.FromMilliseconds(900);
        int finishes = 0;

        var set = new XPlatAnimationSet();

        void Report(string state) =>
            readout.Text =
                $"{state}: {set.Progress * 100d:0} per cent of {timeline.TotalMilliseconds:0} ms elapsed, " +
                $"{finishes} finish handler run(s)";

        // Something that moves, with an ease that starts and ends at rest: a linear position reads
        // as mechanical over this distance, and the curve is the difference between the two.
        set.Add(XPlatValueAnimation.Create(
            8f,
            312f,
            timeline,
            value => runner.Left = value,
            easing: XPlatEasing.EaseInOutCubic));

        // Something that changes colour, with a curve of its own, so two animations on one timeline
        // can be seen easing differently from each other.
        set.Add(XPlatValueAnimation.Create(
            eastward,
            westward,
            timeline,
            value => bar.BackColor = value,
            easing: XPlatEasing.EaseInQuad));

        // A staggered set: same animation, three start times, so they arrive in sequence.
        for (int index = 0; index < tiles.Count; index++)
        {
            var tile = tiles[index];
            var stagger = TimeSpan.FromMilliseconds(150 * index);
            var length = TimeSpan.FromMilliseconds(320);

            set.Add(XPlatValueAnimation.Create(
                tile.Top,
                158f,
                length,
                value => tile.Top = value,
                startTime: stagger,
                easing: XPlatEasing.EaseOutCubic));

            set.Add(XPlatValueAnimation.Create(
                0f,
                1f,
                length,
                value => tile.Opacity = value,
                startTime: stagger,
                easing: XPlatEasing.EaseOutQuad));
        }

        // The readout is a destination like any other, which is the point of the callback: it is a
        // value the animation assigns, not a control.
        set.Add(XPlatValueAnimation.Create(0f, 1f, timeline, _ => Report("Running")));

        set.AddFinishedHandler(_ =>
        {
            finishes++;
            Report("Finished");
        });

        var buttons = ButtonRow();

        static XPlatButton RowButton(string text, Action onClick)
        {
            var button = new XPlatButton(text)
            {
                Dock = XDock.Left,
                AutoSize = true,
                Style = XPlatButtonStyle.Secondary,
                Margin = new XPlatThickness(0f, 0f, 6f, 0f),
            };
            button.AddClickHandler(_ => onClick());
            return button;
        }

        buttons.Controls.Add(RowButton("Start", () =>
        {
            set.Start();
            Report("Running");
        }));

        buttons.Controls.Add(RowButton("Pause", () =>
        {
            set.Pause();
            Report("Paused");
        }));

        buttons.Controls.Add(RowButton("Resume", () =>
        {
            set.Resume();
            Report("Running");
        }));

        buttons.Controls.Add(RowButton("Restart", () =>
        {
            set.Restart();
            Report("Running");
        }));

        buttons.Controls.Add(RowButton("End", () =>
        {
            set.End();
            Report("Ended");
        }));

        card.Controls.Add(buttons);
        card.Controls.Add(readout);
        card.Controls.Add(stage);

        return card;
    }

    /// <summary>
    /// The spectrum colour picker, one instance per colour space plus a narrow one. Each column
    /// carries a live readout of the colour it is producing, so dragging one square shows what the
    /// same value looks like in the other two modes, and the narrow instance shows the component
    /// fields stacking instead of sharing a row.
    /// </summary>
    private XPlatPanel BuildColorPickerDemo()
    {
        var card = Card();
        card.Controls.Add(Heading("Colour picker"));

        card.Controls.Add(new XPlatLabel(
            "The square runs saturation left to right and value (RGB, HSV) or lightness (HSL) top to " +
            "bottom; the strip on the right is hue, red at the top. HSB is the same space as HSV.")
        {
            Dock = XDock.Top,
            AutoSize = false,
            Height = 42f,
            WrapText = true,
            Font = XPlatTheme.CaptionFont,
            ForeColorRole = XPlatThemeColor.MutedForeground,
            TextAlign = XPlatContentAlignment.TopLeft,
            Margin = new XPlatThickness(0f, 0f, 0f, 8f),
        });

        var row = new XPlatPanel
        {
            Dock = XDock.Top,
            Height = 318f,
            ShowFrame = false,
            IsCard = false,
            BackColor = null,
            BorderWidth = 0f,
            CornerRadius = 0f,
            Padding = new XPlatThickness(0f),
            Margin = new XPlatThickness(0f),
        };

        row.Controls.Add(BuildColorPickerColumn("RGB", XPlatColorMode.Rgb, 250f));
        row.Controls.Add(BuildColorPickerColumn("HSV (HSB)", XPlatColorMode.Hsv, 250f));
        row.Controls.Add(BuildColorPickerColumn("HSL", XPlatColorMode.Hsl, 250f));
        row.Controls.Add(BuildColorPickerColumn("Narrow: fields stack", XPlatColorMode.Hsl, 170f));

        card.Controls.Add(row);
        return card;
    }

    private XPlatPanel BuildColorPickerColumn(string title, XPlatColorMode mode, float width)
    {
        var column = new XPlatPanel
        {
            Dock = XDock.Left,
            Width = width,
            ShowFrame = false,
            IsCard = false,
            BackColor = null,
            BorderWidth = 0f,
            CornerRadius = 0f,
            Padding = new XPlatThickness(0f),
            Margin = new XPlatThickness(0f, 0f, 10f, 0f),
        };

        var picker = new XPlatColorPicker
        {
            Dock = XDock.Top,
            Height = 280f,
            ColorMode = mode,
            Color = new SKColor(0x33, 0x66, 0x99),
        };

        var readout = new XPlatLabel(DescribeColorPicker(picker))
        {
            Dock = XDock.Top,
            AutoSize = false,
            Height = 20f,
            Font = XPlatTheme.CaptionFont,
            ForeColorRole = XPlatThemeColor.Accent,
            TextAlign = XPlatContentAlignment.MiddleLeft,
            Selectable = true,
        };

        picker.AddColorChangedHandler(_ => readout.Text = DescribeColorPicker(picker));

        column.Controls.Add(new XPlatLabel(title)
        {
            Dock = XDock.Top,
            AutoSize = false,
            Height = 18f,
            Font = XPlatTheme.CaptionFont,
            ForeColorRole = XPlatThemeColor.MutedForeground,
            TextAlign = XPlatContentAlignment.BottomLeft,
        });

        column.Controls.Add(picker);
        column.Controls.Add(readout);
        return column;
    }

    private static string DescribeColorPicker(XPlatColorPicker picker) =>
        $"{picker.ColorMode}  #{picker.Color.Red:X2}{picker.Color.Green:X2}{picker.Color.Blue:X2}";

    /// <summary>
    /// The font dialog page: the platform's own chooser, the choice it returned, and a subtree that
    /// switches to that font so the inherited-appearance path is visible at the same time.
    /// </summary>
    private XPlatPanel BuildFontDialogDemo()
    {
        var initial = XPlatTheme.DefaultFont;
        _fontDialogSelection = SelectionOf(initial);

        // The page owns this font for the life of the window. Controls never dispose a font they
        // were handed, so replacing it means keeping it rather than letting the next paint race a
        // Dispose.
        var font = _fontDialogSelection.CreateFont();
        _fontDialogFonts.Add(font);

        var card = Card();
        card.Controls.Add(Heading("Font dialog"));
        card.Controls.Add(new XPlatLabel(
            "PickFont shows the platform's own font chooser - ChooseFont on Windows, NSFontPanel on " +
            "macOS and GtkFontChooserDialog on GTK - and returns a value describing the choice, or " +
            "null when the dialog was cancelled. The value is not a font: the caller builds and owns " +
            "the XPlatFont.")
        {
            Dock = XDock.Top,
            AutoSize = false,
            Height = 58f,
            WrapText = true,
            Font = XPlatTheme.CaptionFont,
            ForeColorRole = XPlatThemeColor.MutedForeground,
            TextAlign = XPlatContentAlignment.TopLeft,
            Margin = new XPlatThickness(0f, 0f, 0f, 10f),
        });

        _fontDialogReadout = new XPlatLabel
        {
            Dock = XDock.Top,
            AutoSize = false,
            Height = 44f,
            WrapText = true,
            Font = XPlatTheme.CaptionFont,
            ForeColorRole = XPlatThemeColor.MutedForeground,
            TextAlign = XPlatContentAlignment.TopLeft,
            Margin = new XPlatThickness(0f, 0f, 0f, 8f),
        };
        card.Controls.Add(_fontDialogReadout);

        var buttons = ButtonRow();

        var choose = new XPlatButton("Choose font...")
        {
            Dock = XDock.Left,
            AutoSize = true,
            Style = XPlatButtonStyle.Secondary,
            Margin = new XPlatThickness(0f, 0f, 8f, 0f),
        };
        choose.AddClickHandler(_ => ShowFontDialog());

        var reset = new XPlatButton("Reset")
        {
            Dock = XDock.Left,
            AutoSize = true,
            Style = XPlatButtonStyle.Secondary,
        };
        reset.AddClickHandler(_ => ResetFontDialog());

        buttons.Controls.Add(choose);
        buttons.Controls.Add(reset);
        card.Controls.Add(buttons);

        // The subtree: the panel assigns the chosen font and everything under it inherits it,
        // including the button, whose class default is the theme's emphasized font.
        var stage = new XPlatPanel
        {
            Dock = XDock.Top,
            Height = 236f,
            Padding = new XPlatThickness(14f, 10f),
            CornerRadius = 8f,
            BorderWidth = 1f,
            Font = font,
            Background = XPlatBackground.FromThemeColor(XPlatThemeColor.Secondary),
        };
        _fontDialogStage = stage;

        stage.Controls.Add(new XPlatLabel(
            "The quick brown fox jumps over the lazy dog. This label inherits the panel's font, so " +
            "the whole subtree re-lays itself out when the choice changes.")
        {
            Dock = XDock.Top,
            AutoSize = false,
            Height = 74f,
            WrapText = true,
            TextAlign = XPlatContentAlignment.TopLeft,
        });

        var nested = new XPlatPanel
        {
            Dock = XDock.Top,
            Height = 72f,
            Padding = new XPlatThickness(10f, 6f),
            CornerRadius = 6f,
            BorderWidth = 1f,
        };

        nested.Controls.Add(new XPlatLabel(
            "This label sits in a nested panel that assigns nothing, so it takes the font from one " +
            "level up - the same font, at the same size.")
        {
            Dock = XDock.Fill,
            AutoSize = false,
            WrapText = true,
            TextAlign = XPlatContentAlignment.TopLeft,
        });

        stage.Controls.Add(nested);
        stage.Controls.Add(new XPlatButton("A button under the same panel")
        {
            Dock = XDock.Top,
            AutoSize = true,
            Style = XPlatButtonStyle.Ghost,
            Margin = new XPlatThickness(0f, 8f, 0f, 0f),
        });
        card.Controls.Add(stage);

        card.Controls.Add(new XPlatLabel(
            "The dialog lists the families the operating system has installed. A face loaded with " +
            "XPlatFont.FromFile is not a system font unless the OS knows it too, so it does not " +
            "appear here; that is documented rather than half-supported.")
        {
            Dock = XDock.Fill,
            AutoSize = false,
            WrapText = true,
            Font = XPlatTheme.CaptionFont,
            ForeColorRole = XPlatThemeColor.MutedForeground,
            TextAlign = XPlatContentAlignment.TopLeft,
            Margin = new XPlatThickness(0f, 10f, 0f, 0f),
        });

        UpdateFontDialogReadout();
        return card;
    }

    private void ShowFontDialog()
    {
        if (OwnerWindow is not { } owner || _fontDialogSelection is not { } current)
        {
            return;
        }

        var chosen = owner.Application.Dialogs.PickFont(
            new XPlatFontDialogOptions
            {
                Title = "Choose a font",
                InitialFamilyName = current.FamilyName,
                InitialSize = current.Size,
                InitialWeight = current.Weight,
                InitialItalic = current.Italic,
                MinimumSize = 6f,
                MaximumSize = 48f,
            },
            owner);

        if (chosen is null)
        {
            SetStatus("Font dialog cancelled; the preview is unchanged.");
            return;
        }

        ApplyFontSelection(chosen);
        SetStatus($"Chose {DescribeSelection(chosen)}.");
    }

    private void ResetFontDialog()
    {
        _fontDialogStage?.ClearFont();

        if (_fontDialogStage is { } stage)
        {
            _fontDialogSelection = SelectionOf(stage.Font);
        }

        UpdateFontDialogReadout();
        SetStatus("The preview inherits the theme's font again.");
    }

    private void ApplyFontSelection(XPlatFontSelection selection)
    {
        _fontDialogSelection = selection;

        // The page owns every font it makes. Keeping the previous one alive is deliberate: a paint
        // may still be holding it when the assignment lands, and a handful of fonts is nothing.
        var font = selection.CreateFont();
        _fontDialogFonts.Add(font);

        if (_fontDialogStage is not null)
        {
            _fontDialogStage.Font = font;
        }

        UpdateFontDialogReadout();
    }

    private void UpdateFontDialogReadout()
    {
        if (_fontDialogReadout is null || _fontDialogSelection is not { } selection)
        {
            return;
        }

        var effective = _fontDialogStage?.Font;
        _fontDialogReadout.Text =
            $"Selection: {DescribeSelection(selection)}." +
            (effective is null
                ? string.Empty
                : $"\nThe subtree reports {effective.FamilyName}, {effective.Size:0.#} logical px, " +
                  $"weight {effective.Weight}, {(effective.Italic ? "italic" : "upright")}.");
    }

    private static XPlatFontSelection SelectionOf(XPlatFont font) =>
        new(string.IsNullOrWhiteSpace(font.FamilyName) ? "sans-serif" : font.FamilyName,
            font.Size,
            font.Weight,
            font.Italic);

    private static string DescribeSelection(XPlatFontSelection selection) =>
        $"{selection.FamilyName}, {selection.Size:0.#} logical px, weight {selection.Weight}, " +
        (selection.Italic ? "italic" : "upright");

    /// <summary>
    /// The colour dialog page: the swatch the colour dialog returns, a readout of the same value,
    /// and a button that opens the dialog modally on this window. The owner comes from
    /// <see cref="OwnerWindow"/>, which is the root control's window, so the button works no matter
    /// which page is showing.
    /// </summary>
    private XPlatPanel BuildColorDialogDemo()
    {
        var card = Card();
        card.Controls.Add(Heading("Colour dialog"));

        card.Controls.Add(new XPlatLabel(
            "XPlatColorDialog is drawn by the library, not by the platform: it is an ordinary " +
            "XPlatWindow holding XPlatColorPicker, with OK and Cancel, shown through the same modal " +
            "path an application uses for its own dialogs. Enter accepts, Escape cancels, and Cancel " +
            "returns null.")
        {
            Dock = XDock.Top,
            AutoSize = false,
            Height = 58f,
            WrapText = true,
            Font = XPlatTheme.CaptionFont,
            ForeColorRole = XPlatThemeColor.MutedForeground,
            TextAlign = XPlatContentAlignment.TopLeft,
            Margin = new XPlatThickness(0f, 0f, 0f, 10f),
        });

        var swatch = new XPlatPanel
        {
            Dock = XDock.Top,
            Height = 110f,
            BorderWidth = 1f,
            CornerRadius = 8f,
            BackColor = _colorDialogColor,
            Margin = new XPlatThickness(0f, 0f, 0f, 10f),
        };
        _colorDialogSwatch = swatch;
        card.Controls.Add(swatch);

        _colorDialogReadout = new XPlatLabel
        {
            Dock = XDock.Top,
            AutoSize = false,
            Height = 40f,
            WrapText = true,
            Font = XPlatTheme.CaptionFont,
            ForeColorRole = XPlatThemeColor.Accent,
            TextAlign = XPlatContentAlignment.TopLeft,
            Selectable = true,
            Margin = new XPlatThickness(0f, 0f, 0f, 8f),
        };
        card.Controls.Add(_colorDialogReadout);

        var buttons = ButtonRow();

        var choose = new XPlatButton("Choose colour...")
        {
            Dock = XDock.Left,
            AutoSize = true,
            Style = XPlatButtonStyle.Primary,
            Margin = new XPlatThickness(0f, 0f, 8f, 0f),
        };
        choose.AddClickHandler(_ => ShowColorDialog());

        var reset = new XPlatButton("Reset")
        {
            Dock = XDock.Left,
            AutoSize = true,
            Style = XPlatButtonStyle.Secondary,
        };
        reset.AddClickHandler(_ => ResetColorDialog());

        buttons.Controls.Add(choose);
        buttons.Controls.Add(reset);
        card.Controls.Add(buttons);

        card.Controls.Add(new XPlatLabel(
            "The dialog is library-drawn on every platform, which is the difference from the font " +
            "dialog next door: the picker already exists as a portable control, so a native colour " +
            "chooser would replace tested code with three different answers. A font panel has no " +
            "portable substitute, so that one is still the platform's own.")
        {
            Dock = XDock.Fill,
            AutoSize = false,
            WrapText = true,
            Font = XPlatTheme.CaptionFont,
            ForeColorRole = XPlatThemeColor.MutedForeground,
            TextAlign = XPlatContentAlignment.TopLeft,
            Margin = new XPlatThickness(0f, 10f, 0f, 0f),
        });

        UpdateColorDialogReadout();
        return card;
    }

    private void ShowColorDialog()
    {
        if (OwnerWindow is not { } owner)
        {
            return;
        }

        var dialog = new XPlatColorDialog(new XPlatColorDialogOptions
        {
            Title = "Choose a colour",
            InitialColor = _colorDialogColor,
            ColorMode = XPlatColorMode.Rgb,
        });

        var chosen = dialog.Show(owner);
        if (chosen is null)
        {
            SetStatus("Colour dialog cancelled; the swatch is unchanged.");
            return;
        }

        _colorDialogColor = chosen.Value;
        UpdateColorDialogReadout();
        SetStatus($"Chose {DescribeColor(_colorDialogColor)}.");
    }

    private void ResetColorDialog()
    {
        _colorDialogColor = new SKColor(0x33, 0x66, 0x99);
        UpdateColorDialogReadout();
        SetStatus("The swatch is back to #336699.");
    }

    private void UpdateColorDialogReadout()
    {
        if (_colorDialogSwatch is not null)
        {
            _colorDialogSwatch.BackColor = _colorDialogColor;
        }

        if (_colorDialogReadout is not null)
        {
            _colorDialogReadout.Text =
                $"Current colour: {DescribeColor(_colorDialogColor)}. Opening the dialog again " +
                "starts from this value.";
        }
    }

    private static string DescribeColor(SKColor color) =>
        $"#{color.Red:X2}{color.Green:X2}{color.Blue:X2} (R {color.Red}, G {color.Green}, B {color.Blue})";

    /// <summary>
    /// A panel that draws the focus ring when it has the focus. A plain panel has no chrome of its
    /// own, and the readout alone is a weak way to show that a container - not a control inside it -
    /// is the thing Tab landed on.
    /// </summary>
    private sealed class FocusRingPanel : XPlatPanel
    {
        protected override void OnPaintBorder(SKCanvas canvas, SKRect rect)
        {
            base.OnPaintBorder(canvas, rect);

            if (IsFocused)
            {
                XPlatPainter.DrawFocusRing(canvas, rect, XPlatTheme.FocusRing, 2f, CornerRadius);
            }
        }
    }
}
