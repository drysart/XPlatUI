namespace XPlatUI;

/// <summary>
/// A keyboard accelerator: one key plus the modifiers held with it, shown next to a menu item and
/// dispatched by the window that owns the menu.
/// </summary>
/// <remarks>
/// <para>
/// A shortcut always carries at least one modifier, because a bare key belongs to the focused
/// control: Tab moves the focus and a letter types, and neither of those is an accelerator.
/// </para>
/// <para>
/// <see cref="XPlatModifiers.Control"/> means the platform's accelerator modifier rather than the
/// literal Control key. <see cref="DisplayText"/> spells it "Cmd" on macOS and "Ctrl" everywhere
/// else, and the macOS backend gives a menu item a Command key equivalent, which is what actually
/// fires there. The macOS input pipeline never reports Command as a modifier for the same reason,
/// so <see cref="Matches"/> stays an exact comparison and no key is claimed twice.
/// </para>
/// </remarks>
public readonly struct XPlatShortcut : IEquatable<XPlatShortcut>
{
    /// <summary>
    /// Creates a shortcut for <paramref name="key"/>, with Control - the platform's accelerator
    /// modifier - held unless <paramref name="modifiers"/> says otherwise.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="key"/> is <see cref="XPlatKey.None"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="modifiers"/> is <see cref="XPlatModifiers.None"/>.</exception>
    public XPlatShortcut(XPlatKey key, XPlatModifiers modifiers = XPlatModifiers.Control)
    {
        if (key == XPlatKey.None)
        {
            throw new ArgumentOutOfRangeException(nameof(key), key, "A shortcut needs a key.");
        }

        if (modifiers == XPlatModifiers.None)
        {
            throw new ArgumentException(
                "A shortcut needs at least one modifier; a bare key belongs to the focused control.",
                nameof(modifiers));
        }

        Key = key;
        Modifiers = modifiers;
    }

    /// <summary>The key that triggers the shortcut.</summary>
    public XPlatKey Key { get; }

    /// <summary>The modifiers that have to be held with <see cref="Key"/>.</summary>
    public XPlatModifiers Modifiers { get; }

    /// <summary>True when <paramref name="key"/> pressed with <paramref name="modifiers"/> is this shortcut.</summary>
    public bool Matches(XPlatKey key, XPlatModifiers modifiers) => key == Key && modifiers == Modifiers;

    /// <summary>
    /// The shortcut as the user should see it, e.g. "Ctrl+Shift+F", or "Cmd+Shift+F" on macOS.
    /// </summary>
    public string DisplayText
    {
        get
        {
            var mac = OperatingSystem.IsMacOS();
            var parts = new List<string>(4);

            if ((Modifiers & XPlatModifiers.Control) != 0)
            {
                parts.Add(mac ? "Cmd" : "Ctrl");
            }

            if ((Modifiers & XPlatModifiers.Shift) != 0)
            {
                parts.Add("Shift");
            }

            if ((Modifiers & XPlatModifiers.Alt) != 0)
            {
                parts.Add(mac ? "Option" : "Alt");
            }

            parts.Add(KeyName(Key));
            return string.Join('+', parts);
        }
    }

    public bool Equals(XPlatShortcut other) => Key == other.Key && Modifiers == other.Modifiers;

    public override bool Equals(object? obj) => obj is XPlatShortcut other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Key, Modifiers);

    public static bool operator ==(XPlatShortcut left, XPlatShortcut right) => left.Equals(right);

    public static bool operator !=(XPlatShortcut left, XPlatShortcut right) => !left.Equals(right);

    /// <summary>The same text <see cref="DisplayText"/> returns.</summary>
    public override string ToString() => DisplayText;

    /// <summary>The name a shortcut hint uses for <paramref name="key"/>.</summary>
    private static string KeyName(XPlatKey key) => key switch
    {
        XPlatKey.Tab => "Tab",
        XPlatKey.Enter => "Enter",
        XPlatKey.Space => "Space",
        XPlatKey.Escape => "Esc",
        XPlatKey.Backspace => "Backspace",
        XPlatKey.Delete => "Del",
        XPlatKey.Left => "Left",
        XPlatKey.Right => "Right",
        XPlatKey.Up => "Up",
        XPlatKey.Down => "Down",
        XPlatKey.Home => "Home",
        XPlatKey.End => "End",
        XPlatKey.PageUp => "Page Up",
        XPlatKey.PageDown => "Page Down",
        >= XPlatKey.D0 and <= XPlatKey.D9 => ((char)('0' + ((int)key - (int)XPlatKey.D0))).ToString(),
        _ => key.ToString(),
    };
}
