using XPlatUI;

namespace GtkHarness;

/// <summary>
/// Translates GDK key values into the library's <see cref="XPlatKey"/> values. GDK key values are
/// stable across platforms (they descend from X11), so the mapping is a plain switch.
/// </summary>
internal static class GtkKeyMap
{
    private const uint KeyBackspace = 0xFF08;
    private const uint KeyTab = 0xFF09;
    private const uint KeyReturn = 0xFF0D;
    private const uint KeyEscape = 0xFF1B;
    private const uint KeySpace = 0x0020;
    private const uint KeyHome = 0xFF50;
    private const uint KeyLeft = 0xFF51;
    private const uint KeyUp = 0xFF52;
    private const uint KeyRight = 0xFF53;
    private const uint KeyDown = 0xFF54;
    private const uint KeyPageUp = 0xFF55;
    private const uint KeyPageDown = 0xFF56;
    private const uint KeyEnd = 0xFF57;
    private const uint KeyKpEnter = 0xFF8D;
    private const uint KeyDelete = 0xFFFF;

    public static XPlatKey Map(uint keyval)
    {
        if (keyval is >= 'a' and <= 'z')
        {
            return XPlatKey.A + (int)(keyval - 'a');
        }

        if (keyval is >= 'A' and <= 'Z')
        {
            return XPlatKey.A + (int)(keyval - 'A');
        }

        if (keyval is >= '0' and <= '9')
        {
            return XPlatKey.D0 + (int)(keyval - '0');
        }

        return keyval switch
        {
            KeyTab => XPlatKey.Tab,
            KeyReturn or KeyKpEnter => XPlatKey.Enter,
            KeySpace => XPlatKey.Space,
            KeyEscape => XPlatKey.Escape,
            KeyBackspace => XPlatKey.Backspace,
            KeyDelete => XPlatKey.Delete,
            KeyLeft => XPlatKey.Left,
            KeyRight => XPlatKey.Right,
            KeyUp => XPlatKey.Up,
            KeyDown => XPlatKey.Down,
            KeyHome => XPlatKey.Home,
            KeyEnd => XPlatKey.End,
            KeyPageUp => XPlatKey.PageUp,
            KeyPageDown => XPlatKey.PageDown,
            _ => XPlatKey.None,
        };
    }
}