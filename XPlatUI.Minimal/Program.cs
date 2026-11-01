using SkiaSharp;
using XPlatUI;

#if WINDOWS
using XPlatUI.Backend.Win32;
#elif MACOS
using XPlatUI.Backend.AppKit;
#else
using XPlatUI.Backend.Gtk4;
#endif

namespace XPlatUI.Minimal;

/// <summary>
/// The measurement fixture described in <c>FEATURE-COST-POLICY.md</c>: one window, one label, no
/// features. Its NativeAOT size is the floor every feature is measured against, so it must not grow
/// options, icons, menu bars or controls. If it needs something a real application would also need,
/// that something belongs in XPlatUI's build assets instead.
/// </summary>
internal static class Program
{
    /// <summary>
    /// Entry point. The program avoids Windows Forms and System.Drawing entirely, so the whole
    /// application stays trimmable and publishes with NativeAOT.
    /// </summary>
    /// <remarks>
    /// The attribute is not decoration: the runtime reads it before <c>Main</c> runs and initializes
    /// the thread as a single-threaded COM apartment, which the shell's file dialogs require.
    /// </remarks>
    [STAThread]
    private static int Main()
    {
        // Must run before anything touches SkiaSharp: an AOT publish carries the native Skia library
        // inside the executable instead of next to it. The resource is embedded in this assembly by
        // XPlatUI's build assets, which is why this assembly is the one named here.
        SkiaNativeLoader.EnsureLoaded(typeof(Program).Assembly);

        var options = new XPlatApplicationOptions
        {
            ApplicationName = "XPlat UI Minimal",
#if WINDOWS
            // A WinExe has no console of its own; borrow the parent's so diagnostics are visible.
            AttachConsole = true,
#endif
        };

        using var application = new XPlatApplication(CreateBackend(), options);

        var window = new XPlatWindow("XPlat UI - Minimal", new XPlatLabel("Hello, XPlatUI."))
        {
            ClientSize = new SKSize(480f, 240f),
        };

        application.MainWindow = window;
        window.Show();

        return application.Run();
    }

    /// <summary>Creates the backend for the platform this build is for.</summary>
    private static IXPlatApplicationBackend CreateBackend() =>
#if WINDOWS
        new Win32ApplicationBackend();
#elif MACOS
        new AppKitApplicationBackend();
#else
        new Gtk4ApplicationBackend();
#endif
}
