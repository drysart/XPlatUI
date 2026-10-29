using AppKit;
using CoreGraphics;
using XPlatUI;
using XPlatUI.Demo;

namespace MacHarness;

/// <summary>
/// Entry point for the macOS harness: a native AppKit application whose only window hosts the
/// XPlat UI library. It is the counterpart of the Win32 harness in WinFormsApp3 and of the GTK4
/// harness in GtkHarness - the same sample control tree, drawn by the same library, on a different
/// native surface.
/// </summary>
internal static class Program
{
    private const string WindowTitle = "XPlat UI - AppKit test harness";

    /// <summary>The sample UI is designed against this logical (96 DPI) client size.</summary>
    private const int DesignClientWidth = 1280;
    private const int DesignClientHeight = 760;
    private const int DesignMinimumWidth = 820;
    private const int DesignMinimumHeight = 660;

    /// <summary>
    /// The live UI surface. It owns the invalidation and cursor subscriptions and the caret timer,
    /// so it has to outlive the launch: disposing it with the window would leave the window
    /// showing its first frame and never repainting.
    /// </summary>
    private static MacUiView? _surface;

    private static NSWindow? _window;
    private static MacContextMenuPresenter? _menus;
    private static MacSystemTheme? _systemTheme;
    private static MacAppDelegate? _appDelegate;

    private static int Main(string[] args)
    {
        // Headless render of the sample UI, handy for visual checks and CI. It deliberately runs
        // before AppKit is initialised, so it also works with no window server (ssh, CI agents):
        //   MacHarness --render output.png [width height] [--dark|--light]
        if (args.Length > 0 && args[0] == "--render")
        {
            return RenderSample(args);
        }

        // Registers the managed/Objective-C bridge. Nothing AppKit related works before this.
        NSApplication.Init();

        // Draw with a real macOS face rather than SkiaSharp's default, so the controls match the
        // rest of the system. Must happen before any control is created.
        XPlatTheme.FontFamily = MacSystemTheme.PreferredFontFamily();

        // Give the UI library a real clipboard; the library itself stays platform free.
        XPlatClipboard.Current = new MacClipboard();

        var application = NSApplication.SharedApplication;
        application.ActivationPolicy = NSApplicationActivationPolicy.Regular;

        // Likewise for the system's light/dark preference, read before any control exists so
        // nothing is ever painted in the wrong palette.
        _systemTheme = new MacSystemTheme();
        XPlatSystemTheme.Current = _systemTheme;
        XPlatSystemTheme.Refresh();
        ApplyAppearanceOverride(args);

        // The delegate must stay rooted: AppKit holds an unretained reference to it.
        _appDelegate = new MacAppDelegate();
        application.Delegate = _appDelegate;
        application.MainMenu = MacMenuBar.Build();

        application.Run();
        return 0;
    }

    /// <summary>Builds the window and the sample UI. Called from the application delegate.</summary>
    internal static void Launch()
    {
        var systemTheme = _systemTheme!;

        var window = new NSWindow(
            new CGRect(0, 0, DesignClientWidth, DesignClientHeight),
            NSWindowStyle.Titled | NSWindowStyle.Closable |
            NSWindowStyle.Miniaturizable | NSWindowStyle.Resizable,
            NSBackingStore.Buffered,
            deferCreation: false)
        {
            Title = WindowTitle,
        };

        // The managed side owns the window, so AppKit must not release it when it closes. The
        // property setter is obsolete; the method is the supported spelling of the same thing.
        window.ReleaseWhenClosed(false);

        window.ContentMinSize = new CGSize(DesignMinimumWidth, DesignMinimumHeight);
        systemTheme.ApplyToWindow(window, XPlatTheme.IsDarkMode);

        _surface = new MacUiView { Frame = new CGRect(0, 0, DesignClientWidth, DesignClientHeight) };
        window.ContentView = _surface;
        window.Center();
        window.MakeKeyAndOrderFront(null!);
        window.MakeFirstResponder(_surface);
        _window = window;

        // Context menus are platform native, so the UI library only describes them and the host
        // presents them. Installed after the view exists because a menu is anchored to it.
        _menus = new MacContextMenuPresenter(_surface);
        XPlatContextMenuPresenter.Current = _menus;

        _surface.Root = new DemoUi().Root;

        // The system appearance can flip at any time; follow it, and keep the window furniture in
        // step with what SkiaSharp draws inside it.
        _surface.Ticked += (_, _) => systemTheme.Poll();
        systemTheme.Changed += (_, _) =>
        {
            if (XPlatSystemTheme.Refresh())
            {
                systemTheme.ApplyToWindow(window, XPlatTheme.IsDarkMode);
            }
        };

        _surface.Start();
        NSApplication.SharedApplication.Activate();
    }

    /// <summary>Tears the UI down. Called from the application delegate.</summary>
    internal static void Shutdown()
    {
        XPlatContextMenuPresenter.Current = null;
        _menus = null;

        // Detach the view before its drawing buffers go away, so AppKit cannot ask it to draw a
        // frame it no longer has anything to draw with.
        _window?.ContentView = null!;
        _window = null;

        _surface?.Dispose();
        _surface = null;

        XPlatSystemTheme.Current = null;
        _systemTheme = null;
    }

    /// <summary>
    /// Feeds one of the standard editing accelerators into the UI, so the focused control handles
    /// it exactly as it would a real key press.
    /// </summary>
    internal static void SendAccelerator(XPlatKey key) => _surface?.SendAccelerator(key);

    private static int RenderSample(string[] args)
    {
        var path = args.Length > 1 ? args[1] : "xplatui.png";
        var width = args.Length > 2 && int.TryParse(args[2], out var w) ? w : DesignClientWidth;
        var height = args.Length > 3 && int.TryParse(args[3], out var h) ? h : DesignClientHeight;

        XPlatTheme.FontFamily = MacSystemTheme.PreferredFontFamily();
        ApplyAppearanceOverride(args);

        DemoUi.RenderToPng(path, width, height);
        Console.WriteLine($"Rendered {width}x{height} sample UI to {path}");
        return 0;
    }

    /// <summary>
    /// Honours <c>--dark</c> / <c>--light</c>, which force a palette instead of following the
    /// system. Handy for screenshots and for checking both appearances on one machine.
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