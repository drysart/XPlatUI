using SkiaSharp;

namespace XPlatUI;

/// <summary>
/// The colours the built-in controls draw with. <see cref="XPlatThemeColor"/> names one entry
/// so a control (or a text span) can say "paint me in the accent colour" instead of copying a
/// colour value that would then be frozen at the moment the control was built.
/// </summary>
public enum XPlatThemeColor
{
    Background = 0,
    Surface = 1,
    Foreground = 2,
    MutedForeground = 3,
    Border = 4,
    Accent = 5,
    AccentHover = 6,
    AccentPressed = 7,
    AccentForeground = 8,
    Secondary = 9,
    SecondaryHover = 10,
    SecondaryPressed = 11,
    FocusRing = 12,
    Splitter = 13,
    SplitterHover = 14,
    InputBackground = 15,
    Selection = 16,
    Caret = 17,
    Placeholder = 18,
    DisabledBackground = 19,
    DisabledForeground = 20,
}

/// <summary>
/// One complete set of theme colours. The library ships <see cref="Light"/> and <see cref="Dark"/>;
/// an application can brand either one with a <c>with</c> expression and assign the result to
/// <see cref="XPlatTheme.Palette"/>.
/// </summary>
public sealed record XPlatPalette
{
    /// <summary>The default light palette.</summary>
    public static XPlatPalette Light { get; } = new();

    /// <summary>The palette used while the system reports a dark appearance.</summary>
    public static XPlatPalette Dark { get; } = Light with
    {
        IsDark = true,
        Background = new(0xFF1B1F24),
        Surface = new(0xFF23282F),
        Foreground = new(0xFFE6E9EE),
        MutedForeground = new(0xFF9AA5B1),
        Border = new(0xFF39414B),
        Accent = new(0xFF4C8DFF),
        AccentHover = new(0xFF6BA0FF),
        AccentPressed = new(0xFF3A79E8),
        Secondary = new(0xFF2E353E),
        SecondaryHover = new(0xFF38404A),
        SecondaryPressed = new(0xFF424B57),
        FocusRing = new(0xFF7FB0FF),
        Splitter = new(0xFF39414B),
        SplitterHover = new(0xFF4B5563),
        InputBackground = new(0xFF1A1E24),
        Selection = new(0x554C8DFF),
        Caret = new(0xFFE6E9EE),
        Placeholder = new(0xFF6F7A87),
        DisabledBackground = new(0xFF2A3038),
        DisabledForeground = new(0xFF6B7481),
    };

    /// <summary>
    /// True for a dark palette. Colour alone cannot express every difference between light and
    /// dark, so controls that draw non-colour details can branch on this.
    /// </summary>
    public bool IsDark { get; init; }

    /// <summary>Window background, behind everything else.</summary>
    public SKColor Background { get; init; } = new(0xFFF4F6F9);

    /// <summary>Fill of raised surfaces such as panels and cards.</summary>
    public SKColor Surface { get; init; } = SKColors.White;

    /// <summary>Default text colour.</summary>
    public SKColor Foreground { get; init; } = new(0xFF1F2933);

    /// <summary>Text colour for secondary, less important text.</summary>
    public SKColor MutedForeground { get; init; } = new(0xFF6B7280);

    /// <summary>Lines and outlines.</summary>
    public SKColor Border { get; init; } = new(0xFFDDE1E7);

    public SKColor Accent { get; init; } = new(0xFF2F6FED);

    public SKColor AccentHover { get; init; } = new(0xFF2560D8);

    public SKColor AccentPressed { get; init; } = new(0xFF1D4FBB);

    public SKColor AccentForeground { get; init; } = SKColors.White;

    public SKColor Secondary { get; init; } = new(0xFFE9EDF3);

    public SKColor SecondaryHover { get; init; } = new(0xFFDEE4EC);

    public SKColor SecondaryPressed { get; init; } = new(0xFFCFD7E2);

    public SKColor FocusRing { get; init; } = new(0xFF7FA8F5);

    /// <summary>Fill of the splitter of an <see cref="XPlatSplitContainer"/>.</summary>
    public SKColor Splitter { get; init; } = new(0xFFDDE1E7);

    public SKColor SplitterHover { get; init; } = new(0xFFC3CBD6);

    /// <summary>Fill of editable fields such as <see cref="XPlatTextBox"/>.</summary>
    public SKColor InputBackground { get; init; } = SKColors.White;

    /// <summary>Fill of a selection (drawn over the background, so it is usually translucent).</summary>
    public SKColor Selection { get; init; } = new(0x552F6FED);

    /// <summary>Colour of the text caret.</summary>
    public SKColor Caret { get; init; } = new(0xFF1F2933);

    /// <summary>Colour of placeholder text shown in an empty editable field.</summary>
    public SKColor Placeholder { get; init; } = new(0xFF9AA3AF);

    public SKColor DisabledBackground { get; init; } = new(0xFFE4E7EC);

    public SKColor DisabledForeground { get; init; } = new(0xFF9AA3AF);
}
