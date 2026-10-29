using SkiaSharp;

namespace XPlatUI;

/// <summary>
/// The palette and fonts the built-in controls draw with. Controls read these values while
/// they paint, so assigning <see cref="Palette"/> recolours the whole UI on the next frame;
/// <see cref="Changed"/> tells a backend that a frame is due.
/// </summary>
/// <remarks>
/// The palette is process-wide, the way branding usually is. To follow the operating system's
/// light/dark preference use <see cref="XPlatSystemTheme"/>; to force one, assign
/// <see cref="Palette"/> directly.
/// </remarks>
public static class XPlatTheme
{
    private static XPlatPalette _palette = XPlatPalette.Light;

    /// <summary>Raised after <see cref="Palette"/> was assigned a different palette.</summary>
    public static event EventHandler? Changed;

    /// <summary>
    /// The colours in use. Assign <see cref="XPlatPalette.Light"/>, <see cref="XPlatPalette.Dark"/>
    /// or a palette of your own.
    /// </summary>
    public static XPlatPalette Palette
    {
        get => _palette;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (ReferenceEquals(_palette, value))
            {
                return;
            }

            Assign(value);
        }
    }

    /// <summary>True while a dark palette is in use.</summary>
    public static bool IsDarkMode => _palette.IsDark;

    /// <summary>Resolves one palette entry by name, for controls that defer a colour to paint time.</summary>
    public static SKColor ColorOf(XPlatThemeColor color) => color switch
    {
        XPlatThemeColor.Background => _palette.Background,
        XPlatThemeColor.Surface => _palette.Surface,
        XPlatThemeColor.Foreground => _palette.Foreground,
        XPlatThemeColor.MutedForeground => _palette.MutedForeground,
        XPlatThemeColor.Border => _palette.Border,
        XPlatThemeColor.Accent => _palette.Accent,
        XPlatThemeColor.AccentHover => _palette.AccentHover,
        XPlatThemeColor.AccentPressed => _palette.AccentPressed,
        XPlatThemeColor.AccentForeground => _palette.AccentForeground,
        XPlatThemeColor.Secondary => _palette.Secondary,
        XPlatThemeColor.SecondaryHover => _palette.SecondaryHover,
        XPlatThemeColor.SecondaryPressed => _palette.SecondaryPressed,
        XPlatThemeColor.FocusRing => _palette.FocusRing,
        XPlatThemeColor.Splitter => _palette.Splitter,
        XPlatThemeColor.SplitterHover => _palette.SplitterHover,
        XPlatThemeColor.InputBackground => _palette.InputBackground,
        XPlatThemeColor.Selection => _palette.Selection,
        XPlatThemeColor.Caret => _palette.Caret,
        XPlatThemeColor.Placeholder => _palette.Placeholder,
        XPlatThemeColor.DisabledBackground => _palette.DisabledBackground,
        XPlatThemeColor.DisabledForeground => _palette.DisabledForeground,
        _ => _palette.Foreground,
    };

    // The individual colours read and write the current palette: assigning one replaces the
    // palette with an edited copy. Prefer Palette when switching appearance.

    public static SKColor Background
    {
        get => _palette.Background;
        set => Assign(_palette with { Background = value });
    }

    public static SKColor Surface
    {
        get => _palette.Surface;
        set => Assign(_palette with { Surface = value });
    }

    public static SKColor Foreground
    {
        get => _palette.Foreground;
        set => Assign(_palette with { Foreground = value });
    }

    public static SKColor MutedForeground
    {
        get => _palette.MutedForeground;
        set => Assign(_palette with { MutedForeground = value });
    }

    public static SKColor Border
    {
        get => _palette.Border;
        set => Assign(_palette with { Border = value });
    }

    public static SKColor Accent
    {
        get => _palette.Accent;
        set => Assign(_palette with { Accent = value });
    }

    public static SKColor AccentHover
    {
        get => _palette.AccentHover;
        set => Assign(_palette with { AccentHover = value });
    }

    public static SKColor AccentPressed
    {
        get => _palette.AccentPressed;
        set => Assign(_palette with { AccentPressed = value });
    }

    public static SKColor AccentForeground
    {
        get => _palette.AccentForeground;
        set => Assign(_palette with { AccentForeground = value });
    }

    public static SKColor Secondary
    {
        get => _palette.Secondary;
        set => Assign(_palette with { Secondary = value });
    }

    public static SKColor SecondaryHover
    {
        get => _palette.SecondaryHover;
        set => Assign(_palette with { SecondaryHover = value });
    }

    public static SKColor SecondaryPressed
    {
        get => _palette.SecondaryPressed;
        set => Assign(_palette with { SecondaryPressed = value });
    }

    public static SKColor FocusRing
    {
        get => _palette.FocusRing;
        set => Assign(_palette with { FocusRing = value });
    }

    public static SKColor Splitter
    {
        get => _palette.Splitter;
        set => Assign(_palette with { Splitter = value });
    }

    public static SKColor SplitterHover
    {
        get => _palette.SplitterHover;
        set => Assign(_palette with { SplitterHover = value });
    }

    /// <summary>Fill of editable fields such as <see cref="XPlatTextBox"/>.</summary>
    public static SKColor InputBackground
    {
        get => _palette.InputBackground;
        set => Assign(_palette with { InputBackground = value });
    }

    /// <summary>Fill of a text selection inside an editable field (drawn over the background).</summary>
    public static SKColor Selection
    {
        get => _palette.Selection;
        set => Assign(_palette with { Selection = value });
    }

    /// <summary>Color of the text caret.</summary>
    public static SKColor Caret
    {
        get => _palette.Caret;
        set => Assign(_palette with { Caret = value });
    }

    /// <summary>Color of placeholder text shown in an empty editable field.</summary>
    public static SKColor Placeholder
    {
        get => _palette.Placeholder;
        set => Assign(_palette with { Placeholder = value });
    }

    public static SKColor DisabledBackground
    {
        get => _palette.DisabledBackground;
        set => Assign(_palette with { DisabledBackground = value });
    }

    public static SKColor DisabledForeground
    {
        get => _palette.DisabledForeground;
        set => Assign(_palette with { DisabledForeground = value });
    }

    private static string? _fontFamily;
    private static XPlatFont _defaultFont = new(size: 13f);
    private static XPlatFont _captionFont = new(size: 11.5f);
    private static XPlatFont _emphasizedFont = new(size: 13f, weight: 600);
    private static XPlatFont _titleFont = new(size: 24f, weight: 700);
    private static XPlatFont _sectionFont = new(size: 15f, weight: 600);

    /// <summary>
    /// The font family the built-in controls draw with, or null to use the operating system's
    /// default. A backend sets this once at start-up so the UI matches the desktop it runs on.
    /// </summary>
    /// <remarks>
    /// Assign it before creating controls: the theme fonts are rebuilt when it changes, and the
    /// previous font objects are left undisposed because a control may still be holding one from a
    /// paint pass. A handful of fonts is a few bytes, and this is a start-up only operation.
    /// </remarks>
    public static string? FontFamily
    {
        get => _fontFamily;
        set
        {
            if (string.Equals(_fontFamily, value, StringComparison.Ordinal))
            {
                return;
            }

            _fontFamily = value;
            RebuildFonts();
            Changed?.Invoke(null, EventArgs.Empty);
        }
    }

    public static XPlatFont DefaultFont => _defaultFont;

    public static XPlatFont CaptionFont => _captionFont;

    public static XPlatFont EmphasizedFont => _emphasizedFont;

    public static XPlatFont TitleFont => _titleFont;

    public static XPlatFont SectionFont => _sectionFont;

    private static void RebuildFonts()
    {
        _defaultFont = new XPlatFont(_fontFamily, 13f);
        _captionFont = new XPlatFont(_fontFamily, 11.5f);
        _emphasizedFont = new XPlatFont(_fontFamily, 13f, weight: 600);
        _titleFont = new XPlatFont(_fontFamily, 24f, weight: 700);
        _sectionFont = new XPlatFont(_fontFamily, 15f, weight: 600);
    }

    private static void Assign(XPlatPalette palette)
    {
        _palette = palette;
        Changed?.Invoke(null, EventArgs.Empty);
    }
}
