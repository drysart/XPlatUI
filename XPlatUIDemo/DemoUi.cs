using SkiaSharp;
using XPlatUI;
using XPlatUI.Backend.Headless;
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
    private XPlatButton _dialogAnchor = null!;
    private int _clicks;

    public DemoUi()
    {
        Root = BuildUi();
    }

    /// <summary>The root of the sample control tree.</summary>
    public XPlatControl Root { get; }

    /// <summary>Builds a fresh sample control tree, for hosts that only need the controls.</summary>
    /// <returns>The root of a new sample tree.</returns>
    public static XPlatControl CreateRoot() => new DemoUi().Root;

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
    public static void RenderToPng(string path, int width, int height)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var backend = new HeadlessApplicationBackend();
        using var application = new XPlatApplication(backend);
        var window = new XPlatWindow("XPlatUI sample", CreateRoot())
        {
            ClientSize = new SKSize(width, height),
        };

        window.Shown += (_, _) => backend.LastWindow!.SavePng(path);
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
            Height = 22f,
            Font = XPlatTheme.CaptionFont,
            ForeColorRole = XPlatThemeColor.MutedForeground,
            TextAlign = XPlatContentAlignment.TopLeft,
            Margin = new XPlatThickness(28f, 4f, 28f, 0f),
        });

        return header;
    }

    /// <summary>
    /// Swaps the palette. The library repaints on its own (the host listens to
    /// <see cref="XPlatTheme.Changed"/>), so this is the whole of the application's dark mode story.
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

        button.Click += (_, _) =>
        {
            XPlatTheme.Palette = XPlatTheme.IsDarkMode ? XPlatPalette.Light : XPlatPalette.Dark;
            SetStatus(XPlatTheme.IsDarkMode
                ? "Dark palette applied - every control recoloured on the next frame."
                : "Light palette applied - every control recoloured on the next frame.");
        };

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
        resetButton.Click += (_, _) =>
        {
            _clicks = 0;
            UpdateCounter();
            SetStatus("Counter reset.");
        };

        var toggleButton = new XPlatButton("Disable primary")
        {
            Dock = XDock.Right,
            Style = XPlatButtonStyle.Secondary,
            AutoSize = true,
            Margin = new XPlatThickness(8f, 16f, 0f, 16f),
        };
        toggleButton.Click += (_, _) =>
        {
            _primaryButton.Enabled = !_primaryButton.Enabled;
            toggleButton.Text = _primaryButton.Enabled ? "Disable primary" : "Enable primary";
            SetStatus(_primaryButton.Enabled ? "Primary action enabled." : "Primary action disabled.");
        };

        footer.Controls.Add(_statusLabel);
        footer.Controls.Add(resetButton);
        footer.Controls.Add(toggleButton);
        return footer;
    }

    private XPlatControl BuildContent()
    {
        var content = new XPlatPanel
        {
            Dock = XDock.Fill,
            ShowFrame = false,
            IsCard = false,
            BackColor = null,
            BorderWidth = 0f,
            CornerRadius = 0f,
            Padding = new XPlatThickness(0f),
        };

        content.Controls.Add(BuildSplitDemo());
        content.Controls.Add(BuildTextInputDemo());
        content.Controls.Add(BuildDialogDemo());
        content.Controls.Add(BuildCardTable());

        return content;
    }

    private XPlatPanel BuildTextInputDemo()
    {
        var section = new XPlatPanel
        {
            Dock = XDock.Bottom,
            Height = 92f,
            ShowFrame = false,
            IsCard = false,
            BackColor = null,
            BorderWidth = 0f,
            CornerRadius = 0f,
            Padding = new XPlatThickness(20f, 0f, 20f, 12f),
        };

        section.Controls.Add(new XPlatLabel("Text input")
        {
            Dock = XDock.Top,
            AutoSize = false,
            Height = 26f,
            Font = XPlatTheme.SectionFont,
            TextAlign = XPlatContentAlignment.BottomLeft,
        });

        var nameBox = new XPlatTextBox
        {
            PlaceholderText = "Type your name",
            MaxLength = 24,
        };
        nameBox.TextChanged += (_, _) => SetStatus(nameBox.Text.Length == 0
            ? "Name cleared."
            : $"Name: {nameBox.Text} ({nameBox.Text.Length}/{nameBox.MaxLength})");

        var passwordBox = new XPlatTextBox
        {
            IsPassword = true,
            PlaceholderText = "Password",
        };
        passwordBox.TextChanged += (_, _) => SetStatus($"Password length: {passwordBox.Text.Length}");

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
        // One row rather than the label-above-content shape the other strips use, because the card
        // table below is the part of the sample worth the vertical space.
        var section = new XPlatPanel
        {
            Dock = XDock.Bottom,
            Height = 56f,
            ShowFrame = false,
            IsCard = false,
            BackColor = null,
            BorderWidth = 0f,
            CornerRadius = 0f,
            Padding = new XPlatThickness(20f, 0f, 20f, 12f),
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

        row.Controls.Add(new XPlatLabel("Dialogs")
        {
            Dock = XDock.Left,
            AutoSize = false,
            Width = 92f,
            Font = XPlatTheme.SectionFont,
            TextAlign = XPlatContentAlignment.MiddleLeft,
        });

        row.Controls.Add(DialogButton("Message box", () => ShowMessageBox()));
        row.Controls.Add(DialogButton("Open file", () => ShowFileDialog(DialogKind.Open)));
        row.Controls.Add(DialogButton("Save file", () => ShowFileDialog(DialogKind.Save)));
        row.Controls.Add(DialogButton("Pick folder", () => ShowFileDialog(DialogKind.Folder)));

        _dialogAnchor = (XPlatButton)row.Controls[1];

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
        button.Click += (_, _) => onClick();
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

    /// <summary>The window the demo is shown in, used to parent the dialogs.</summary>
    private XPlatWindow? OwnerWindow => _dialogAnchor.Window;

    private XPlatPanel BuildSplitDemo()
    {
        var section = new XPlatPanel
        {
            Dock = XDock.Bottom,
            Height = 232f,
            ShowFrame = false,
            IsCard = false,
            BackColor = null,
            BorderWidth = 0f,
            CornerRadius = 0f,
            Padding = new XPlatThickness(20f, 0f, 20f, 12f),
        };

        section.Controls.Add(new XPlatLabel("Split container")
        {
            Dock = XDock.Top,
            AutoSize = false,
            Height = 26f,
            Font = XPlatTheme.SectionFont,
            TextAlign = XPlatContentAlignment.BottomLeft,
        });

        var split = new XPlatSplitContainer
        {
            Dock = XDock.Fill,
            Orientation = XPlatOrientation.Vertical,
            SplitterDistance = 260f,
            Panel1MinSize = 140f,
            Panel1MaxSize = 420f,
            Panel2MinSize = 180f,
            Margin = new XPlatThickness(0f, 6f, 0f, 0f),
        };
        split.SplitterMoved += (_, _) => SetStatus($"Splitter moved: panel 1 is {split.SplitterDistance:0} px wide.");

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
        button.Click += (_, _) => onClick();
        return button;
    }

    private XPlatControl BuildCardTable()
    {
        var table = new XPlatTableLayoutPanel
        {
            Dock = XDock.Fill,
            ShowFrame = false,
            IsCard = false,
            BackColor = null,
            BorderWidth = 0f,
            CornerRadius = 0f,
            Padding = new XPlatThickness(20f, 4f, 20f, 12f),
            CellPadding = new XPlatThickness(8f),
        };

        for (int column = 0; column < 5; column++)
        {
            table.ColumnStyles.Add(new XPlatColumnStyle(XPlatSizeType.Percent, 20f));
        }

        table.RowStyles.Add(new XPlatRowStyle(XPlatSizeType.AutoSize, 0f));
        table.RowStyles.Add(new XPlatRowStyle(XPlatSizeType.Percent, 100f));

        table.Add(SectionLabel("Labels and text"), 0, 0);
        table.Add(SectionLabel("Buttons and input"), 1, 0);
        table.Add(SectionLabel("Rich text"), 2, 0);
        table.Add(SectionLabel("Images and layout"), 3, 0);
        table.Add(SectionLabel("Selection and clipboard"), 4, 0);

        table.Add(BuildLabelsCard(), 0, 1);
        table.Add(BuildButtonsCard(), 1, 1);
        table.Add(BuildRichTextCard(), 2, 1);
        table.Add(BuildImagesCard(), 3, 1);
        table.Add(BuildSelectionCard(), 4, 1);

        return table;
    }

    private static XPlatLabel SectionLabel(string text) => new(text)
    {
        Font = XPlatTheme.SectionFont,
        AutoSize = true,
        TextAlign = XPlatContentAlignment.BottomLeft,
    };

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
        link.MouseEnter += (_, _) => SetStatus("Pointer entered a link span - spans receive mouse events.");
        link.MouseLeave += (_, _) => SetStatus("Pointer left the link span.");

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
            ShortcutText = "Ctrl+Shift+F",
        };

        var menu = new XPlatContextMenu();

        menu.Add(new XPlatMenuItem("Copy rich text", () => SetStatus("'Copy rich text' picked."))
        {
            IsDefault = true,
            ShortcutText = "Ctrl+C",
        });

        menu.Add(new XPlatMenuItem("Copy as plain text", () => SetStatus("'Copy as plain text' picked."))
        {
            ShortcutText = "Ctrl+Shift+C",
        });

        menu.AddSeparator();
        menu.Add(formatting);
        menu.Add(new XPlatMenuItem("Undo formatting", null) { Enabled = false, ShortcutText = "Ctrl+Z" });
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
        chip.Click += (_, _) => SetStatus($"Inline chip '{text}' clicked.");
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
        whole.Click += (_, _) => SetStatus("Whole-control button clicked - a drag over it selects the button as one unit.");
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
        _primaryButton.Click += (_, _) =>
        {
            _clicks++;
            UpdateCounter();
            SetStatus($"Primary action clicked {_clicks} time(s).");
        };
        card.Controls.Add(_primaryButton);

        var secondary = new XPlatButton("Secondary action")
        {
            Dock = XDock.Top,
            AutoSize = true,
            Style = XPlatButtonStyle.Secondary,
            Margin = new XPlatThickness(0f, 0f, 0f, 10f),
        };
        secondary.Click += (_, _) => SetStatus("Secondary action clicked.");
        card.Controls.Add(secondary);

        var ghost = new XPlatButton("Ghost action")
        {
            Dock = XDock.Top,
            AutoSize = true,
            Style = XPlatButtonStyle.Ghost,
            Margin = new XPlatThickness(0f, 0f, 0f, 10f),
        };
        ghost.Click += (_, _) => SetStatus("Ghost action clicked.");
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

        var image = new XPlatImage
        {
            Dock = XDock.Fill,
            SizeMode = XPlatSizeMode.Zoom,
            ImageAlign = XPlatContentAlignment.MiddleCenter,
            CornerRadius = 8f,
            DisposeImageWithControl = true,
            Margin = new XPlatThickness(0f, 0f, 0f, 8f),
        };
        image.Image = CreateGradientImage(320, 200);
        card.Controls.Add(image);

        card.Controls.Add(new XPlatLabel("A gradient drawn with SkiaSharp and shown with XPlatImage (Zoom).")
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

    private void SetStatus(string message) => _statusLabel.Text = message;

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
}
