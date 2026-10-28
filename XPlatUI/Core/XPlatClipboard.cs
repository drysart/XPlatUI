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

    /// <summary>Fallback used when no platform clipboard is installed.</summary>
    private sealed class InMemoryClipboard : IXPlatClipboard
    {
        private string? _text;

        public string? GetText() => _text;

        public void SetText(string? text) => _text = string.IsNullOrEmpty(text) ? null : text;
    }
}