using AppKit;
using Foundation;

namespace MacHarness;

/// <summary>
/// The application delegate. It only forwards the two lifecycle events the harness cares about to
/// <see cref="Program"/>, which keeps all of the window and UI state in one place.
/// </summary>
internal sealed class MacAppDelegate : NSApplicationDelegate
{
    public override void DidFinishLaunching(NSNotification notification) => Program.Launch();

    public override void WillTerminate(NSNotification notification) => Program.Shutdown();

    /// <summary>Closing the window ends the application, which is what a single window app should do.</summary>
    public override bool ApplicationShouldTerminateAfterLastWindowClosed(NSApplication sender) => true;
}