namespace XPlatUI;

/// <summary>
/// The platform's light/dark preference. A backend implements this over its native setting - the
/// registry's <c>AppsUseLightTheme</c> value (plus <c>ShouldSystemUseDarkMode</c>) on Windows,
/// <c>NSApplication.EffectiveAppearance</c> on macOS, the freedesktop colour-scheme portal on
/// Linux - and installs the implementation into <see cref="XPlatSystemTheme.Current"/>.
/// </summary>
public interface IXPlatSystemTheme
{
    /// <summary>True when the operating system is configured for a dark appearance.</summary>
    bool IsDarkMode { get; }
}

/// <summary>
/// Process-wide access point for the platform's light/dark preference. The library itself never
/// talks to the operating system: a backend supplies <see cref="Current"/> and calls
/// <see cref="Refresh"/> from its theme-changed notification.
/// </summary>
public static class XPlatSystemTheme
{
    private static IXPlatSystemTheme _current = AlwaysLight.Instance;

    /// <summary>
    /// The platform implementation in use. Assigning null restores the default, which always
    /// reports light.
    /// </summary>
    public static IXPlatSystemTheme? Current
    {
        get => _current;
        set => _current = value ?? AlwaysLight.Instance;
    }

    /// <summary>The platform's current preference, or false when no backend is installed.</summary>
    public static bool IsDarkMode => _current.IsDarkMode;

    /// <summary>
    /// Reads the platform preference and, when it no longer matches <see cref="XPlatTheme.Palette"/>,
    /// switches to <see cref="XPlatPalette.Dark"/> or <see cref="XPlatPalette.Light"/>. Returns true
    /// when the palette changed, which means a repaint is due. Call it once at startup and again
    /// whenever the platform reports that the system appearance changed.
    /// </summary>
    public static bool Refresh()
    {
        var dark = _current.IsDarkMode;
        if (dark == XPlatTheme.IsDarkMode)
        {
            return false;
        }

        XPlatTheme.Palette = dark ? XPlatPalette.Dark : XPlatPalette.Light;
        return true;
    }

    private sealed class AlwaysLight : IXPlatSystemTheme
    {
        public static readonly AlwaysLight Instance = new();

        public bool IsDarkMode => false;
    }
}
