using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using SkiaSharp;

namespace XPlatUI;

/// <summary>
/// Makes SkiaSharp's native library resolvable when a NativeAOT publish has folded it into the
/// executable.
/// </summary>
/// <remarks>
/// <para>
/// An AOT publish produces a single native executable, so the Skia library cannot simply sit next to
/// it. The build assets shipped with this library (<c>buildTransitive/XPlatUI.targets</c>) embed a
/// compressed copy in the application and this extracts it into a per-user cache and redirects
/// SkiaSharp's P/Invoke at it.
/// </para>
/// <para>
/// The resource belongs to the <em>application</em>, not to this assembly, because the copy to embed
/// is the one the application's build resolved for its platform. That is why
/// <see cref="EnsureLoaded"/> takes the assembly to look in rather than assuming its own.
/// </para>
/// </remarks>
public static class SkiaNativeLoader
{
    /// <summary>The name SkiaSharp's <c>DllImport</c> attributes ask for, without the platform suffix.</summary>
    private const string ImportName = "libSkiaSharp";

    /// <summary>The file the native asset package ships on this platform.</summary>
    private static readonly string NativeLibraryFileName =
        OperatingSystem.IsWindows() ? "libSkiaSharp.dll"
        : OperatingSystem.IsMacOS() ? "libSkiaSharp.dylib"
        : "libSkiaSharp.so";

    /// <summary>Serializes <see cref="EnsureLoaded"/> so the resolver is registered at most once.</summary>
    private static readonly object Gate = new();

    /// <summary>Whether <see cref="Load"/> has already run to completion.</summary>
    private static bool _loaded;

    /// <summary>
    /// Makes sure the native Skia library can be resolved by the P/Invoke loader.
    /// </summary>
    /// <param name="resourceAssembly">
    /// The assembly the embedded library was compiled into, which is the application's own assembly.
    /// Pass <c>typeof(Program).Assembly</c>, or <c>Assembly.GetEntryAssembly()</c> where the
    /// application is not the caller.
    /// </param>
    /// <remarks>
    /// <para>
    /// Must run before the first call into SkiaSharp, so it belongs at the top of <c>Main</c>. A
    /// regular build copies the library next to the executable and this does nothing.
    /// </para>
    /// <para>
    /// Calling it more than once is safe and does nothing the second time. That matters because the
    /// call has two natural homes: the application's <c>Main</c>, and
    /// <c>XPlatUIBackend.CreateDefault</c>, which an application may use alongside a call of its own.
    /// The P/Invoke loader rejects a second resolver for the same assembly, so without this the two
    /// would not coexist.
    /// </para>
    /// </remarks>
    public static void EnsureLoaded(Assembly resourceAssembly)
    {
        ArgumentNullException.ThrowIfNull(resourceAssembly);

        lock (Gate)
        {
            if (_loaded)
            {
                return;
            }

            Load(resourceAssembly);
            _loaded = true;
        }
    }

    /// <summary>Does the work of <see cref="EnsureLoaded"/>, which decides whether it is needed.</summary>
    private static void Load(Assembly resourceAssembly)
    {
        // A regular build copies the DLL next to the executable, so there is nothing to do.
        if (File.Exists(Path.Combine(AppContext.BaseDirectory, NativeLibraryFileName)))
        {
            return;
        }

        using var resource = resourceAssembly.GetManifestResourceStream(ResourceName);
        if (resource is null)
        {
            // Nothing embedded: let the default probing find it (e.g. an installed copy).
            return;
        }

        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "XPlatUI",
            "native",
            CacheKey(resourceAssembly, resource.Length));
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

    /// <summary>The logical name <c>buildTransitive/XPlatUI.targets</c> embeds the library under.</summary>
    private static string ResourceName => $"XPlatUI.Native.{NativeLibraryFileName}.gz";

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
