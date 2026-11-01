using SkiaSharp;
using XPlatUI;
using XPlatUI.Backend;

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
        var options = new XPlatApplicationOptions
        {
            ApplicationName = "XPlat UI Minimal",
#if WINDOWS
            // A WinExe has no console of its own; borrow the parent's so diagnostics are visible.
            AttachConsole = true,
#endif
        };

        // Both of the platform-specific steps an application used to have to take itself - choosing
        // the backend and making SkiaSharp's native library resolvable - happen inside this call.
        using var application = new XPlatApplication(XPlatUIBackend.CreateDefault(), options);

        var window = new XPlatWindow("XPlat UI - Minimal", new XPlatLabel("Hello, XPlatUI."))
        {
            ClientSize = new SKSize(480f, 240f),
        };

        application.MainWindow = window;
        window.Show();

        return application.Run();
    }
}
