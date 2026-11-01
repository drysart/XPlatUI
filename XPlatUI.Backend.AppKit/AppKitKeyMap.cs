using AppKit;

namespace XPlatUI.Backend.AppKit;

/// <summary>
/// Translates between AppKit and the platform independent input types the UI library understands.
/// </summary>
internal static class AppKitKeyMap
{
    /// <summary>
    /// Maps a macOS virtual key code to an <see cref="XPlatKey"/>. The numbers are hardware key
    /// codes from Carbon's Events.h, not character codes, so this is a lookup table rather than
    /// anything derived from the event's characters.
    /// </summary>
    public static XPlatKey ToKey(ushort keyCode) => keyCode switch
    {
        0x00 => XPlatKey.A,
        0x01 => XPlatKey.S,
        0x02 => XPlatKey.D,
        0x03 => XPlatKey.F,
        0x04 => XPlatKey.H,
        0x05 => XPlatKey.G,
        0x06 => XPlatKey.Z,
        0x07 => XPlatKey.X,
        0x08 => XPlatKey.C,
        0x09 => XPlatKey.V,
        0x0B => XPlatKey.B,
        0x0C => XPlatKey.Q,
        0x0D => XPlatKey.W,
        0x0E => XPlatKey.E,
        0x0F => XPlatKey.R,
        0x10 => XPlatKey.Y,
        0x11 => XPlatKey.T,
        0x12 => XPlatKey.D1,
        0x13 => XPlatKey.D2,
        0x14 => XPlatKey.D3,
        0x15 => XPlatKey.D4,
        0x16 => XPlatKey.D6,
        0x17 => XPlatKey.D5,
        0x18 => XPlatKey.None,   // Equal
        0x19 => XPlatKey.D9,
        0x1A => XPlatKey.D7,
        0x1B => XPlatKey.None,   // Minus
        0x1C => XPlatKey.D8,
        0x1D => XPlatKey.D0,
        0x1F => XPlatKey.O,
        0x20 => XPlatKey.U,
        0x21 => XPlatKey.None,   // Left Bracket
        0x22 => XPlatKey.I,
        0x23 => XPlatKey.P,
        0x24 => XPlatKey.Enter,
        0x25 => XPlatKey.L,
        0x26 => XPlatKey.J,
        0x27 => XPlatKey.None,   // Quote
        0x28 => XPlatKey.K,
        0x29 => XPlatKey.None,   // Semicolon
        0x2A => XPlatKey.None,   // Backslash
        0x2B => XPlatKey.None,   // Comma
        0x2C => XPlatKey.None,   // Slash
        0x2D => XPlatKey.N,
        0x2E => XPlatKey.M,
        0x2F => XPlatKey.None,   // Period
        0x30 => XPlatKey.Tab,
        0x31 => XPlatKey.Space,
        0x32 => XPlatKey.None,   // Backquote
        0x33 => XPlatKey.Backspace,
        0x35 => XPlatKey.Escape,
        0x37 => XPlatKey.None,   // Command: a modifier, reported through FlagsChanged.
        0x38 => XPlatKey.None,   // Shift
        0x39 => XPlatKey.None,   // Caps Lock
        0x3A => XPlatKey.None,   // Option
        0x3B => XPlatKey.None,   // Control
        0x3C => XPlatKey.None,   // Right Shift
        0x3D => XPlatKey.None,   // Right Option
        0x3E => XPlatKey.None,   // Right Control
        0x3F => XPlatKey.None,   // Function
        0x40 => XPlatKey.None,   // F17
        0x4C => XPlatKey.Enter,  // Keypad Enter
        0x4E => XPlatKey.None,   // Keypad Minus
        0x4F => XPlatKey.None,   // F18
        0x50 => XPlatKey.None,   // F19
        0x51 => XPlatKey.None,   // Keypad Equal
        0x52 => XPlatKey.D0,     // Keypad 0
        0x53 => XPlatKey.D1,
        0x54 => XPlatKey.D2,
        0x55 => XPlatKey.D3,
        0x56 => XPlatKey.D4,
        0x57 => XPlatKey.D5,
        0x58 => XPlatKey.D6,
        0x59 => XPlatKey.D7,
        0x5A => XPlatKey.None,   // F20
        0x5B => XPlatKey.D8,
        0x5C => XPlatKey.D9,
        0x60 => XPlatKey.None,   // F5
        0x61 => XPlatKey.None,   // F6
        0x62 => XPlatKey.None,   // F7
        0x63 => XPlatKey.None,   // F3
        0x64 => XPlatKey.None,   // F8
        0x65 => XPlatKey.None,   // F9
        0x67 => XPlatKey.None,   // F11
        0x69 => XPlatKey.None,   // F13
        0x6A => XPlatKey.None,   // F16
        0x6B => XPlatKey.None,   // F14
        0x6D => XPlatKey.None,   // F10
        0x6F => XPlatKey.None,   // F12
        0x71 => XPlatKey.None,   // F15
        0x73 => XPlatKey.Home,
        0x74 => XPlatKey.PageUp,
        0x75 => XPlatKey.Delete, // Forward Delete
        0x76 => XPlatKey.None,   // F4
        0x77 => XPlatKey.End,
        0x78 => XPlatKey.None,   // F2
        0x79 => XPlatKey.PageDown,
        0x7A => XPlatKey.None,   // F1
        0x7B => XPlatKey.Left,
        0x7C => XPlatKey.Right,
        0x7D => XPlatKey.Down,
        0x7E => XPlatKey.Up,
        _ => XPlatKey.None,
    };

    /// <summary>
    /// The modifier keys held during an event. Command is deliberately not reported: it drives the
    /// menu bar's key equivalents rather than the UI's own Ctrl-style shortcuts, so mapping it onto
    /// <see cref="XPlatModifiers.Control"/> would fire shortcuts the user did not ask for.
    /// </summary>
    public static XPlatModifiers ToModifiers(NSEventModifierMask flags)
    {
        var modifiers = XPlatModifiers.None;
        if ((flags & NSEventModifierMask.ShiftKeyMask) != 0)
        {
            modifiers |= XPlatModifiers.Shift;
        }

        if ((flags & NSEventModifierMask.ControlKeyMask) != 0)
        {
            modifiers |= XPlatModifiers.Control;
        }

        if ((flags & NSEventModifierMask.AlternateKeyMask) != 0)
        {
            modifiers |= XPlatModifiers.Alt;
        }

        return modifiers;
    }

    /// <summary>
    /// The text an event inserts. Modifier-only keys and the non-text keys (arrows, Escape, Return,
    /// function keys) arrive as control characters or private-use code points; those are dropped
    /// because the UI library handles them through <see cref="XPlatKey"/> instead.
    /// </summary>
    public static string ToText(NSEvent evt)
    {
        // The characters already carry the effect of Shift, the keyboard layout and Option dead
        // keys (Option+E then E gives an accented letter), so they are used as they arrive.
        var characters = evt.Characters;

        if (string.IsNullOrEmpty(characters))
        {
            return string.Empty;
        }

        return ContainsOnlyText(characters) ? characters : string.Empty;
    }

    private static bool ContainsOnlyText(string characters)
    {
        foreach (var c in characters)
        {
            // C0 controls, Delete, and the function-key range (NSUpArrowFunctionKey and friends,
            // which start at U+F700) never represent typed text.
            if (c < 0x20 || c == 0x7F || (c >= 0xF700 && c <= 0xF8FF))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The key equivalent character for a shortcut key, or null for a key AppKit cannot spell that
    /// way. AppKit describes a menu shortcut as a character rather than a key code, which is why
    /// the arrows and the other non-text keys use the function-key code points.
    /// </summary>
    public static string? ToKeyEquivalent(XPlatKey key) => key switch
    {
        >= XPlatKey.A and <= XPlatKey.Z => ((char)('a' + ((int)key - (int)XPlatKey.A))).ToString(),
        >= XPlatKey.D0 and <= XPlatKey.D9 => ((char)('0' + ((int)key - (int)XPlatKey.D0))).ToString(),
        XPlatKey.Tab => "\t",
        XPlatKey.Enter => "\r",
        XPlatKey.Escape => "\u001B",
        XPlatKey.Backspace => "\b",
        XPlatKey.Delete => "\u007F",
        XPlatKey.Left => "\uF702",
        XPlatKey.Right => "\uF703",
        XPlatKey.Up => "\uF700",
        XPlatKey.Down => "\uF701",
        XPlatKey.Home => "\uF729",
        XPlatKey.End => "\uF72B",
        XPlatKey.PageUp => "\uF72C",
        XPlatKey.PageDown => "\uF72D",
        _ => null,
    };

    /// <summary>
    /// The AppKit modifier mask for a shortcut. <see cref="XPlatModifiers.Control"/> is the
    /// library's accelerator modifier, which macOS spells Command.
    /// </summary>
    public static NSEventModifierMask ToKeyEquivalentMask(XPlatModifiers modifiers)
    {
        var mask = (NSEventModifierMask)0;

        if ((modifiers & XPlatModifiers.Control) != 0)
        {
            mask |= NSEventModifierMask.CommandKeyMask;
        }

        if ((modifiers & XPlatModifiers.Shift) != 0)
        {
            mask |= NSEventModifierMask.ShiftKeyMask;
        }

        if ((modifiers & XPlatModifiers.Alt) != 0)
        {
            mask |= NSEventModifierMask.AlternateKeyMask;
        }

        return mask;
    }
}