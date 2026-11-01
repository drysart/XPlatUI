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
        // Must happen before anything touches SkiaSharp: an AOT publish carries the native Skia
        // library inside the executable instead of next to it. On macOS the library stays in the
        // bundle, so the loader finds it where it expects it and does nothing. The resource is
        // embedded in this assembly by XPlatUI's build assets, which is why it is named here.
        SkiaNativeLoader.EnsureLoaded(typeof(Program).Assembly);

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

            // The application-wide icon: the Dock tile on macOS, the taskbar and Alt-Tab on Windows,
            // and the default icon name for every GTK window. Windows also shows it in the title bar
            // because XPlatWindow inherits this when it is created.
            Icon = DemoIcon.Create(),

            // Launching the sample twice re-uses the window that is already open instead of opening
            // a second one, which is what makes "open a file with this application" work on all
            // three platforms.
            SingleInstance = true,
#if WINDOWS
            // A WinExe has no console of its own; borrow the parent's so diagnostics are visible.
            AttachConsole = true,
#endif
        };

        using var application = new XPlatApplication(CreateBackend(args), options);

        var demo = new DemoUi();
        var window = new XPlatWindow(WindowTitle, demo.Root)
        {
            ClientSize = DesignClientSize,
            MinimumClientSize = DesignMinimumClientSize,
        };

// Attached on every platform: GTK and macOS draw the bar, and the Win32 backend records it
// without drawing it yet - but the shortcuts it carries work everywhere, because the window
// checks the bar before the focused control sees a key.
window.MenuBar = CreateMenuBar(window, demo);

        application.MainWindow = window;

        // The window comes forward on its own; the status line says why it did.
        application.SecondInstance += (_, e) => demo.SetStatus(
            e.Arguments.Count == 0
                ? "A second launch was handed to this window."
                : $"A second launch was handed to this window: {string.Join(' ', e.Arguments)}");

        // Activation is the other half of the multi-window story: the status line reports it, which
        // is also the easiest way to see that an owned window taking the focus deactivates this one.
        window.Activated += (_, _) => demo.SetStatus("Window activated - it has the keyboard focus.");
        window.Deactivated += (_, _) => demo.SetStatus("Window deactivated - another window has the focus.");

        // Windows has no menu bar to hang these off yet, so the same two window features are also
        // reachable as flags: --topmost starts above everything, --owned opens a window owned by
        // this one.
        window.TopMost = Array.IndexOf(args, "--topmost") >= 0;

        window.Show();

        if (Array.IndexOf(args, "--owned") >= 0)
        {
            demo.ShowOwnedWindow();
        }

        return application.Run();
    }

    /// <summary>
    /// The window's own menu bar, on the platforms that show one. Each item carries a shortcut so
    /// the same commands are reachable from the keyboard on all three platforms, including the one
    /// that does not draw the bar yet.
    /// </summary>
    private static XPlatMenuBar CreateMenuBar(XPlatWindow window, DemoUi demo) => new(
        new XPlatMenu(
            "View",
            new XPlatMenuItem("Dark Mode", () => demo.SetAppearance(XPlatPalette.Dark))
            {
                Shortcut = new XPlatShortcut(XPlatKey.D),
            },
            new XPlatMenuItem("Light Mode", () => demo.SetAppearance(XPlatPalette.Light))
            {
                Shortcut = new XPlatShortcut(XPlatKey.L),
            }),
        new XPlatMenu(
            "Window",
            new XPlatMenuItem("Topmost", () =>
            {
                window.TopMost = !window.TopMost;
                demo.SetStatus(window.TopMost
                    ? "The window is now above every other window."
                    : "The window is back in the normal stacking order.");
            })
            {
                Shortcut = new XPlatShortcut(XPlatKey.T),
            },
            new XPlatMenuItem("Owned Window", demo.ShowOwnedWindow)
            {
                Shortcut = new XPlatShortcut(XPlatKey.N),
            }));

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
