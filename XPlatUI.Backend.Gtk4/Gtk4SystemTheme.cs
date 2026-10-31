namespace XPlatUI.Backend.Gtk4;

/// <summary>
/// Reads the desktop's light/dark preference. GTK itself has no single answer: the freedesktop
/// colour-scheme preference lives in the GNOME settings schema, older desktops only express it
/// through <c>GTK_THEME</c> or the theme name, and applications can force it with GTK's own
/// "prefer dark" flag. All three are consulted, most authoritative first.
/// </summary>
public sealed class Gtk4SystemTheme : IXPlatSystemTheme
{
    private const string GnomeInterfaceSchema = "org.gnome.desktop.interface";
    private const string ColorSchemeKey = "color-scheme";
    private const string ThemeKey = "gtk-theme";

    private readonly Gio.Settings? _desktop;
    private Gtk.Settings? _gtkSettings;

    public Gtk4SystemTheme()
    {
        _desktop = TryOpenDesktopSettings();
    }

    /// <summary>Raised when the desktop reports that the appearance preference changed.</summary>
    public event EventHandler? Changed;

    public bool IsDarkMode
    {
        get
        {
            var scheme = ReadDesktopColorScheme();
            if (scheme is not null)
            {
                return scheme.Value;
            }

            var forced = ReadEnvironmentOverride();
            if (forced is not null)
            {
                return forced.Value;
            }

            var settings = GtkSettings();
            if (settings is not null)
            {
                if (settings.GtkApplicationPreferDarkTheme)
                {
                    return true;
                }

                var themeName = settings.GtkThemeName;
                if (!string.IsNullOrEmpty(themeName))
                {
                    return themeName.Contains("dark", StringComparison.OrdinalIgnoreCase);
                }
            }

            return false;
        }
    }

    /// <summary>
    /// Starts watching for appearance changes. GTK has to be initialised first, so this is called
    /// once the application is running rather than from the constructor.
    /// </summary>
    public void Attach()
    {
        if (_desktop is not null)
        {
            _desktop.OnChanged += OnDesktopSettingsChanged;
        }

        _gtkSettings = GtkSettings();
        if (_gtkSettings is not null)
        {
            _gtkSettings.OnNotify += OnGtkSettingsNotify;
        }
    }

    /// <summary>
    /// Mirrors the resolved palette onto GTK's own chrome (title bar, popover background) so the
    /// window furniture matches what SkiaSharp draws inside it.
    /// </summary>
    public void ApplyToGtk(bool dark)
    {
        var settings = GtkSettings();
        if (settings is null || settings.GtkApplicationPreferDarkTheme == dark)
        {
            return;
        }

        settings.GtkApplicationPreferDarkTheme = dark;
    }

    /// <summary>
    /// The family name of the desktop's UI font, so SkiaSharp draws with the same face as the rest
    /// of the desktop instead of whatever SkiaSharp considers its default. GTK reports the font as
    /// a Pango description such as "Cantarell 11", so the trailing size is dropped: the UI library
    /// keeps its own sizes, which the layout is tuned for.
    /// </summary>
    public static string? PreferredFontFamily()
    {
        try
        {
            var description = Gtk.Settings.GetDefault()?.GtkFontName;
            if (string.IsNullOrWhiteSpace(description))
            {
                return null;
            }

            var words = description.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var family = words.Length > 1 ? string.Join(' ', words[..^1]) : words[0];
            return family.Length == 0 ? null : family;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void OnDesktopSettingsChanged(Gio.Settings sender, Gio.Settings.ChangedSignalArgs args)
    {
        if (args.Key is ColorSchemeKey or ThemeKey)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnGtkSettingsNotify(GObject.Object sender, GObject.Object.NotifySignalArgs args)
    {
        if (args.Pspec?.GetName() is "gtk-application-prefer-dark-theme" or "gtk-theme-name" or "gtk-font-name")
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private bool? ReadDesktopColorScheme()
    {
        if (_desktop is null)
        {
            return null;
        }

        try
        {
            // "default" means the desktop has no opinion, so the GTK theme decides instead.
            return _desktop.GetString(ColorSchemeKey) switch
            {
                "prefer-dark" => true,
                "prefer-light" => false,
                _ => null,
            };
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool? ReadEnvironmentOverride()
    {
        var theme = Environment.GetEnvironmentVariable("GTK_THEME");
        if (string.IsNullOrEmpty(theme))
        {
            return null;
        }

        return theme.Contains("dark", StringComparison.OrdinalIgnoreCase);
    }

    private Gtk.Settings? GtkSettings()
    {
        try
        {
            return Gtk.Settings.GetDefault();
        }
        catch (Exception)
        {
            // Before gtk_init the singleton does not exist yet.
            return null;
        }
    }

    private static Gio.Settings? TryOpenDesktopSettings()
    {
        try
        {
            // g_settings_new() aborts the process when the schema is missing, so look it up first.
            if (Gio.SettingsSchemaSource.GetDefault()?.Lookup(GnomeInterfaceSchema, true) is null)
            {
                return null;
            }

            return Gio.Settings.New(GnomeInterfaceSchema);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
