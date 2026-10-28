using SkiaSharp;

namespace XPlatUI;

public class XPlatMouseEventArgs : EventArgs
{
    public XPlatMouseEventArgs(
        SKPoint location,
        XPlatMouseButtons button = XPlatMouseButtons.None,
        XPlatModifiers modifiers = XPlatModifiers.None,
        int clickCount = 1)
    {
        Location = location;
        Button = button;
        Modifiers = modifiers;
        ClickCount = clickCount;
    }

    /// <summary>Mouse position in the receiving control's coordinate space.</summary>
    public SKPoint Location { get; }

    public XPlatMouseButtons Button { get; }

    public XPlatModifiers Modifiers { get; }

    public int ClickCount { get; }

    public bool Handled { get; set; }
}

public class XPlatKeyEventArgs : EventArgs
{
    public XPlatKeyEventArgs(XPlatKey key, XPlatModifiers modifiers = XPlatModifiers.None)
    {
        Key = key;
        Modifiers = modifiers;
    }

    public XPlatKey Key { get; }

    public XPlatModifiers Modifiers { get; }

    public bool Handled { get; set; }
}

public class XPlatTextInputEventArgs : EventArgs
{
    public XPlatTextInputEventArgs(string text) => Text = text;

    public string Text { get; }

    public bool Handled { get; set; }
}
