using System.Runtime.InteropServices;
using XPlatUI;

namespace WinFormsApp3;

/// <summary>
/// Clipboard access for the Windows host, implemented directly over the Win32 clipboard so
/// the harness needs no Windows Forms or System.Drawing reference.
/// </summary>
internal sealed class Win32Clipboard : IXPlatClipboard
{
    private const int OpenAttempts = 10;
    private const int OpenRetryDelayMs = 10;

    public string? GetText()
    {
        if (!NativeMethods.IsClipboardFormatAvailable(NativeMethods.CfUnicodeText) || !TryOpen())
        {
            return null;
        }

        try
        {
            var handle = NativeMethods.GetClipboardData(NativeMethods.CfUnicodeText);
            if (handle == IntPtr.Zero)
            {
                return null;
            }

            var pointer = NativeMethods.GlobalLock(handle);
            if (pointer == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                return Marshal.PtrToStringUni(pointer);
            }
            finally
            {
                NativeMethods.GlobalUnlock(handle);
            }
        }
        finally
        {
            NativeMethods.CloseClipboard();
        }
    }

    public void SetText(string? text)
    {
        if (!TryOpen())
        {
            return;
        }

        try
        {
            NativeMethods.EmptyClipboard();
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            var bytes = (nuint)((text.Length + 1) * sizeof(char));
            var handle = NativeMethods.GlobalAlloc(NativeMethods.GmemMoveable, bytes);
            if (handle == IntPtr.Zero)
            {
                return;
            }

            var pointer = NativeMethods.GlobalLock(handle);
            if (pointer == IntPtr.Zero)
            {
                NativeMethods.GlobalFree(handle);
                return;
            }

            var characters = text.ToCharArray();
            Marshal.Copy(characters, 0, pointer, characters.Length);
            Marshal.WriteInt16(pointer, characters.Length * sizeof(char), 0);
            NativeMethods.GlobalUnlock(handle);

            // The clipboard takes ownership of the block on success, so only free it on failure.
            if (NativeMethods.SetClipboardData(NativeMethods.CfUnicodeText, handle) == IntPtr.Zero)
            {
                NativeMethods.GlobalFree(handle);
            }
        }
        finally
        {
            NativeMethods.CloseClipboard();
        }
    }

    /// <summary>The clipboard is a shared, singly-owned resource, so retry briefly when another app holds it.</summary>
    private static bool TryOpen()
    {
        for (var attempt = 0; attempt < OpenAttempts; attempt++)
        {
            if (NativeMethods.OpenClipboard(IntPtr.Zero))
            {
                return true;
            }

            Thread.Sleep(OpenRetryDelayMs);
        }

        return false;
    }
}
