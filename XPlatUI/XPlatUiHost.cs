using SkiaSharp;

namespace XPlatUI;

/// <summary>
/// The platform-independent heart of the UI library. A host adapter (WinForms, WPF,
/// Win32, Cocoa, X11, SDL, ...) owns an <see cref="XPlatUiHost"/>, forwards input and
/// calls <see cref="Paint"/> whenever <see cref="Invalidated"/> fires.
/// </summary>
public sealed class XPlatUiHost
{
    private XPlatControl? _root;
    private SKSize _clientSize;
    private XPlatControl? _hovered;
    private XPlatControl? _captured;
    private XPlatControl? _focused;
    private XPlatCursor _cursor = XPlatCursor.Default;

    /// <summary>Raised when the UI needs to be repainted.</summary>
    public event EventHandler? Invalidated;

    /// <summary>Raised when the control under the pointer requests a different cursor.</summary>
    public event EventHandler? CursorChanged;

    /// <summary>The root of the control tree.</summary>
    public XPlatControl? Root
    {
        get => _root;
        set
        {
            if (ReferenceEquals(_root, value))
            {
                return;
            }

            if (_focused is not null)
            {
                SetFocus(null);
            }

            _hovered?.RaiseMouseLeave();
            _hovered = null;
            _captured = null;

            _root?.AttachHost(null);
            _root = value;
            _root?.AttachHost(this);

            PerformLayout();
            Invalidate();
        }
    }

    /// <summary>Size of the drawable area in logical pixels (96 DPI).</summary>
    public SKSize ClientSize => _clientSize;

    /// <summary>Color used to clear the surface before painting.</summary>
    public SKColor ClearColor { get; set; } = SKColors.White;

    public XPlatControl? FocusedControl => _focused;

    public XPlatControl? HoveredControl => _hovered;

    public XPlatControl? CapturedControl => _captured;

    public XPlatCursor Cursor => _cursor;

    /// <summary>Updates the logical client size and re-runs layout when it changed.</summary>
    public void SetClientSize(float width, float height)
    {
        width = MathF.Max(0f, width);
        height = MathF.Max(0f, height);

        if (MathF.Abs(width - _clientSize.Width) < 0.01f && MathF.Abs(height - _clientSize.Height) < 0.01f)
        {
            return;
        }

        _clientSize = new SKSize(width, height);
        PerformLayout();
        Invalidate();
    }

    /// <summary>Arranges the root control to fill the client area.</summary>
    public void PerformLayout()
    {
        if (_root is null)
        {
            return;
        }

        _root.Bounds = new SKRect(0f, 0f, _clientSize.Width, _clientSize.Height);
        _root.PerformLayout();
    }

    /// <summary>Clears the surface and paints the whole control tree.</summary>
    /// <param name="canvas">Canvas whose transform maps logical pixels to device pixels.</param>
    public void Paint(SKCanvas canvas)
    {
        ArgumentNullException.ThrowIfNull(canvas);

        canvas.Clear(ClearColor);
        _root?.Render(canvas);
    }

    public void Invalidate() => Invalidated?.Invoke(this, EventArgs.Empty);

    public void ProcessMouseMove(SKPoint location)
    {
        var hit = HitTest(location);

        if (!ReferenceEquals(hit, _hovered))
        {
            _hovered?.RaiseMouseLeave();
            _hovered = hit;
            _hovered?.RaiseMouseEnter();
        }

        var target = _captured ?? hit;
        target?.RaiseMouseMove(new XPlatMouseEventArgs(ToLocal(target, location)));
        SetCursor(hit is null ? XPlatCursor.Default : hit.GetCursorAt(ToLocal(hit, location)));
    }

    public void ProcessMouseDown(
        SKPoint location,
        XPlatMouseButtons button = XPlatMouseButtons.Left,
        XPlatModifiers modifiers = XPlatModifiers.None,
        int clickCount = 1)
    {
        var hit = HitTest(location);
        _captured = hit;

        if (hit is null)
        {
            SetFocus(null);
            return;
        }

        if (hit.TabStop)
        {
            SetFocus(hit);
        }

        hit.RaiseMouseDown(new XPlatMouseEventArgs(ToLocal(hit, location), button, modifiers, clickCount));
    }

    public void ProcessMouseUp(SKPoint location, XPlatMouseButtons button = XPlatMouseButtons.Left, XPlatModifiers modifiers = XPlatModifiers.None)
    {
        var target = _captured ?? HitTest(location);
        _captured = null;
        target?.RaiseMouseUp(new XPlatMouseEventArgs(ToLocal(target, location), button, modifiers));
    }

    /// <summary>Clears the hover state when the pointer leaves the surface.</summary>
    public void ProcessMouseLeave()
    {
        _hovered?.RaiseMouseLeave();
        _hovered = null;
        SetCursor(XPlatCursor.Default);
    }

    /// <summary>
    /// Routes a key press to the focused control. Returns true when the control consumed the
    /// key, which tells the adapter to swallow the platform key event (and its text input).
    /// </summary>
    public bool ProcessKeyDown(XPlatKey key, XPlatModifiers modifiers = XPlatModifiers.None)
    {
        if (key == XPlatKey.Tab)
        {
            MoveFocus((modifiers & XPlatModifiers.Shift) != 0 ? -1 : 1);
            return true;
        }

        return _focused?.RaiseKeyDown(new XPlatKeyEventArgs(key, modifiers)) ?? false;
    }

    public void ProcessKeyUp(XPlatKey key, XPlatModifiers modifiers = XPlatModifiers.None) =>
        _focused?.RaiseKeyUp(new XPlatKeyEventArgs(key, modifiers));

    public void ProcessTextInput(string text) =>
        _focused?.RaiseTextInput(new XPlatTextInputEventArgs(text));

    /// <summary>
    /// Advances time-based behaviour (such as the text caret blink) of the focused control.
    /// Adapters call this from a timer, typically every 100-250 ms.
    /// </summary>
    public void ProcessTick(TimeSpan elapsed) => _focused?.RaiseTick(elapsed);

    /// <summary>Moves keyboard focus to the given control (null clears focus).</summary>
    public void SetFocus(XPlatControl? control)
    {
        if (ReferenceEquals(_focused, control))
        {
            return;
        }

        if (control is { Enabled: false })
        {
            return;
        }

        _focused?.RaiseLostFocus();
        _focused = control;
        _focused?.RaiseGotFocus();
        Invalidate();
    }

    public void CaptureMouse(XPlatControl control)
    {
        ArgumentNullException.ThrowIfNull(control);
        _captured = control;
    }

    public void ReleaseMouseCapture(XPlatControl control)
    {
        if (ReferenceEquals(_captured, control))
        {
            _captured = null;
        }
    }

    private XPlatControl? HitTest(SKPoint location) => _root?.HitTest(location);

    private void SetCursor(XPlatCursor cursor)
    {
        if (_cursor == cursor)
        {
            return;
        }

        _cursor = cursor;
        CursorChanged?.Invoke(this, EventArgs.Empty);
    }

    private void MoveFocus(int direction)
    {
        var focusable = new List<XPlatControl>();
        CollectFocusable(_root, focusable);
        if (focusable.Count == 0)
        {
            return;
        }

        int current = _focused is null ? -1 : focusable.IndexOf(_focused);
        int next = current < 0
            ? (direction >= 0 ? 0 : focusable.Count - 1)
            : ((current + direction) % focusable.Count + focusable.Count) % focusable.Count;

        SetFocus(focusable[next]);
    }

    private static void CollectFocusable(XPlatControl? control, List<XPlatControl> output)
    {
        if (control is null || !control.Visible || !control.Enabled)
        {
            return;
        }

        if (control.TabStop)
        {
            output.Add(control);
        }

        if (control.HasChildren)
        {
            foreach (var child in control.Controls)
            {
                CollectFocusable(child, output);
            }
        }
    }

    private static SKPoint ToLocal(XPlatControl control, SKPoint point)
    {
        var local = point;
        for (var current = control; current is not null; current = current.Parent)
        {
            local = new SKPoint(local.X - current.Left, local.Y - current.Top);
        }

        return local;
    }
}
