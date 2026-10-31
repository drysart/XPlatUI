using SkiaSharp;
using XPlatUI;
using XPlatUI.Demo;

#if WINDOWS
using XPlatUI.Backend.Win32;
#elif MACOS
using XPlatUI.Backend.AppKit;
#else
using XPlatUI.Backend.Gtk4;
#endif

namespace XPlatUI.Demo.App;

/// <summary>
/// The one application, built for whichever platform is compiling it. Everything platform specific
/// lives behind <see cref="XPlatUI.IXPlatApplicationBackend"/>, so the only difference between the
/// three builds is which backend this file names and how it reads the desktop's font and
/// appearance.
/// </summary>
/// <remarks>
/// Each OS builds exactly one target framework (<c>net10.0-windows</c>, <c>net10.0-macos</c> or
/// <c>net10.0</c>), which is what makes the conditional compilation below honest: a portable
/// <c>net10.0</c> build on a Mac defines neither <c>WINDOWS</c> nor <c>MACOS</c>, so the TFM has to
/// be right rather than the symbols.
/// </remarks>
internal static class Program
{
#if WINDOWS || MACOS
    /// <summary>The sample UI is designed against this logical (96 DPI) client size.</summary>
    private static readonly SKSize DesignClientSize = new(1280f, 820f);

    /// <summary>Below this the sample layout has nothing left to squeeze.</summary>
    private static readonly SKSize DesignMinimumClientSize = new(820f, 716f);
#else
    /// <summary>
    /// The same 1280 x 760 drawing area, expressed as a client size: GTK has no global menu bar, so
    /// the window packs its own inside the client area and it eats the difference.
    /// </summary>
    private static readonly SKSize DesignClientSize = new(1280f, 760f);

    /// <summary>Below this the sample layout has nothing left to squeeze.</summary>
    private static readonly SKSize DesignMinimumClientSize = new(820f, 660f);
#endif

    /// <summary>Drives caret blinking and any other time-based UI behaviour.</summary>
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Entry point. The program deliberately avoids Windows Forms and System.Drawing: the window is
    /// a native window and everything inside it is drawn with SkiaSharp, so the whole application
    /// stays trimmable and can be published with NativeAOT.
    /// </summary>
    /// <remarks>
    /// The attribute is not decoration: the runtime reads it before <c>Main</c> runs and initializes
    /// the thread as a single-threaded COM apartment, which is the only thing that can do so. Windows
    /// needs it for the shell's file dialogs, whose <c>IFileDialog::Show</c> never returns on a
    /// multi-threaded apartment. It is ignored on macOS and Linux.
    /// </remarks>
    [STAThread]
    private static int Main(string[] args)
    {
#if WINDOWS
        // Must happen before anything touches SkiaSharp: an AOT publish carries the native Skia
        // library inside the executable instead of next to it.
        SkiaNativeLoader.EnsureLoaded();
#endif

        // Headless render of the sample UI, handy for visual checks and CI. It paints through the
        // headless backend rather than the window one, so it needs no window, no message loop and no
        // display, and it works over ssh:
        //   XPlatUIDemoApp --render output.png [width height] [--dark|--light]
        //
        // This has to come before anything touches a toolkit. GDK's initialisation prints
        // "Failed to open display" and terminates the process with exit code 1 when there is no
        // display, and it does so from Gtk.Module.Initialize() - too early for InitCheck to report
        // it - so a render path that loads GTK cannot run on a headless machine. Nothing in the
        // renderer needs GTK: DemoUi.RenderToPng is pure XPlatUI plus SkiaSharp.
        if (args.Length > 0 && args[0] == "--render")
        {
            return RenderSample(args);
        }

#if !WINDOWS && !MACOS
        // Registers the native library resolver for the GTK/GDK/GLib bindings and opens the display,
        // so without one it ends the process with exit code 1. The backend does this too, but the
        // desktop font has to be read from GTK before the options are built.
        Gtk.Module.Initialize();
#endif

        var options = new XPlatApplicationOptions
        {
            ApplicationName = "XPlat UI",
            FontFamily = PreferredFontFamily(),
            ForcedDarkMode = ForcedDarkMode(args),
            TickInterval = TickInterval,
#if WINDOWS
            // A WinExe has no console of its own; borrow the parent's so diagnostics are visible.
            AttachConsole = true,
#endif
        };

        using var application = new XPlatApplication(CreateBackend(args), options);

        var window = new XPlatWindow(WindowTitle, DemoUi.CreateRoot())
        {
            ClientSize = DesignClientSize,
            MinimumClientSize = DesignMinimumClientSize,
        };

#if !WINDOWS
        // The Win32 backend leaves the menu bar to the window's own system menu; GTK and macOS show
        // this one.
        window.MenuBar = CreateMenuBar();
#endif

        application.MainWindow = window;
        window.Show();

        return application.Run();
    }

    /// <summary>The window's own menu bar, on the platforms that show one.</summary>
    private static XPlatMenuBar CreateMenuBar() => new(
        new XPlatMenu(
            "View",
            new XPlatMenuItem("Dark Mode", () => XPlatTheme.Palette = XPlatPalette.Dark),
            new XPlatMenuItem("Light Mode", () => XPlatTheme.Palette = XPlatPalette.Light)));

    /// <summary>
    /// The title bar text. It names the backend on purpose: three builds of the same program are
    /// the point, and a screenshot should say which one it came from.
    /// </summary>
    private static string WindowTitle =>
#if WINDOWS
        "XPlat UI - Win32";
#elif MACOS
        "XPlat UI - AppKit";
#else
        "XPlat UI - GTK4";
#endif

    /// <summary>Creates the backend for the platform this build is for.</summary>
    private static IXPlatApplicationBackend CreateBackend(string[] args) =>
#if WINDOWS
        new Win32ApplicationBackend
        {
            ForceClassicMenus = Array.IndexOf(args, "--classic-menus") >= 0,
        };
#elif MACOS
        new AppKitApplicationBackend();
#else
        new Gtk4ApplicationBackend();
#endif

    /// <summary>
    /// The desktop's own UI font, so the controls match the rest of the system, or null to keep
    /// SkiaSharp's default.
    /// </summary>
    private static string? PreferredFontFamily() =>
#if MACOS
        AppKitSystemTheme.PreferredFontFamily();
#elif WINDOWS
        // The Win32 backend installs its own IXPlatSystemTheme and the library reads the font from
        // there, so there is nothing to pass.
        null;
#else
        // Reading this goes through Gtk.Settings, so the display has to be open already.
        Gtk4SystemTheme.PreferredFontFamily();
#endif

    private static int RenderSample(string[] args)
    {
        var path = args.Length > 1 ? args[1] : "xplatui.png";
        var width = args.Length > 2 && int.TryParse(args[2], out var w) ? w : (int)DesignClientSize.Width;
        var height = args.Length > 3 && int.TryParse(args[3], out var h) ? h : (int)DesignClientSize.Height;

        // Deliberately not PreferredFontFamily() on GTK: that goes through Gtk.Settings, which needs a
        // display. With no backend installed XPlatSystemTheme falls back to light, and --dark and
        // --light still force a palette, so the output stays deterministic - which is what a CI
        // screenshot wants anyway.
#if MACOS
        XPlatTheme.FontFamily = AppKitSystemTheme.PreferredFontFamily();
#endif
        ApplyAppearanceOverride(args);

        DemoUi.RenderToPng(path, width, height);
        Console.WriteLine($"Rendered {width}x{height} sample UI to {path}");
        // Printing what the render actually used makes it possible to tell a real visual difference
        // from a changed font or appearance when comparing renders from two builds.
        Console.WriteLine(
            $"Font: {XPlatTheme.FontFamily ?? "<SkiaSharp default>"}; " +
            $"appearance: {(XPlatTheme.IsDarkMode ? "dark" : "light")}");
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

    /// <summary>
    /// Applies <c>--dark</c> / <c>--light</c> if given, and otherwise the machine's own preference.
    /// </summary>
    private static void ApplyAppearanceOverride(string[] args)
    {
        if (ForcedDarkMode(args) is { } dark)
        {
            XPlatTheme.Palette = dark ? XPlatPalette.Dark : XPlatPalette.Light;
            return;
        }

#if WINDOWS
        // Only for --render: the normal path lets XPlatApplication install the backend's
        // IXPlatSystemTheme and pick the palette up itself, but a headless render has no
        // application to do that.
        XPlatSystemTheme.Current = new Win32ApplicationBackend().SystemTheme;
        XPlatSystemTheme.Refresh();
#endif
    }
}
