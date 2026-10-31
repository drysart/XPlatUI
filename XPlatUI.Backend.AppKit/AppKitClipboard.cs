using AppKit;

namespace XPlatUI.Backend.AppKit;

/// <summary>
/// The macOS clipboard. Unlike Win32 and X11 there is nothing to pump: NSPasteboard reads and
/// writes synchronously, so the library can call it straight from a menu command.
/// </summary>
internal sealed class AppKitClipboard : IXPlatClipboard
{
    private const string PlainTextType = "public.utf8-plain-text";
    private const string HtmlType = "public.html";

    public string? GetText() => NSPasteboard.GeneralPasteboard.GetStringForType(PlainTextType);

    public void SetText(string? text) => Write(text, null);

    public string? GetHtml() => NSPasteboard.GeneralPasteboard.GetStringForType(HtmlType);

    public void SetHtml(string? text, string? html) => Write(text, html);

    private static void Write(string? text, string? html)
    {
        var pasteboard = NSPasteboard.GeneralPasteboard;
        pasteboard.ClearContents();

        if (string.IsNullOrEmpty(text) && string.IsNullOrEmpty(html))
        {
            return;
        }

        if (!string.IsNullOrEmpty(text))
        {
            pasteboard.SetStringForType(text, PlainTextType);
        }

        // macOS takes the HTML fragment as-is; unlike Windows there is no CF_HTML envelope to add.
        if (!string.IsNullOrEmpty(html))
        {
            pasteboard.SetStringForType(html, HtmlType);
        }
    }
}