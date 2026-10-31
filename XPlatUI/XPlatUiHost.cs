using SkiaSharp;

namespace XPlatUI;

/// <summary>
/// The platform-independent heart of the UI library. A host adapter (WinForms, WPF,
/// Win32, Cocoa, X11, SDL, ...) owns an <see cref="XPlatUiHost"/>, forwards input and
/// calls <see cref="Paint"/> whenever <see cref="Invalidated"/> fires.
/// </summary>
public sealed class XPlatUiHost
{
    private readonly XPlatSelection _selection;
    private XPlatControl? _root;
    private SKSize _clientSize;
    private XPlatControl? _hovered;
    private XPlatControl? _captured;
    private XPlatControl? _focused;
    private XPlatControl? _selectionOwner;
    private XPlatCursor _cursor = XPlatCursor.Default;
    private SKPoint _selectionOrigin;
    private bool _selecting;
    private bool _draggingSelection;
    private SKColor? _clearColor;

    public XPlatUiHost() => _selection = new XPlatSelection(this);

    /// <summary>Raised when the UI needs to be repainted.</summary>
    public event EventHandler? Invalidated;

    /// <summary>Raised when the control under the pointer requests a different cursor.</summary>
    public event EventHandler? CursorChanged;

    /// <summary>
    /// Raised before a context menu is shown, so an application can supply one for a control
    /// that carries no <see cref="XPlatControl.ContextMenu"/> of its own.
    /// </summary>
    public event EventHandler<XPlatContextMenuEventArgs>? ContextMenuOpening;

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
            _selecting = false;
            _draggingSelection = false;
            ClearSelection();

            _root?.AttachHost(null);
            _root = value;
            _root?.AttachHost(this);

            PerformLayout();
            Invalidate();
        }
    }

    /// <summary>
    /// The window-scoped service provider, assigned by the <see cref="XPlatWindow"/> that owns this
    /// host. Null when a host is used standalone, as the harnesses do.
    /// </summary>
    public IServiceProvider? Services { get; set; }

    /// <summary>Resolves <typeparamref name="TService"/> from <see cref="Services"/>, or null when there is none.</summary>
    /// <typeparam name="TService">The service to resolve.</typeparam>
    public TService? GetService<TService>() where TService : class =>
        Services?.GetService(typeof(TService)) as TService;

    /// <summary>Size of the drawable area in logical pixels (96 DPI).</summary>
    public SKSize ClientSize => _clientSize;

    /// <summary>
    /// Color used to clear the surface before painting. While it has never been assigned it
    /// follows <see cref="XPlatTheme.Background"/>, so the window follows the theme on its own.
    /// </summary>
    public SKColor ClearColor
    {
        get => _clearColor ?? XPlatTheme.Background;
        set => _clearColor = value;
    }

    public XPlatControl? FocusedControl => _focused;

    /// <summary>
    /// The control that currently owns a selection of its own, such as a text box with a caret,
    /// or null when the host's own <see cref="Selection"/> is the only selection.
    /// </summary>
    public XPlatControl? SelectionOwner => _selectionOwner;

    public XPlatControl? HoveredControl => _hovered;

    public XPlatControl? CapturedControl => _captured;

    public XPlatCursor Cursor => _cursor;

    /// <summary>
    /// The selection driven by pointer input. It can span several controls, so it lives here
    /// rather than in a control.
    /// </summary>
    public XPlatSelection Selection => _selection;

    /// <summary>
    /// Text inserted between the pieces that different controls contribute when a selection is
    /// copied. Defaults to a newline, so a selection spanning three labels copies as three lines.
    /// </summary>
    public string SelectionTextSeparator { get; set; } = "\n";

    /// <summary>
    /// Distance in logical pixels the pointer must travel after a press before the press turns
    /// into a selection. Keeps plain clicks reaching links, buttons and text boxes.
    /// </summary>
    public float SelectionDragThreshold { get; set; } = 3f;

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

    /// <summary>
    /// Re-reads the platform's light/dark preference through <see cref="XPlatSystemTheme"/> and, when
    /// it no longer matches the palette, switches the palette and repaints. Backends call this once
    /// at startup and again from their theme-changed notification (<c>WM_SETTINGCHANGE</c> and
    /// <c>WM_THEMECHANGED</c> on Windows).
    /// </summary>
    /// <returns>True when the appearance changed.</returns>
    public bool RefreshTheme()
    {
        if (!XPlatSystemTheme.Refresh())
        {
            return false;
        }

        Invalidate();
        return true;
    }

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

        if (_selecting && _selectionOwner is null)
        {
            if (!_draggingSelection)
            {
                var dx = location.X - _selectionOrigin.X;
                var dy = location.Y - _selectionOrigin.Y;
                _draggingSelection = (dx * dx) + (dy * dy) >= SelectionDragThreshold * SelectionDragThreshold;
            }

            if (_draggingSelection)
            {
                ExtendSelection(location);
            }
        }
    }

    public void ProcessMouseDown(
        SKPoint location,
        XPlatMouseButtons button = XPlatMouseButtons.Left,
        XPlatModifiers modifiers = XPlatModifiers.None,
        int clickCount = 1)
    {
        var hit = HitTest(location);
        _captured = hit;

        if (button == XPlatMouseButtons.Left)
        {
            var selectionTarget = FindSelectionTarget(hit);
            if (selectionTarget is null)
            {
                _selecting = false;
                _draggingSelection = false;
                ClearSelection();
            }
            else
            {
                _selectionOrigin = location;
                _selecting = true;
                _draggingSelection = false;
                BeginSelection(selectionTarget, location);
            }
        }

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
        if (button == XPlatMouseButtons.Left)
        {
            _selecting = false;
            _draggingSelection = false;
        }

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
    /// Shows the context menu for <paramref name="location"/>. Adapters call this from the
    /// platform's own context menu event (on Windows, <c>WM_CONTEXTMENU</c>), which is why the
    /// host does not raise it from a right button release: the platform message already covers
    /// both the mouse and the keyboard menu key. Returns true when a menu was handed to a
    /// presenter.
    /// </summary>
    /// <remarks>
    /// The menu comes from the hit control's <see cref="XPlatControl.ContextMenu"/> (or the
    /// nearest ancestor that has one), then from <see cref="ContextMenuOpening"/>, and finally
    /// from the current selection, which offers Copy. Right-clicking never changes the
    /// selection, so a menu can act on what the user already selected.
    /// </remarks>
    public bool ProcessContextMenu(SKPoint location)
    {
        var hit = HitTest(location);
        var menu = hit?.BuildContextMenu();

        if (menu is null || menu.IsEmpty)
        {
            var args = new XPlatContextMenuEventArgs(hit, location);
            ContextMenuOpening?.Invoke(this, args);
            menu = args.Menu;
        }

        if (menu is null || menu.IsEmpty)
        {
            menu = _selection.IsEmpty ? null : XPlatContextMenu.CreateCopyMenu(this);
        }

        return menu is not null && menu.Show(this, hit, location);
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

        var previous = _focused;
        previous?.RaiseLostFocus();

        // A control's internal selection only lives while it holds focus, so leaving the
        // control ends it - that keeps a caret from lingering next to another selection.
        if (previous is not null && ReferenceEquals(_selectionOwner, previous))
        {
            ReleaseSelectionOwner();
        }

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

    /// <summary>
    /// Declares that <paramref name="owner"/> is starting a selection of its own - a text box
    /// caret or selection, for example. Only one selection may exist at a time, so the host's
    /// control-level selection is cleared and any other control's internal selection is dropped
    /// before the caller proceeds.
    /// </summary>
    public void ClaimSelection(XPlatControl owner)
    {
        ArgumentNullException.ThrowIfNull(owner);

        var previous = _selectionOwner;
        _selectionOwner = owner;
        _selecting = false;
        _draggingSelection = false;

        bool changed = !_selection.IsEmpty;
        _selection.Clear();

        if (!ReferenceEquals(previous, owner))
        {
            changed = true;
            previous?.RaiseSelectionCleared();
        }

        if (changed)
        {
            Invalidate();
        }
    }

    /// <summary>Clears every selection: the control-level selection and any control's internal one.</summary>
    public void ClearSelection()
    {
        _selection.Clear();
        ReleaseSelectionOwner();
    }

    /// <summary>
    /// Copies the current selection to the clipboard. Every control the selection touches
    /// contributes its own piece of the text, in visual order, joined with
    /// <see cref="SelectionTextSeparator"/>. Controls that can describe their formatting also
    /// contribute an HTML flavour, so rich targets paste the formatting.
    /// Returns true when text was copied.
    /// </summary>
    public bool CopySelection()
    {
        var text = _selection.GetText(SelectionTextSeparator);
        if (text.Length == 0)
        {
            return false;
        }

        XPlatClipboard.SetHtml(text, _selection.GetHtml(SelectionTextSeparator));
        return true;
    }

    private void ReleaseSelectionOwner()
    {
        var previous = _selectionOwner;
        if (previous is null)
        {
            return;
        }

        _selectionOwner = null;
        previous.RaiseSelectionCleared();
    }

    private XPlatControl? HitTest(SKPoint location) => _root?.HitTest(location);

    private void BeginSelection(XPlatControl control, SKPoint location)
    {
        ReleaseSelectionOwner();
        _selection.Begin(new XPlatSelectionPosition(control, control.GetSelectionOffset(ToLocal(control, location))));
    }

    private void ExtendSelection(SKPoint location)
    {
        var position = ResolveSelectionPosition(location);
        if (position.IsEmpty)
        {
            return;
        }

        _selection.SetFocus(position);
    }

    /// <summary>
    /// Resolves a pointer location to a selection position. A location over a selectable control
    /// maps straight into that control; a location outside every selectable control - the gap
    /// between two controls, the padding of a panel, or beyond the surface - clamps to the
    /// nearest one, so dragging off the end of the content selects up to that end instead of
    /// freezing wherever the pointer last happened to be over a control.
    /// </summary>
    private XPlatSelectionPosition ResolveSelectionPosition(SKPoint location)
    {
        var target = FindSelectionTarget(HitTest(location));
        if (target is not null)
        {
            return new XPlatSelectionPosition(target, target.GetSelectionOffset(ToLocal(target, location)));
        }

        var nearest = FindNearestSelectable(location) ?? _selection.Focus.Control ?? _selection.Anchor.Control;
        return nearest is null
            ? default
            : new XPlatSelectionPosition(nearest, nearest.GetSelectionOffset(ToLocal(nearest, location)));
    }

    /// <summary>
    /// The selectable control whose client area is closest to <paramref name="location"/>. Ties
    /// keep the control that currently holds the moving end of the selection, so a drag along an
    /// edge between two neighbours does not flicker between them.
    /// </summary>
    private XPlatControl? FindNearestSelectable(SKPoint location)
    {
        var current = _selection.Focus.Control;
        XPlatControl? best = current is { } control && control.SelectionUnitCount > 0 ? control : null;
        float bestDistance = best is null
            ? float.MaxValue
            : DistanceToRect(location, ToHost(best, best.ClientRect));

        CollectNearestSelectable(_root, location, ref best, ref bestDistance);
        return best;
    }

    private static void CollectNearestSelectable(
        XPlatControl? control,
        SKPoint location,
        ref XPlatControl? best,
        ref float bestDistance)
    {
        if (control is null || !control.Visible || !control.Enabled)
        {
            return;
        }

        if (control.SelectionUnitCount > 0)
        {
            // Matches the selection's own walk: a selectable control hides its children.
            float distance = DistanceToRect(location, ToHost(control, control.ClientRect));
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = control;
            }

            return;
        }

        if (!control.HasChildren)
        {
            return;
        }

        foreach (var child in control.Controls)
        {
            CollectNearestSelectable(child, location, ref best, ref bestDistance);
        }
    }

    private static float DistanceToRect(SKPoint point, SKRect rect)
    {
        float dx = point.X < rect.Left ? rect.Left - point.X : point.X > rect.Right ? point.X - rect.Right : 0f;
        float dy = point.Y < rect.Top ? rect.Top - point.Y : point.Y > rect.Bottom ? point.Y - rect.Bottom : 0f;
        return MathF.Sqrt((dx * dx) + (dy * dy));
    }

    /// <summary>Maps a point from a control's coordinate space to the host's.</summary>
    private static SKPoint ToHost(XPlatControl control, SKPoint point)
    {
        for (var current = control; current is not null; current = current.Parent)
        {
            point = new SKPoint(point.X + current.Left, point.Y + current.Top);
        }

        return point;
    }

    /// <summary>Maps a rectangle from a control's coordinate space to the host's.</summary>
    private static SKRect ToHost(XPlatControl control, SKRect rect)
    {
        var origin = ToHost(control, SKPoint.Empty);
        return new SKRect(
            rect.Left + origin.X,
            rect.Top + origin.Y,
            rect.Right + origin.X,
            rect.Bottom + origin.Y);
    }

    /// <summary>
    /// Returns the control that should own a selection position for <paramref name="control"/>:
    /// the control itself when it is selectable, otherwise its nearest selectable ancestor (so
    /// clicking a link or a chip inside a selectable label selects the label).
    /// </summary>
    private static XPlatControl? FindSelectionTarget(XPlatControl? control)
    {
        for (var current = control; current is not null; current = current.Parent)
        {
            if (current.SelectionUnitCount > 0)
            {
                return current;
            }
        }

        return null;
    }

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
