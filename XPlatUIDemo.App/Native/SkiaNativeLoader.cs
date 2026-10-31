#if WINDOWS
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;

namespace XPlatUI.Demo.App;

/// <summary>
/// A NativeAOT publish produces a single native executable, so SkiaSharp's native library cannot
/// simply sit next to it. When the application is published with AOT the build embeds a compressed
/// copy of <c>libSkiaSharp.dll</c> as a manifest resource; this extracts it into a per-user cache
/// and loads it before SkiaSharp's first P/Invoke.
/// </summary>
/// <remarks>
/// Windows only: macOS and Linux get <c>libSkiaSharp.dylib</c> and <c>libSkiaSharp.so</c> as loose
/// files from their native asset packages, and neither is published with AOT yet.
/// </remarks>
internal static class SkiaNativeLoader
{
    private const string NativeLibraryFileName = "libSkiaSharp.dll";
    private const string ResourceName = "XPlatUI.Native.libSkiaSharp.dll.gz";

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

        // Loading by full path means the module is already in memory when SkiaSharp's first P/Invoke
        // asks for "libSkiaSharp.dll" by name; the DLL directory also covers the search path.
        NativeLibrary.Load(path);
        SetDllDirectory(directory);
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

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetDllDirectory(string path);
}
#endif
