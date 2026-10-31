using XPlatUI;

namespace XPlatUI.Backend.Win32;

/// <summary>
/// Reads Windows' light/dark preference - Settings &gt; Personalisation &gt; Colours &gt;
/// "Choose your default app mode" - for the XPlatUI library.
/// </summary>
/// <remarks>
/// <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize\AppsUseLightTheme</c> is
/// the same value Explorer and WinUI read: 0 means dark apps, 1 means light apps. Windows does not
/// raise an event for it, so the window calls <see cref="IsDarkMode"/> again whenever it receives
/// <c>WM_SETTINGCHANGE</c> (with "ImmersiveColorSet") or <c>WM_THEMECHANGED</c>.
/// </remarks>
internal sealed class Win32SystemTheme : IXPlatSystemTheme
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string AppsUseLightTheme = "AppsUseLightTheme";

    public bool IsDarkMode
    {
        get
        {
            if (NativeMethods.ReadUserDword(PersonalizeKey, AppsUseLightTheme) is { } light)
            {
                return light == 0;
            }

            return NativeMethods.ShouldSystemUseDarkMode();
        }
    }
}
