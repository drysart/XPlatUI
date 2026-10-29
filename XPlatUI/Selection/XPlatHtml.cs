using System.Text;

namespace XPlatUI;

/// <summary>
/// Helpers for the HTML flavour of the clipboard, which is how a control says something richer
/// about its slice of a selection than plain text can.
/// </summary>
internal static class XPlatHtml
{
    /// <summary>
    /// Escapes <paramref name="text"/> so it can be dropped into an HTML fragment, turning line
    /// breaks into <c>&lt;br&gt;</c> so a multi-line selection survives the trip.
    /// </summary>
    public static string Escape(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(text.Length + 16);
        foreach (var character in text)
        {
            switch (character)
            {
                case '&':
                    builder.Append("&amp;");
                    break;
                case '<':
                    builder.Append("&lt;");
                    break;
                case '>':
                    builder.Append("&gt;");
                    break;
                case '"':
                    builder.Append("&quot;");
                    break;
                case '\r':
                    // The \n that follows it carries the break, so dropping this one avoids two.
                    break;
                case '\n':
                    builder.Append("<br>");
                    break;
                default:
                    builder.Append(character);
                    break;
            }
        }

        return builder.ToString();
    }
}
