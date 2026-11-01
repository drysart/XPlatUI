using System.Text;
using SkiaSharp;

namespace XPlatUI;

/// <summary>
/// A single-line editable text field with a blinking caret, selection, clipboard support,
/// password masking and placeholder text. Like every XPlat control it has no platform
/// dependency: it paints into an <see cref="SKCanvas"/> and receives mouse, keyboard and
/// text input from an <see cref="XPlatUiHost"/>.
/// </summary>
/// <remarks>
/// The caret is an <see cref="XPlatCaret"/>, so it looks and blinks like the caret of every other
/// editable control and only runs a timer while this text box has focus. Adapters only need to
/// install a platform clipboard into <see cref="XPlatClipboard.Current"/>.
/// </remarks>
public class XPlatTextBox : XPlatControl
{
    private const float HorizontalCaretPadding = 2f;

    private readonly XPlatCaret _caret;
    private XPlatFont _font = XPlatTheme.DefaultFont;
    private string _placeholderText = string.Empty;
    private int _caretIndex;
    private int _anchor;
    private float _scrollX;
    private bool _selecting;
    private int _maxLength;
    private bool _readOnly;
    private bool _isPassword;
    private char _passwordChar = '\u2022';

    public XPlatTextBox()
    {
        TabStop = true;
        Cursor = XPlatCursor.IBeam;
        _caret = new XPlatCaret(this);
        BorderWidth = 1f;
        CornerRadius = 6f;
        Padding = new XPlatThickness(8f, 6f);
        MinimumSize = new SKSize(0f, 30f);

        // A text box is always part of a selection: its own caret and anchor answer for a drag
        // that starts inside it, and the control-level contract answers for a selection that
        // merely passes through it. The two never fight - claiming a selection is exclusive.
        Selectable = true;
        SelectionStyle = XPlatSelectionStyle.Partial;
    }

    public XPlatTextBox(string text)
        : this()
    {
        Text = text;
    }

    /// <summary>An editable field is painted on the input fill until <see cref="XPlatControl.BackColor"/> is assigned.</summary>
    protected override SKColor? DefaultBackColor => XPlatTheme.InputBackground;

    /// <summary>Font used to draw the text. The text box does not take ownership of it.</summary>
    public XPlatFont Font
    {
        get => _font;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (ReferenceEquals(_font, value))
            {
                return;
            }

            _font = value;
            ClampScroll();
            Invalidate();
        }
    }

    /// <summary>The insertion point's caret, which this text box owns and positions.</summary>
    public XPlatCaret Caret => _caret;

    /// <summary>Index of the insertion point (0 .. <see cref="XPlatControl.Text"/>.Length).</summary>
    public int CaretIndex
    {
        get => _caretIndex;
        set => MoveCaretTo(value, extendSelection: false);
    }

    /// <summary>Lower bound of the selection; equals <see cref="CaretIndex"/> when nothing is selected.</summary>
    public int SelectionStart => Math.Min(_caretIndex, _anchor);

    public int SelectionLength => Math.Abs(_caretIndex - _anchor);

    public bool HasSelection => _caretIndex != _anchor;

    public string SelectedText => HasSelection ? Text.Substring(SelectionStart, SelectionLength) : string.Empty;

    /// <summary>Maximum number of characters the user may type. 0 means unlimited.</summary>
    public int MaxLength
    {
        get => _maxLength;
        set => _maxLength = Math.Max(0, value);
    }

    /// <summary>When true the text can be selected and copied but not modified.</summary>
    public bool ReadOnly
    {
        get => _readOnly;
        set
        {
            if (_readOnly == value)
            {
                return;
            }

            _readOnly = value;
            Invalidate();
        }
    }

    /// <summary>When true the text is rendered with <see cref="PasswordChar"/> instead of its real characters.</summary>
    public bool IsPassword
    {
        get => _isPassword;
        set
        {
            if (_isPassword == value)
            {
                return;
            }

            _isPassword = value;
            ClampScroll();
            Invalidate();
        }
    }

    /// <summary>The character used to mask the text when <see cref="IsPassword"/> is true.</summary>
    public char PasswordChar
    {
        get => _passwordChar;
        set
        {
            if (_passwordChar == value)
            {
                return;
            }

            _passwordChar = value;
            ClampScroll();
            Invalidate();
        }
    }

    /// <summary>Muted text shown while <see cref="XPlatControl.Text"/> is empty.</summary>
    public string PlaceholderText
    {
        get => _placeholderText;
        set
        {
            value ??= string.Empty;
            if (_placeholderText == value)
            {
                return;
            }

            _placeholderText = value;
            Invalidate();
        }
    }

    /// <summary>Selects <paramref name="length"/> characters starting at <paramref name="start"/>.</summary>
    public void Select(int start, int length)
    {
        ActivateCaret();

        start = Math.Clamp(start, 0, Text.Length);
        int end = Math.Clamp(start + Math.Max(0, length), start, Text.Length);

        _anchor = start;
        _caretIndex = end;
        _caret.ResetBlink();
        EnsureCaretVisible();
        Invalidate();
    }

    public void SelectAll() => Select(0, Text.Length);

    /// <summary>Collapses the selection, leaving the caret where it is.</summary>
    public void ClearSelection()
    {
        if (!HasSelection)
        {
            return;
        }

        _anchor = _caretIndex;
        Invalidate();
    }

    /// <summary>Copies the selection to the clipboard. Returns false when nothing is selected.</summary>
    public bool Copy()
    {
        if (!HasSelection)
        {
            return false;
        }

        XPlatClipboard.SetText(SelectedText);
        return true;
    }

    /// <summary>Copies the selection to the clipboard and deletes it. Returns false when nothing is selected.</summary>
    public bool Cut()
    {
        if (!HasSelection || ReadOnly)
        {
            return false;
        }

        XPlatClipboard.SetText(SelectedText);
        ReplaceRange(SelectionStart, SelectionLength, string.Empty);
        return true;
    }

    /// <summary>Inserts the clipboard text at the caret, replacing the selection. Returns false when there is no text.</summary>
    public bool Paste()
    {
        if (ReadOnly)
        {
            return false;
        }

        var text = Sanitize(XPlatClipboard.GetText());
        if (text.Length == 0)
        {
            return false;
        }

        InsertText(text);
        return true;
    }

    /// <summary>Inserts text at the caret, replacing the selection.</summary>
    public void InsertText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        ReplaceRange(SelectionStart, SelectionLength, text);
    }

    /// <summary>
    /// Copies the box's own selection, or the host's selection when the box is only part of one.
    /// </summary>
    private void CopySelection()
    {
        if (!Copy())
        {
            Host?.CopySelection();
        }
    }

    protected override SKSize MeasureContent(SKSize availableSize)
    {
        var sample = Text.Length == 0 ? "W" : Text;
        float width = MathF.Min(MathF.Ceiling(_font.MeasureWidth(sample)) + 4f, 320f);
        return new SKSize(width, MathF.Ceiling(_font.LineHeight) + 2f);
    }

    /// <summary>
    /// Maps a point to the nearest character boundary rather than a proportion of the width, so
    /// a selection dragged into the box lands between two characters.
    /// </summary>
    public override float GetSelectionOffset(SKPoint pointInLocal) => IndexFromX(pointInLocal.X);

    /// <summary>
    /// Adds the run of characters covered by the selection, using the same geometry as the box's
    /// own highlight so a control-level selection looks identical to an internal one.
    /// </summary>
    public override void GetSelectionRects(int start, int length, List<SKRect> output)
    {
        ArgumentNullException.ThrowIfNull(output);

        var content = ContentRect;
        if (length <= 0 || content.Width <= 0f || content.Height <= 0f)
        {
            return;
        }

        var display = DisplayText;
        float lineHeight = _font.LineHeight;
        float top = content.Top + MathF.Max(0f, (content.Height - lineHeight) / 2f);
        float originX = content.Left - _scrollX;

        float left = MathF.Max(originX + MeasureX(display, Math.Clamp(start, 0, display.Length)), content.Left);
        float right = MathF.Min(originX + MeasureX(display, Math.Clamp(start + length, 0, display.Length)), content.Right);
        if (right > left)
        {
            output.Add(new SKRect(left, top, right, top + lineHeight));
        }
    }

    /// <summary>
    /// Offers the editing commands the box supports - Cut, Copy, Paste and Select All - instead
    /// of the generic menu. Copy falls back to the host's selection when the box is only part of
    /// a larger selection, which is what the generic menu would have offered.
    /// </summary>
    protected internal override XPlatContextMenu? BuildContextMenu()
    {
        if (ContextMenu is not null || !Enabled)
        {
            return base.BuildContextMenu();
        }

        bool hasSelection = HasSelection;
        return new XPlatContextMenu()
            .Add(new XPlatMenuItem("Cut", () => Cut())
            {
                Shortcut = new XPlatShortcut(XPlatKey.X),
                Enabled = !ReadOnly && hasSelection,
            })
            .Add(new XPlatMenuItem("Copy", CopySelection)
            {
                Shortcut = new XPlatShortcut(XPlatKey.C),
                Enabled = hasSelection || IsSelected,
            })
            .Add(new XPlatMenuItem("Paste", () => Paste())
            {
                Shortcut = new XPlatShortcut(XPlatKey.V),
                Enabled = !ReadOnly,
            })
            .AddSeparator()
            .Add(new XPlatMenuItem("Select All", SelectAll)
            {
                Shortcut = new XPlatShortcut(XPlatKey.A),
                Enabled = Text.Length > 0,
            });
    }

    protected override void OnPaintBackground(SKCanvas canvas, SKRect rect)
    {
        var fill = Enabled ? BackColor ?? XPlatTheme.InputBackground : XPlatTheme.DisabledBackground;
        XPlatPainter.Fill(canvas, rect, fill, CornerRadius);

        var border = !Enabled
            ? XPlatTheme.DisabledBackground
            : IsFocused
                ? XPlatTheme.Accent
                : IsMouseOver ? XPlatTheme.SplitterHover : BorderColor;

        XPlatPainter.DrawBorder(canvas, rect, border, BorderWidth > 0f ? BorderWidth : 1f, CornerRadius);
    }

    protected override void OnPaint(SKCanvas canvas, SKRect rect)
    {
        var content = XPlatGeometry.Deflate(rect, Padding);
        if (content.Width <= 0f || content.Height <= 0f)
        {
            return;
        }

        var display = DisplayText;
        float lineHeight = _font.LineHeight;
        float top = content.Top + MathF.Max(0f, (content.Height - lineHeight) / 2f);
        float baseline = top + _font.BaselineOffset;
        float originX = content.Left - _scrollX;

        canvas.Save();
        canvas.ClipRect(content);

        using var paint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };

        if (HasSelection && Enabled)
        {
            float x1 = originX + MeasureX(display, SelectionStart);
            float x2 = originX + MeasureX(display, SelectionStart + SelectionLength);
            paint.Color = XPlatTheme.Selection;
            canvas.DrawRect(new SKRect(x1, top, x2, top + lineHeight), paint);
        }

        string text = display;
        var color = ForeColor;
        if (text.Length == 0 && !string.IsNullOrEmpty(_placeholderText))
        {
            text = _placeholderText;
            color = XPlatTheme.Placeholder;
        }

        if (!Enabled)
        {
            color = XPlatTheme.DisabledForeground;
        }

        if (text.Length > 0)
        {
            paint.Color = color;
            canvas.DrawText(text, originX, baseline, SKTextAlign.Left, _font.SkFont, paint);
        }

        _caret.Draw(canvas, originX + MeasureX(display, _caretIndex), top, lineHeight);

        canvas.Restore();
    }

    protected override void OnPaintBorder(SKCanvas canvas, SKRect rect)
    {
        // The border is drawn in OnPaintBackground so it stays behind the text and caret.
    }

    protected override void OnMouseDown(XPlatMouseEventArgs e)
    {
        base.OnMouseDown(e);

        if (e.Button != XPlatMouseButtons.Left || !Enabled)
        {
            return;
        }

        ActivateCaret();
        Focus();
        _selecting = true;
        CaptureMouse();

        int index = IndexFromX(e.Location.X);
        if (e.ClickCount >= 3)
        {
            SelectAll();
        }
        else if (e.ClickCount == 2)
        {
            var (start, length) = WordAt(index);
            Select(start, length);
        }
        else
        {
            _anchor = _caretIndex = index;
            _caret.ResetBlink();
            EnsureCaretVisible();
            Invalidate();
        }
    }

    protected override void OnMouseMove(XPlatMouseEventArgs e)
    {
        base.OnMouseMove(e);

        if (_selecting)
        {
            MoveCaretTo(IndexFromX(e.Location.X), extendSelection: true);
        }
    }

    protected override void OnMouseUp(XPlatMouseEventArgs e)
    {
        base.OnMouseUp(e);

        if (!_selecting)
        {
            return;
        }

        _selecting = false;
        ReleaseMouseCapture();
        Invalidate();
    }

    protected override void OnKeyDown(XPlatKeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled)
        {
            return;
        }

        bool shift = (e.Modifiers & XPlatModifiers.Shift) != 0;
        bool control = (e.Modifiers & XPlatModifiers.Control) != 0;

        switch (e.Key)
        {
            case XPlatKey.Left:
                MoveCaretTo(control ? PreviousWordIndex(_caretIndex) : _caretIndex - 1, shift);
                e.Handled = true;
                break;

            case XPlatKey.Right:
                MoveCaretTo(control ? NextWordIndex(_caretIndex) : _caretIndex + 1, shift);
                e.Handled = true;
                break;

            case XPlatKey.Home:
                MoveCaretTo(0, shift);
                e.Handled = true;
                break;

            case XPlatKey.End:
                MoveCaretTo(Text.Length, shift);
                e.Handled = true;
                break;

            case XPlatKey.Backspace:
                e.Handled = true;
                if (ReadOnly)
                {
                    break;
                }

                if (HasSelection)
                {
                    ReplaceRange(SelectionStart, SelectionLength, string.Empty);
                }
                else if (_caretIndex > 0)
                {
                    ReplaceRange(_caretIndex - 1, 1, string.Empty);
                }

                break;

            case XPlatKey.Delete:
                e.Handled = true;
                if (ReadOnly)
                {
                    break;
                }

                if (HasSelection)
                {
                    ReplaceRange(SelectionStart, SelectionLength, string.Empty);
                }
                else if (_caretIndex < Text.Length)
                {
                    ReplaceRange(_caretIndex, 1, string.Empty);
                }

                break;

            case XPlatKey.A when control:
                SelectAll();
                e.Handled = true;
                break;

            case XPlatKey.C when control:
                Copy();
                e.Handled = true;
                break;

            case XPlatKey.X when control:
                Cut();
                e.Handled = true;
                break;

            case XPlatKey.V when control:
                Paste();
                e.Handled = true;
                break;
        }
    }

    protected override void OnTextInput(XPlatTextInputEventArgs e)
    {
        base.OnTextInput(e);

        if (e.Handled || ReadOnly)
        {
            return;
        }

        var text = Sanitize(e.Text);
        if (text.Length == 0)
        {
            return;
        }

        InsertText(text);
        e.Handled = true;
    }

    protected override void OnTextChanged(EventArgs e)
    {
        _caretIndex = Math.Clamp(_caretIndex, 0, Text.Length);
        _anchor = Math.Clamp(_anchor, 0, Text.Length);
        ClampScroll();

        base.OnTextChanged(e);
    }

    protected override void OnGotFocus(EventArgs e)
    {
        base.OnGotFocus(e);
        ActivateCaret();
        EnsureCaretVisible();
        Invalidate();
    }

    protected override void OnLostFocus(EventArgs e)
    {
        base.OnLostFocus(e);
        _selecting = false;
        Invalidate();
    }

    protected override void OnSelectionCleared(EventArgs e)
    {
        base.OnSelectionCleared(e);
        _caret.Deactivate();
        _anchor = _caretIndex;
    }

    protected override void OnBoundsChanged(EventArgs e)
    {
        base.OnBoundsChanged(e);
        EnsureCaretVisible();
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        if (!Enabled)
        {
            _selecting = false;
            ReleaseMouseCapture();
        }

        base.OnEnabledChanged(e);
    }

    private string DisplayText =>
        _isPassword && Text.Length > 0 ? new string(_passwordChar, Text.Length) : Text;

    private void ReplaceRange(int start, int length, string replacement)
    {
        if (ReadOnly)
        {
            return;
        }

        ActivateCaret();

        start = Math.Clamp(start, 0, Text.Length);
        length = Math.Clamp(length, 0, Text.Length - start);

        if (length == 0 && replacement.Length == 0)
        {
            return;
        }

        if (_maxLength > 0)
        {
            int room = _maxLength - (Text.Length - length);
            if (room <= 0)
            {
                return;
            }

            if (replacement.Length > room)
            {
                replacement = replacement[..room];
            }
        }

        _anchor = _caretIndex = start + replacement.Length;
        Text = Text.Remove(start, length).Insert(start, replacement);

        _caret.ResetBlink();
        EnsureCaretVisible();
        Invalidate();
    }

    private void MoveCaretTo(int index, bool extendSelection)
    {
        ActivateCaret();

        _caretIndex = Math.Clamp(index, 0, Text.Length);
        if (!extendSelection)
        {
            _anchor = _caretIndex;
        }

        _caret.ResetBlink();
        EnsureCaretVisible();
        Invalidate();
    }

    /// <summary>
    /// Takes the caret and tells the host that this text box is taking over the selection, so any
    /// selection elsewhere in the UI is cleared first.
    /// </summary>
    private void ActivateCaret()
    {
        _caret.Activate();
        ClaimSelection();
    }

    private void EnsureCaretVisible()
    {
        float width = ContentRect.Width;
        if (width <= 0f)
        {
            _scrollX = 0f;
            return;
        }

        float caretX = MeasureX(DisplayText, _caretIndex);
        if (caretX - _scrollX > width - HorizontalCaretPadding)
        {
            _scrollX = caretX - width + HorizontalCaretPadding;
        }
        else if (caretX - _scrollX < 0f)
        {
            _scrollX = caretX;
        }

        ClampScroll();
    }

    private void ClampScroll()
    {
        float width = ContentRect.Width;
        float textWidth = _font.MeasureWidth(DisplayText);
        float max = MathF.Max(0f, textWidth - width + HorizontalCaretPadding + _caret.Width);
        _scrollX = Math.Clamp(_scrollX, 0f, max);
    }

    private float MeasureX(string display, int index)
    {
        if (index <= 0 || display.Length == 0)
        {
            return 0f;
        }

        return _font.MeasureWidth(index >= display.Length ? display : display[..index]);
    }

    private int IndexFromX(float xInControl)
    {
        var display = DisplayText;
        if (display.Length == 0)
        {
            return 0;
        }

        float target = xInControl - ContentRect.Left + _scrollX;
        if (target <= 0f)
        {
            return 0;
        }

        float best = float.MaxValue;
        int bestIndex = 0;
        for (int i = 0; i <= display.Length; i++)
        {
            float distance = MathF.Abs(MeasureX(display, i) - target);
            if (distance < best)
            {
                best = distance;
                bestIndex = i;
            }
        }

        return bestIndex;
    }

    private int PreviousWordIndex(int index)
    {
        var text = Text;
        int i = Math.Clamp(index, 0, text.Length);
        while (i > 0 && !IsWordChar(text[i - 1]))
        {
            i--;
        }

        while (i > 0 && IsWordChar(text[i - 1]))
        {
            i--;
        }

        return i;
    }

    private int NextWordIndex(int index)
    {
        var text = Text;
        int i = Math.Clamp(index, 0, text.Length);
        while (i < text.Length && IsWordChar(text[i]))
        {
            i++;
        }

        while (i < text.Length && !IsWordChar(text[i]))
        {
            i++;
        }

        return i;
    }

    private (int Start, int Length) WordAt(int index)
    {
        var text = Text;
        if (text.Length == 0)
        {
            return (0, 0);
        }

        int i = Math.Clamp(index, 0, text.Length - 1);
        int start = i;
        int end = i + 1;

        if (IsWordChar(text[i]))
        {
            while (start > 0 && IsWordChar(text[start - 1]))
            {
                start--;
            }

            while (end < text.Length && IsWordChar(text[end]))
            {
                end++;
            }
        }
        else
        {
            while (start > 0 && !IsWordChar(text[start - 1]))
            {
                start--;
            }

            while (end < text.Length && !IsWordChar(text[end]))
            {
                end++;
            }
        }

        return (start, end - start);
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    /// <summary>Strips control characters (including line breaks) so the field stays single-line.</summary>
    private static string Sanitize(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            if (!char.IsControl(c))
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }
}