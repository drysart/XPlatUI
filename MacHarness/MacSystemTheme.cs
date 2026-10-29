using AppKit;
using Foundation;
using SkiaSharp;
using XPlatUI;

namespace MacHarness;

/// <summary>
/// The system light/dark preference, read from AppKit's effective appearance. AppKit has no
/// notification for an appearance change that is guaranteed to fire for every case (it arrives as
/// KVO on <c>effectiveAppearance</c>), so the harness polls instead: the UI already ticks four
/// times a second for the caret, which makes the change look immediate.
/// </summary>
internal sealed class MacSystemTheme : IXPlatSystemTheme
{
    private const string Aqua = "NSAppearanceNameAqua";
    private const string DarkAqua = "NSAppearanceNameDarkAqua";

    private bool _isDarkMode;

    public MacSystemTheme() => _isDarkMode = Query();

    /// <summary>Raised when <see cref="Poll"/> notices the system appearance has changed.</summary>
    public event EventHandler? Changed;

    public bool IsDarkMode => _isDarkMode;

    /// <summary>
    /// Re-reads the system appearance. Returns true when it changed, which means the palette has
    /// to be refreshed and everything repainted.
    /// </summary>
    public bool Poll()
    {
        var dark = Query();
        if (dark == _isDarkMode)
        {
            return false;
        }

        _isDarkMode = dark;
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>
    /// Makes the window's title bar and other AppKit chrome match the palette the library is
    /// drawing with. Only called when the two disagree - that is, when the harness was started
    /// with an appearance override - so normally the window just inherits the system appearance.
    /// </summary>
    public void ApplyToWindow(NSWindow window, bool dark)
    {
        if (dark == _isDarkMode)
        {
            return;
        }

        window.Appearance = new NSAppearance(dark ? DarkAqua : Aqua, NSBundle.MainBundle);
    }

    /// <summary>
    /// The font family the UI should use. The system UI face is the right choice: it is what every
    /// other window on the machine uses. Falls back through fonts that ship with every macOS.
    /// </summary>
    public static string PreferredFontFamily()
    {
        foreach (var candidate in new[] { "SF Pro Text", "Helvetica Neue", "Helvetica" })
        {
            if (SKFontManager.Default.MatchFamily(candidate) is not null)
            {
                return candidate;
            }
        }

        return "Helvetica";
    }

    private static bool Query()
    {
        var appearance = NSApplication.SharedApplication?.EffectiveAppearance;
        if (appearance is null)
        {
            return false;
        }

        // Asking for the best match against the two standard names is how AppKit documents this:
        // the effective appearance may be a vibrant or high contrast variant of either.
        return string.Equals(appearance.FindBestMatch(new[] { Aqua, DarkAqua }), DarkAqua, StringComparison.Ordinal);
    }
}