using System.Runtime.InteropServices;

namespace XPlatUI.Backend.Gtk4;

/// <summary>Reads the files out of the boxed <c>GdkFileList</c> that a GTK drag hands over.</summary>
/// <remarks>
/// <para>
/// The GTK binding surfaces <c>Gdk.FileList</c> as a create-only type: it can build one from a list
/// of files, but it generates no accessor for the files a list already holds. The two GTK entry
/// points that read it are therefore called directly. Both are part of the stable GTK4 and GLib ABI,
/// and this backend only ever runs on Linux.
/// </para>
/// <para>
/// A <c>GSList</c> node is a pair of pointers - the data, then the next node - so the list is walked
/// as raw memory rather than through the binding's own list wrapper, which cannot be built from a
/// pointer the binding does not own.
/// </para>
/// </remarks>
internal static partial class Gtk4FileList
{
    /// <summary>Reads the local paths of the files in a boxed <c>GdkFileList</c>.</summary>
    /// <param name="boxed">The boxed file list, or <see cref="IntPtr.Zero"/> when there is none.</param>
    /// <returns>The paths, in the order the drag source listed them; empty when there are none.</returns>
    public static unsafe string[] ReadPaths(IntPtr boxed)
    {
        if (boxed == IntPtr.Zero)
        {
            return [];
        }

        var node = (nint*)GdkFileListGetFiles(boxed);
        List<string> paths = [];

        while (node is not null)
        {
            // peek_path borrows the string from the file object, so nothing has to be freed.
            var path = GFilePeekPath(node[0]);
            if (path != IntPtr.Zero && Marshal.PtrToStringUTF8(path) is { Length: > 0 } text)
            {
                paths.Add(text);
            }

            node = (nint*)node[1];
        }

        return [.. paths];
    }

    [LibraryImport("libgtk-4.so.1")]
    private static partial IntPtr GdkFileListGetFiles(IntPtr fileList);

    [LibraryImport("libgio-2.0.so.0")]
    private static partial IntPtr GFilePeekPath(IntPtr file);
}
