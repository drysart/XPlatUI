using SkiaSharp;
using XPlatUI;
using XPlatUI.Backend.AppKit;
using XPlatUI.Demo;

namespace MacHarness;

/// <summary>
/// The macOS harness. Like the Windows one it is deliberately tiny: everything platform specific
/// lives in <see cref="AppKitApplicationBackend"/>, so the whole program is "pick a backend,
/// describe a window, run" - the same shape the Windows and Linux harnesses have.
/// </summary>
internal static class Program
{
    /// <summary>The sample UI is designed against this logical (96 DPI) client size.</summary>
    private static readonly SKSize DesignClientSize = new(1280f, 820f);

    /// <summary>Below this the sample layout has nothing left to squeeze.</summary>
    private static readonly SKSize DesignMinimumClientSize = new(820f, 716f);

    /// <summary>Drives caret blinking and any other time-based UI behaviour.</summary>
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(250);

    private static int Main(string[] args)
    {
        // Headless render of the sample UI, handy for visual checks and CI. It paints through the
        // headless backend rather than the window one, so it needs no window server and works over
        // ssh:   MacHarness --render output.png [width height] [--dark|--light]
        if (args.Length > 0 && args[0] == "--render")
        {
            return RenderSample(args);
        }

        var options = new XPlatApplicationOptions
        {
            ApplicationName = "XPlat UI",
            // Draw with a real macOS face rather than SkiaSharp's default, so the controls match
            // the rest of the system.
            FontFamily = AppKitSystemTheme.PreferredFontFamily(),
            ForcedDarkMode = ForcedDarkMode(args),
            TickInterval = TickInterval,
        };

        using var application = new XPlatApplication(new AppKitApplicationBackend(), options);

        var window = new XPlatWindow("XPlat UI - AppKit test harness", DemoUi.CreateRoot())
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
    /// The window's own menu bar. macOS adds the application and Edit menus itself, so this only
    /// carries what the application brings; the Edit menu's accelerators are routed into the
    /// focused control by the backend.
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

        XPlatTheme.FontFamily = AppKitSystemTheme.PreferredFontFamily();
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
    /// system. Handy for screenshots and for checking both appearances on one machine.
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