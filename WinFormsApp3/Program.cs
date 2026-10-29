using XPlatUI;

namespace WinFormsApp3
{
    internal static class Program
    {
        /// <summary>
        /// Entry point. The harness deliberately avoids Windows Forms and System.Drawing: the
        /// window is a plain Win32 window and everything inside it is drawn with SkiaSharp, so the
        /// whole program stays trimmable and can be published with NativeAOT.
        /// </summary>
        [STAThread]
        private static int Main(string[] args)
        {
            // Must happen before anything touches SkiaSharp: an AOT publish carries the native
            // Skia library inside the executable instead of next to it.
            SkiaNativeLoader.EnsureLoaded();

            // A WinExe has no console of its own; borrow the parent's so diagnostics are visible.
            NativeMethods.AttachConsole(AttachParentProcess);

            // Opt in to per-monitor DPI awareness before any window exists.
            NativeMethods.TryEnablePerMonitorDpi();

            // Give the UI library a real clipboard; the library itself stays platform free.
            XPlatClipboard.Current = new Win32Clipboard();

            // Headless render of the sample UI, handy for visual checks and CI:
            //   WinFormsApp3.exe --render output.png [width height]
            if (args.Length > 0 && args[0] == "--render")
            {
                var path = args.Length > 1 ? args[1] : "xplatui.png";
                var width = args.Length > 2 && int.TryParse(args[2], out var w) ? w : 1280;
                var height = args.Length > 3 && int.TryParse(args[3], out var h) ? h : 760;
                DemoUi.RenderToPng(path, width, height);
                Console.WriteLine($"Rendered {width}x{height} sample UI to {path}");
                return 0;
            }

            using var window = new HarnessWindow();
            if (!window.Create())
            {
                Console.Error.WriteLine("Failed to create the harness window.");
                return 1;
            }

            // Context menus are platform native, so the UI library only describes them and the
            // host draws them. Installed after the window exists because Win32 needs an owner.
            XPlatContextMenuPresenter.Current = new Win32ContextMenuPresenter(window.Handle, window.LogicalToScreen);

            window.Root = new DemoUi().Root;
            return window.Run();
        }

        private const uint AttachParentProcess = 0xFFFFFFFF;
    }
}