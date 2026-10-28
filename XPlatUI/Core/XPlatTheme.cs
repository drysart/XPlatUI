using SkiaSharp;

namespace XPlatUI;

/// <summary>
/// Process-wide defaults used by the built-in controls. Set these once at startup to
/// brand the UI; controls resolve their own defaults when they are constructed.
/// </summary>
public static class XPlatTheme
{
    public static SKColor Background { get; set; } = new(0xFFF4F6F9);

    public static SKColor Surface { get; set; } = SKColors.White;

    public static SKColor Foreground { get; set; } = new(0xFF1F2933);

    public static SKColor MutedForeground { get; set; } = new(0xFF6B7280);

    public static SKColor Border { get; set; } = new(0xFFDDE1E7);

    public static SKColor Accent { get; set; } = new(0xFF2F6FED);

    public static SKColor AccentHover { get; set; } = new(0xFF2560D8);

    public static SKColor AccentPressed { get; set; } = new(0xFF1D4FBB);

    public static SKColor AccentForeground { get; set; } = SKColors.White;

    public static SKColor Secondary { get; set; } = new(0xFFE9EDF3);

    public static SKColor SecondaryHover { get; set; } = new(0xFFDEE4EC);

    public static SKColor SecondaryPressed { get; set; } = new(0xFFCFD7E2);

    public static SKColor FocusRing { get; set; } = new(0xFF7FA8F5);

    public static SKColor Splitter { get; set; } = new(0xFFDDE1E7);

    public static SKColor SplitterHover { get; set; } = new(0xFFC3CBD6);

    /// <summary>Fill of editable fields such as <see cref="XPlatTextBox"/>.</summary>
    public static SKColor InputBackground { get; set; } = SKColors.White;

    /// <summary>Fill of a text selection inside an editable field (drawn over the background).</summary>
    public static SKColor Selection { get; set; } = new(0x552F6FED);

    /// <summary>Color of the text caret.</summary>
    public static SKColor Caret { get; set; } = new(0xFF1F2933);

    /// <summary>Color of placeholder text shown in an empty editable field.</summary>
    public static SKColor Placeholder { get; set; } = new(0xFF9AA3AF);

    public static SKColor DisabledBackground { get; set; } = new(0xFFE4E7EC);

    public static SKColor DisabledForeground { get; set; } = new(0xFF9AA3AF);

    public static XPlatFont DefaultFont { get; } = new(size: 13f);

    public static XPlatFont CaptionFont { get; } = new(size: 11.5f);

    public static XPlatFont EmphasizedFont { get; } = new(size: 13f, weight: 600);

    public static XPlatFont TitleFont { get; } = new(size: 24f, weight: 700);

    public static XPlatFont SectionFont { get; } = new(size: 15f, weight: 600);
}
