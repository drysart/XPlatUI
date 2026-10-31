using SkiaSharp;
using XPlatUI;
using XPlatUI.Backend.Win32;
using XPlatUI.Demo;

namespace WinFormsApp3;

/// <summary>
/// The Windows harness. It is deliberately tiny: everything platform specific lives in
/// <see cref="Win32ApplicationBackend"/>, so the whole program is "pick a backend, describe a
/// window, run" - the same shape a macOS or Linux build of the same application has.
/// </summary>
internal static class Program
{
    /// <summary>The sample UI is designed against this logical (96 DPI) client size.</summary>
    private static readonly SKSize DesignClientSize = new(1280f, 820f);

    /// <summary>Below this the sample layout has nothing left to squeeze.</summary>
    private static readonly SKSize DesignMinimumClientSize = new(820f, 716f);

    /// <summary>Drives caret blinking and any other time-based UI behaviour.</summary>
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Entry point. The harness deliberately avoids Windows Forms and System.Drawing: the window is
    /// a plain Win32 window and everything inside it is drawn with SkiaSharp, so the whole program
    /// stays trimmable and can be published with NativeAOT.
    /// </summary>
    [STAThread]
    private static int Main(string[] args)
    {
        // Must happen before anything touches SkiaSharp: an AOT publish carries the native Skia
        // library inside the executable instead of next to it.
        SkiaNativeLoader.EnsureLoaded();

        var backend = new Win32ApplicationBackend
        {
            ForceClassicMenus = Array.IndexOf(args, "--classic-menus") >= 0,
        };

        // Headless render of the sample UI, handy for visual checks and CI. It paints through the
        // headless backend rather than the window one, so it needs no window and no message loop:
        //   WinFormsApp3.exe --render output.png [width height] [--dark|--light]
        if (args.Length > 0 && args[0] == "--render")
        {
            ApplyAppearanceOverride(args, backend);

            var path = args.Length > 1 ? args[1] : "xplatui.png";
            var width = args.Length > 2 && int.TryParse(args[2], out var w) ? w : (int)DesignClientSize.Width;
            var height = args.Length > 3 && int.TryParse(args[3], out var h) ? h : (int)DesignClientSize.Height;
            DemoUi.RenderToPng(path, width, height);
            Console.WriteLine($"Rendered {width}x{height} sample UI to {path}");
            return 0;
        }

        var options = new XPlatApplicationOptions
        {
            ApplicationName = "XPlat UI",
            // A WinExe has no console of its own; borrow the parent's so diagnostics are visible.
            AttachConsole = true,
            ForcedDarkMode = ForcedDarkMode(args),
            TickInterval = TickInterval,
        };

        using var application = new XPlatApplication(backend, options);

        var window = new XPlatWindow("XPlat UI - SkiaSharp test harness", DemoUi.CreateRoot())
        {
            ClientSize = DesignClientSize,
            MinimumClientSize = DesignMinimumClientSize,
        };

        application.MainWindow = window;
        window.Show();

        return application.Run();
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

    /// <summary>
    /// Applies <c>--dark</c> / <c>--light</c> if given, and otherwise the machine's own preference.
    /// </summary>
    /// <remarks>
    /// The second half matters only for <c>--render</c>: the normal path lets
    /// <see cref="XPlatApplication"/> install <see cref="Win32ApplicationBackend.SystemTheme"/> and
    /// pick the palette up itself, but a headless render has no application to do that.
    /// </remarks>
    private static void ApplyAppearanceOverride(string[] args, Win32ApplicationBackend backend)
    {
        if (ForcedDarkMode(args) is { } dark)
        {
            XPlatTheme.Palette = dark ? XPlatPalette.Dark : XPlatPalette.Light;
            return;
        }

        XPlatSystemTheme.Current = backend.SystemTheme;
        XPlatSystemTheme.Refresh();
    }
}
