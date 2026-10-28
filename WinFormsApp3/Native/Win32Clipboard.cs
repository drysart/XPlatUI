using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
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

    /// <summary>
    /// The registered "HTML Format" clipboard format. Windows has no constant for it, so the
    /// name is registered once and the id it returns is used from then on.
    /// </summary>
    private static readonly uint HtmlFormat = NativeMethods.RegisterClipboardFormatW("HTML Format");

    // The byte offsets in a CF_HTML header are fixed width, so filling them in does not change
    // the header length and the offsets stay correct.
    private const string HtmlHeader =
        "Version:0.9\r\n" +
        "StartHTML:{0:0000000000}\r\n" +
        "EndHTML:{1:0000000000}\r\n" +
        "StartFragment:{2:0000000000}\r\n" +
        "EndFragment:{3:0000000000}\r\n";

    private const string HtmlPrefix = "<html><body>\r\n<!--StartFragment-->";
    private const string HtmlSuffix = "<!--EndFragment-->\r\n</body></html>";

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

    public string? GetHtml()
    {
        if (HtmlFormat == 0 ||
            !NativeMethods.IsClipboardFormatAvailable(HtmlFormat) ||
            !TryOpen())
        {
            return null;
        }

        try
        {
            var handle = NativeMethods.GetClipboardData(HtmlFormat);
            var size = handle == IntPtr.Zero ? 0 : (int)NativeMethods.GlobalSize(handle);
            var pointer = handle == IntPtr.Zero ? IntPtr.Zero : NativeMethods.GlobalLock(handle);
            if (pointer == IntPtr.Zero || size <= 0)
            {
                return null;
            }

            try
            {
                var bytes = new byte[size];
                Marshal.Copy(pointer, bytes, 0, size);

                // Other applications are not obliged to terminate the payload.
                var terminator = Array.IndexOf(bytes, (byte)0);
                return Encoding.UTF8.GetString(bytes, 0, terminator >= 0 ? terminator : size);
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
            SetUnicodeText(text);
        }
        finally
        {
            NativeMethods.CloseClipboard();
        }
    }

    public void SetHtml(string? text, string? html)
    {
        if (string.IsNullOrEmpty(html))
        {
            SetText(text);
            return;
        }

        if (!TryOpen())
        {
            return;
        }

        try
        {
            NativeMethods.EmptyClipboard();

            // Plain text first: it is what simple targets paste, and the copy still works if the
            // HTML block cannot be allocated.
            SetUnicodeText(text);
            SetHtmlText(html);
        }
        finally
        {
            NativeMethods.CloseClipboard();
        }
    }

    /// <summary>Builds a CF_HTML payload: a header of byte offsets, then the fragment markers.</summary>
    internal static string BuildHtmlPayload(string html)
    {
        var prefixBytes = Encoding.UTF8.GetByteCount(HtmlPrefix);
        var htmlBytes = Encoding.UTF8.GetByteCount(html);
        var suffixBytes = Encoding.UTF8.GetByteCount(HtmlSuffix);
        var headerBytes = Encoding.UTF8.GetByteCount(string.Format(
            CultureInfo.InvariantCulture,
            HtmlHeader,
            0,
            0,
            0,
            0));

        var startHtml = headerBytes;
        var startFragment = startHtml + prefixBytes;
        var endFragment = startFragment + htmlBytes;
        var endHtml = endFragment + suffixBytes;

        var header = string.Format(
            CultureInfo.InvariantCulture,
            HtmlHeader,
            startHtml,
            endHtml,
            startFragment,
            endFragment);

        return string.Concat(header, HtmlPrefix, html, HtmlSuffix);
    }

    /// <summary>Copies text into the open clipboard. The caller owns opening and closing it.</summary>
    private static void SetUnicodeText(string? text)
    {
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

    /// <summary>Copies an HTML fragment into the open clipboard. The caller owns opening and closing it.</summary>
    private static void SetHtmlText(string html)
    {
        if (HtmlFormat == 0)
        {
            return;
        }

        var bytes = Encoding.UTF8.GetBytes(BuildHtmlPayload(html));
        var handle = NativeMethods.GlobalAlloc(NativeMethods.GmemMoveable, (nuint)(bytes.Length + 1));
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

        Marshal.Copy(bytes, 0, pointer, bytes.Length);
        Marshal.WriteByte(pointer, bytes.Length, 0);
        NativeMethods.GlobalUnlock(handle);

        if (NativeMethods.SetClipboardData(HtmlFormat, handle) == IntPtr.Zero)
        {
            NativeMethods.GlobalFree(handle);
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
