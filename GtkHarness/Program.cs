using XPlatUI;
using XPlatUI.Demo;

namespace GtkHarness;

internal static class Program
{
    private const string ApplicationId = "com.xplatui.gtkharness";
    private const string WindowTitle = "XPlat UI - GTK4 test harness";

    /// <summary>The sample UI is designed against this logical (96 DPI) client size.</summary>
    private const int DesignClientWidth = 1280;
    private const int DesignClientHeight = 760;
    private const int DesignMinimumWidth = 820;
    private const int DesignMinimumHeight = 660;

    /// <summary>
    /// The live UI host. It has to outlive <c>OnActivate</c>: the surface owns the invalidation and
    /// cursor subscriptions and the tick timer, so disposing it with the activation would leave the
    /// window painting its first frame and never again.
    /// </summary>
    private static GtkUiSurface? _surface;

    private static Gtk.ApplicationWindow? _window;
    private static GtkContextMenuPresenter? _menus;
    private static GtkSystemTheme? _systemTheme;

    /// <summary>
    /// Entry point. The harness deliberately avoids any other UI toolkit: the window is a plain
    /// GTK4 application window and everything inside it is drawn with SkiaSharp into a Cairo
    /// surface, mirroring what the Win32 harness does on Windows.
    /// </summary>
    private static int Main(string[] args)
    {
        // Headless render of the sample UI, handy for visual checks and CI:
        //   GtkHarness --render output.png [width height] [--dark|--light]
        // This has to come before anything touches a GTK type. GDK's initialisation prints
        // "Failed to open display" and terminates the process with exit code 1 when there is no
        // display, and it does so from Gtk.Module.Initialize() - too early for InitCheck to report
        // it - so a render path that loads GTK cannot run on a headless machine. Nothing in the
        // renderer needs GTK: DemoUi.RenderToPng is pure XPlatUI plus SkiaSharp.
        if (args.Length > 0 && args[0] == "--render")
        {
            return RenderSample(args);
        }

        // Registers the native library resolver for the GTK/GDK/GLib bindings. This also opens the
        // display, so without one it ends the process with exit code 1; the render path above
        // returns before reaching it.
        Gtk.Module.Initialize();

        // Draw with the desktop's own UI font rather than SkiaSharp's default, so the controls match
        // the rest of the desktop. Must happen before any control is created.
        XPlatTheme.FontFamily = GtkSystemTheme.PreferredFontFamily();

        // Give the UI library a real clipboard; the library itself stays platform free.
        XPlatClipboard.Current = new GtkClipboard();

        // Likewise for the desktop's light/dark preference. GTK has to be running before it can be
        // read, so the value is refreshed from OnActivate.
        _systemTheme = new GtkSystemTheme();
        XPlatSystemTheme.Current = _systemTheme;

        using var app = Gtk.Application.New(ApplicationId, Gio.ApplicationFlags.NonUnique);
        app.OnActivate += (_, _) => Activate(app, args);
        app.OnShutdown += (_, _) => Shutdown();

        // GTK parses the command line itself, so it only ever sees the program name; the harness'
        // own switches were consumed above.
        return app.Run([Environment.ProcessPath ?? "GtkHarness"]);
    }

    private static void Activate(Gtk.Application app, string[] args)
    {
        // Activating again (a second launch, or a desktop that re-activates the app) just raises the
        // window that already exists.
        if (_window is not null)
        {
            _window.Present();
            return;
        }

        var systemTheme = _systemTheme!;
        systemTheme.Attach();
        XPlatSystemTheme.Refresh();
        ApplyAppearanceOverride(args);
        systemTheme.ApplyToGtk(XPlatTheme.IsDarkMode);

        _surface = new GtkUiSurface();

        var window = Gtk.ApplicationWindow.New(app);
        window.Title = WindowTitle;
        window.SetDefaultSize(DesignClientWidth, DesignClientHeight);
        window.SetSizeRequest(DesignMinimumWidth, DesignMinimumHeight);
        window.Child = _surface.Widget;
        window.Present();
        _window = window;

        // Context menus are platform native, so the UI library only describes them and the host
        // presents them. Installed after the widget exists because a popover needs an anchor.
        _menus = new GtkContextMenuPresenter(_surface.Widget);
        XPlatContextMenuPresenter.Current = _menus;

        _surface.Root = new DemoUi().Root;
        _surface.Start();

        // The desktop can flip appearance at any time; follow it, and keep GTK's own chrome in step.
        systemTheme.Changed += OnSystemThemeChanged;

        window.OnCloseRequest += (_, _) =>
        {
            _menus?.Dismiss();
            return false;
        };
    }

    private static void OnSystemThemeChanged(object? sender, EventArgs e)
    {
        if (XPlatSystemTheme.Refresh())
        {
            _systemTheme?.ApplyToGtk(XPlatTheme.IsDarkMode);
        }
    }

    private static void Shutdown()
    {
        if (_systemTheme is not null)
        {
            _systemTheme.Changed -= OnSystemThemeChanged;
        }

        _menus?.Dismiss();
        _menus = null;
        XPlatContextMenuPresenter.Current = null;

        _surface?.Dispose();
        _surface = null;
        _window = null;
    }

    private static int RenderSample(string[] args)
    {
        var path = args.Length > 1 ? args[1] : "xplatui.png";
        var width = args.Length > 2 && int.TryParse(args[2], out var w) ? w : DesignClientWidth;
        var height = args.Length > 3 && int.TryParse(args[3], out var h) ? h : DesignClientHeight;

        // Deliberately no GtkSystemTheme here: reading it goes through Gtk.Settings, which needs a
        // display. With no backend installed XPlatSystemTheme falls back to light, and --dark and
        // --light still force a palette, so the output stays deterministic - which is what a CI
        // screenshot wants anyway.
        ApplyAppearanceOverride(args);

        DemoUi.RenderToPng(path, width, height);
        Console.WriteLine($"Rendered {width}x{height} sample UI to {path}");
        return 0;
    }

    /// <summary>
    /// Honours <c>--dark</c> / <c>--light</c>, which force a palette instead of following the
    /// desktop. Handy for screenshots and for checking both appearances on one machine.
    /// </summary>
    private static void ApplyAppearanceOverride(string[] args)
    {
        if (Array.IndexOf(args, "--dark") >= 0)
        {
            XPlatTheme.Palette = XPlatPalette.Dark;
        }
        else if (Array.IndexOf(args, "--light") >= 0)
        {
            XPlatTheme.Palette = XPlatPalette.Light;
        }
    }
}