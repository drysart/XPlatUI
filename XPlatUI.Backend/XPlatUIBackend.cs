using System.Reflection;

#if WINDOWS
using XPlatUI.Backend.Win32;
#elif MACOS
using XPlatUI.Backend.AppKit;
#else
using XPlatUI.Backend.Gtk4;
#endif

namespace XPlatUI.Backend;

/// <summary>
/// Creates the backend an application runs on, so the application never has to name one.
/// </summary>
/// <remarks>
/// <para>
/// Which backend an application gets is decided by its target framework, not at run time:
/// <c>net10.0-windows</c> gets the Win32 backend, <c>net10.0-macos</c> the AppKit one and
/// <c>net10.0</c> the GTK 4 one. That is what keeps a publish from carrying backends it cannot use,
/// which a run-time choice could not do without defeating trimming.
/// </para>
/// <para>
/// <c>net10.0</c> is the one framework that more than one platform can compile against, so a build
/// for it is correct on Linux only. <see cref="CreateDefault"/> says so instead of letting GTK fail
/// later with an error that names the wrong cause.
/// </para>
/// </remarks>
public static class XPlatUIBackend
{
    /// <summary>
    /// Creates the backend for the platform this build is for, and makes SkiaSharp's native library
    /// resolvable.
    /// </summary>
    /// <returns>The backend to hand to <see cref="XPlatApplication"/>.</returns>
    /// <exception cref="PlatformNotSupportedException">
    /// This build targets <c>net10.0</c> but is not running on Linux. Retarget the application at
    /// <c>net10.0-windows</c> or <c>net10.0-macos</c>.
    /// </exception>
    public static IXPlatApplicationBackend CreateDefault()
    {
        // A NativeAOT publish folds SkiaSharp's native library into the application's own assembly
        // rather than leaving it next to the executable, so the assembly to look in is the
        // application's - and it is not this one. Doing it here instead of in the application's Main
        // means it cannot be forgotten, and the loader is idempotent, so an application that still
        // calls it explicitly pays nothing. GetEntryAssembly is null only in a host, which has no
        // embedded copy to find.
        if (Assembly.GetEntryAssembly() is { } application)
        {
            SkiaNativeLoader.EnsureLoaded(application);
        }

#if WINDOWS
        return new Win32ApplicationBackend();
#elif MACOS
        return new AppKitApplicationBackend();
#else
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException(
                "This build targets net10.0, which selects the GTK 4 backend, so it runs on Linux "
                + "only. Target net10.0-windows or net10.0-macos to run on this platform.");
        }

        return new Gtk4ApplicationBackend();
#endif
    }
}
