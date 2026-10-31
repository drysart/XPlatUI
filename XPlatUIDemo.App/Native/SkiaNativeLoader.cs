using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using SkiaSharp;

namespace XPlatUI.Demo.App;

/// <summary>
/// A NativeAOT publish produces a single native executable, so SkiaSharp's native library cannot
/// simply sit next to it. When the application is published with AOT the build embeds a compressed
/// copy of the platform's Skia library as a manifest resource; this extracts it into a per-user cache
/// and redirects SkiaSharp's P/Invoke at it before the first call into Skia.
/// </summary>
/// <remarks>
/// Windows and Linux only. macOS is not published with AOT because a <c>.app</c> bundle already
/// carries its native libraries as loose files inside the bundle, so there is nothing to fold in.
/// </remarks>
internal static class SkiaNativeLoader
{
    /// <summary>The name SkiaSharp's <c>DllImport</c> attributes ask for, without the platform suffix.</summary>
    private const string ImportName = "libSkiaSharp";

    /// <summary>The file the native asset package ships on this platform.</summary>
    private static readonly string NativeLibraryFileName =
        OperatingSystem.IsWindows() ? "libSkiaSharp.dll"
        : OperatingSystem.IsMacOS() ? "libSkiaSharp.dylib"
        : "libSkiaSharp.so";

    private static readonly string ResourceName = $"XPlatUI.Native.{NativeLibraryFileName}.gz";

    /// <summary>Makes sure the native Skia library can be resolved by the P/Invoke loader.</summary>
    public static void EnsureLoaded()
    {
        // A regular build copies the DLL next to the executable, so there is nothing to do.
        if (File.Exists(Path.Combine(AppContext.BaseDirectory, NativeLibraryFileName)))
        {
            return;
        }

        var assembly = typeof(SkiaNativeLoader).Assembly;
        using var resource = assembly.GetManifestResourceStream(ResourceName);
        if (resource is null)
        {
            // Nothing embedded: let the default probing find it (e.g. an installed copy).
            return;
        }

        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "XPlatUI",
            "native",
            CacheKey(assembly, resource.Length));
        var path = Path.Combine(directory, NativeLibraryFileName);

        if (!File.Exists(path))
        {
            Extract(resource, directory, path);
        }

        // Loading by full path is not enough on its own: libSkiaSharp.so carries no SONAME, so a
        // later dlopen("libSkiaSharp.so") would search the library paths instead of reusing the
        // module already in memory. Redirecting the import sidesteps that, and it is the same
        // mechanism on Windows, where the library is otherwise looked up by name next to the
        // executable.
        NativeLibrary.SetDllImportResolver(typeof(SKBitmap).Assembly, (name, _, _) =>
            string.Equals(name, ImportName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, NativeLibraryFileName, StringComparison.OrdinalIgnoreCase)
                ? NativeLibrary.Load(path)
                : IntPtr.Zero);
    }

    private static string CacheKey(Assembly assembly, long archiveLength)
    {
        var version = assembly.GetName().Version?.ToString() ?? "0";
        return $"{version}-{archiveLength:x}";
    }

    private static void Extract(Stream archive, string directory, string path)
    {
        Directory.CreateDirectory(directory);

        // Write to a unique temporary file first so parallel processes never see a partial DLL.
        var temporaryPath = $"{path}.{Environment.ProcessId:x}.tmp";
        try
        {
            using (var file = File.Create(temporaryPath))
            using (var gzip = new GZipStream(archive, CompressionMode.Decompress))
            {
                gzip.CopyTo(file);
            }

            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}
