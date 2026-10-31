using SkiaSharp;
using XPlatUI;
using XPlatUI.Backend.Gtk4;
using XPlatUI.Demo;

namespace GtkHarness;

/// <summary>
/// The Linux harness. Like the Windows and macOS ones it is deliberately tiny: everything platform
/// specific lives in <see cref="Gtk4ApplicationBackend"/>, so the whole program is "pick a backend,
/// describe a window, run".
/// </summary>
internal static class Program
{
    /// <summary>The sample UI is designed against this logical (96 DPI) client size.</summary>
    private static readonly SKSize DesignClientSize = new(1280f, 760f);

    /// <summary>Below this the sample layout has nothing left to squeeze.</summary>
    private static readonly SKSize DesignMinimumClientSize = new(820f, 660f);

    /// <summary>Drives caret blinking and any other time-based UI behaviour.</summary>
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(250);

    private static int Main(string[] args)
    {
        // Headless render of the sample UI, handy for visual checks and CI. It paints through the
        // headless backend rather than the window one, so it needs no display and works over ssh:
        //   GtkHarness --render output.png [width height] [--dark|--light]
        //
        // This has to come before anything touches a GTK type. GDK's initialisation prints
        // "Failed to open display" and terminates the process with exit code 1 when there is no
        // display, and it does so from Gtk.Module.Initialize() - too early for InitCheck to report
        // it - so a render path that loads GTK cannot run on a headless machine. Nothing in the
        // renderer needs GTK: DemoUi.RenderToPng is pure XPlatUI plus SkiaSharp.
        if (args.Length > 0 && args[0] == "--render")
        {
            return RenderSample(args);
        }

        // Registers the native library resolver for the GTK/GDK/GLib bindings and opens the display,
        // so without one it ends the process with exit code 1. The application backend does this
        // too, but the desktop font has to be read from GTK before the options are built.
        Gtk.Module.Initialize();

        var options = new XPlatApplicationOptions
        {
            ApplicationName = "XPlat UI",
            // Draw with the desktop's own UI font rather than SkiaSharp's default, so the controls
            // match the rest of the desktop.
            FontFamily = Gtk4SystemTheme.PreferredFontFamily(),
            ForcedDarkMode = ForcedDarkMode(args),
            TickInterval = TickInterval,
        };

        using var application = new XPlatApplication(new Gtk4ApplicationBackend(), options);

        var window = new XPlatWindow("XPlat UI - GTK4 test harness", DemoUi.CreateRoot())
        {
            ClientSize = DesignClientSize,
            MinimumClientSize = DesignMinimumClientSize,
            MenuBar = CreateMenuBar(),
        };

        application.MainWindow = window;
        window.Show();

        return application.Run();
    }

    /// <summary>
    /// The window's own menu bar. GTK has no global menu bar, so the backend packs this one into the
    /// window above the drawing area.
    /// </summary>
    private static XPlatMenuBar CreateMenuBar() => new(
        new XPlatMenu(
            "View",
            new XPlatMenuItem("Dark Mode", () => XPlatTheme.Palette = XPlatPalette.Dark),
            new XPlatMenuItem("Light Mode", () => XPlatTheme.Palette = XPlatPalette.Light)));

    private static int RenderSample(string[] args)
    {
        var path = args.Length > 1 ? args[1] : "xplatui.png";
        var width = args.Length > 2 && int.TryParse(args[2], out var w) ? w : (int)DesignClientSize.Width;
        var height = args.Length > 3 && int.TryParse(args[3], out var h) ? h : (int)DesignClientSize.Height;

        // Deliberately no Gtk4SystemTheme here: reading it goes through Gtk.Settings, which needs a
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
    private static bool? ForcedDarkMode(string[] args)
    {
        if (Array.IndexOf(args, "--dark") >= 0)
        {
            return true;
        }

        return Array.IndexOf(args, "--light") >= 0 ? false : null;
    }

    private static void ApplyAppearanceOverride(string[] args)
    {
        if (ForcedDarkMode(args) is { } dark)
        {
            XPlatTheme.Palette = dark ? XPlatPalette.Dark : XPlatPalette.Light;
        }
    }
}
