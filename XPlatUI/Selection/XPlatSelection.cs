using System.Text;

namespace XPlatUI;

/// <summary>
/// The selection of an <see cref="XPlatUiHost"/>. A selection is a pair of
/// <see cref="XPlatSelectionPosition"/> values - an anchor (where the press happened) and a
/// focus (where the pointer is now) - which is normalised into one
/// <see cref="XPlatSelectionRange"/> per control it touches. Because the ranges are derived
/// from a depth-first walk of the control tree, a single selection can start inside one
/// control, cover whole controls in between and end inside another.
/// </summary>
public sealed class XPlatSelection
{
    private readonly XPlatUiHost _host;
    private readonly List<XPlatControl> _order = new();
    private readonly List<XPlatSelectionRange> _ranges = new();
    private XPlatSelectionPosition _anchor;
    private XPlatSelectionPosition _focus;

    internal XPlatSelection(XPlatUiHost host) => _host = host;

    /// <summary>Raised after the selection changed. The host repaints before raising it.</summary>
    public event EventHandler? Changed;

    /// <summary>Where the drag started.</summary>
    public XPlatSelectionPosition Anchor => _anchor;

    /// <summary>Where the pointer is now; the end that moves during a drag.</summary>
    public XPlatSelectionPosition Focus => _focus;

    /// <summary>True when no control is selected.</summary>
    public bool IsEmpty => _ranges.Count == 0;

    /// <summary>The selected slice of every control the selection touches, in visual order.</summary>
    public IReadOnlyList<XPlatSelectionRange> Ranges => _ranges;

    /// <summary>Returns the selected slice of <paramref name="control"/>, or an empty range.</summary>
    public XPlatSelectionRange RangeOf(XPlatControl control)
    {
        ArgumentNullException.ThrowIfNull(control);

        for (int i = 0; i < _ranges.Count; i++)
        {
            if (ReferenceEquals(_ranges[i].Control, control))
            {
                return _ranges[i];
            }
        }

        return default;
    }

    /// <summary>True when at least part of <paramref name="control"/> is selected.</summary>
    public bool Contains(XPlatControl control) => !RangeOf(control).IsEmpty;

    /// <summary>
    /// The text the selection contributes to the clipboard: each control's own contribution,
    /// in visual order, joined with <paramref name="separator"/>.
    /// </summary>
    public string GetText(string separator = "\n")
    {
        if (_ranges.Count == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        foreach (var range in _ranges)
        {
            var piece = range.Control!.GetSelectionText(range.Start, range.Length);
            if (piece.Length == 0)
            {
                continue;
            }

            if (builder.Length > 0)
            {
                builder.Append(separator);
            }

            builder.Append(piece);
        }

        return builder.ToString();
    }

    /// <summary>
    /// The selection as an HTML fragment: each control's own markup for its slice, in visual
    /// order, joined with <paramref name="separator"/>. Controls that have no markup of their
    /// own contribute their text, escaped.
    /// </summary>
    public string GetHtml(string separator = "\n")
    {
        if (_ranges.Count == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        foreach (var range in _ranges)
        {
            var piece = range.Control!.GetSelectionHtml(range.Start, range.Length);
            if (string.IsNullOrEmpty(piece))
            {
                piece = XPlatHtml.Escape(range.Control.GetSelectionText(range.Start, range.Length));
            }

            if (piece.Length == 0)
            {
                continue;
            }

            if (builder.Length > 0)
            {
                builder.Append(XPlatHtml.Escape(separator));
            }

            builder.Append(piece);
        }

        return builder.ToString();
    }

    /// <summary>Clears the selection.</summary>
    public void Clear()
    {
        if (_anchor.IsEmpty && _focus.IsEmpty && _ranges.Count == 0)
        {
            return;
        }

        _anchor = default;
        _focus = default;
        _ranges.Clear();
        NotifyChanged();
    }

    /// <summary>Starts a selection at <paramref name="anchor"/>.</summary>
    internal void Begin(XPlatSelectionPosition anchor) => Begin(anchor, anchor);

    /// <summary>Starts a selection that already spans <paramref name="anchor"/> to <paramref name="focus"/>.</summary>
    internal void Begin(XPlatSelectionPosition anchor, XPlatSelectionPosition focus)
    {
        _anchor = anchor;
        _focus = focus;
        Rebuild();
    }

    /// <summary>Moves the moving end of the selection.</summary>
    internal void SetFocus(XPlatSelectionPosition focus)
    {
        if (_focus.Equals(focus))
        {
            return;
        }

        _focus = focus;
        Rebuild();
    }

    /// <summary>
    /// Recomputes the ranges after a control's content changed, so offsets never point past
    /// the end of the content.
    /// </summary>
    internal void Refresh()
    {
        if (!_anchor.IsEmpty || !_focus.IsEmpty)
        {
            Rebuild();
        }
    }

    private void Rebuild()
    {
        _ranges.Clear();

        if (_anchor.IsEmpty || _focus.IsEmpty)
        {
            NotifyChanged();
            return;
        }

        BuildOrder(_host.Root);

        float start = ToAbsolute(_anchor);
        float end = ToAbsolute(_focus);
        if (start > end)
        {
            (start, end) = (end, start);
        }

        float baseOffset = 0f;
        foreach (var control in _order)
        {
            int units = control.SelectionUnitCount;

            if (control.SelectionStyle == XPlatSelectionStyle.Whole)
            {
                // A whole control is in or out as a whole, and it joins the selection as soon as
                // the selection reaches into it from either side. Touching an edge is not enough,
                // so a drag that stops exactly on the boundary leaves the control unselected.
                if (start < baseOffset + units && end > baseOffset)
                {
                    _ranges.Add(new XPlatSelectionRange(control, 0, units));
                }
            }
            else
            {
                // A unit is included once the selection covers its middle, so the ends snap to the
                // nearest unit boundary and a press that does not move selects nothing.
                int from = (int)MathF.Round(MathF.Max(start, baseOffset) - baseOffset, MidpointRounding.AwayFromZero);
                int to = (int)MathF.Round(MathF.Min(end, baseOffset + units) - baseOffset, MidpointRounding.AwayFromZero);
                if (to > from)
                {
                    _ranges.Add(new XPlatSelectionRange(control, from, to - from));
                }
            }

            baseOffset += units;
        }

        NotifyChanged();
    }

    /// <summary>Collects the selectable controls in visual order (depth first, z-order aware).</summary>
    private void BuildOrder(XPlatControl? control)
    {
        _order.Clear();
        CollectOrder(control);
    }

    private void CollectOrder(XPlatControl? control)
    {
        if (control is null || !control.Visible || !control.Enabled)
        {
            return;
        }

        if (control.SelectionUnitCount > 0)
        {
            // A selectable control is selected as a unit, so its children are not separate
            // participants - that also keeps ranges from overlapping.
            _order.Add(control);
            return;
        }

        if (!control.HasChildren)
        {
            return;
        }

        foreach (var child in control.Controls)
        {
            CollectOrder(child);
        }
    }

    private float ToAbsolute(XPlatSelectionPosition position)
    {
        float offset = 0f;
        foreach (var control in _order)
        {
            if (ReferenceEquals(control, position.Control))
            {
                return offset + Math.Clamp(position.Offset, 0f, control.SelectionUnitCount);
            }

            offset += control.SelectionUnitCount;
        }

        // The control is no longer part of the tree (or is not selectable); clamp to the end.
        return offset;
    }

    private void NotifyChanged()
    {
        _host.Invalidate();
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
