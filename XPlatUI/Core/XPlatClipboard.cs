namespace XPlatUI;

/// <summary>
/// Text clipboard used by controls such as <see cref="XPlatTextBox"/>. A host adapter
/// installs a platform implementation into <see cref="XPlatClipboard.Current"/>;
/// when none is installed an in-process implementation is used.
/// </summary>
public interface IXPlatClipboard
{
    /// <summary>Returns the current clipboard text, or null when the clipboard holds no text.</summary>
    string? GetText();

    /// <summary>Replaces the clipboard contents. A null or empty value clears it.</summary>
    void SetText(string? text);

    /// <summary>
    /// Returns the HTML fragment on the clipboard, or null when it holds none. Implementations
    /// that only carry text inherit the null default.
    /// </summary>
    string? GetHtml() => null;

    /// <summary>
    /// Replaces the clipboard with <paramref name="text"/> plus, when
    /// <paramref name="html"/> is non-empty, an HTML flavour of the same content so rich
    /// targets paste the formatting. Implementations without HTML support fall back to
    /// <see cref="SetText"/>.
    /// </summary>
    void SetHtml(string? text, string? html) => SetText(text);
}

/// <summary>Process-wide access point for the text clipboard.</summary>
public static class XPlatClipboard
{
    private static IXPlatClipboard _current = new InMemoryClipboard();

    /// <summary>
    /// The clipboard implementation in use. Adapters assign a platform implementation;
    /// assigning null restores the in-memory fallback.
    /// </summary>
    public static IXPlatClipboard Current
    {
        get => _current;
        set => _current = value ?? new InMemoryClipboard();
    }

    public static string? GetText() => _current.GetText();

    public static void SetText(string? text) => _current.SetText(text);

    /// <summary>Returns the HTML fragment on the clipboard, or null when there is none.</summary>
    public static string? GetHtml() => _current.GetHtml();

    /// <summary>
    /// Replaces the clipboard with plain text and, when given, an HTML flavour of it. Controls
    /// that can describe their formatting richer than text use this so a copy carries both.
    /// </summary>
    public static void SetHtml(string? text, string? html) => _current.SetHtml(text, html);

    /// <summary>Fallback used when no platform clipboard is installed.</summary>
    private sealed class InMemoryClipboard : IXPlatClipboard
    {
        private string? _text;
        private string? _html;

        public string? GetText() => _text;

        public void SetText(string? text)
        {
            _text = string.IsNullOrEmpty(text) ? null : text;
            _html = null;
        }

        public string? GetHtml() => _html;

        public void SetHtml(string? text, string? html)
        {
            SetText(text);
            _html = string.IsNullOrEmpty(html) ? null : html;
        }
    }
}