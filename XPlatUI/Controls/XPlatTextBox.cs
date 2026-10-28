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
/// Adapters should call <see cref="XPlatUiHost.ProcessTick"/> from a timer so the caret
/// blinks, and should install a platform clipboard into <see cref="XPlatClipboard.Current"/>.
/// </remarks>
public class XPlatTextBox : XPlatControl
{
    private const float CaretWidth = 1.5f;
    private const float HorizontalCaretPadding = 2f;

    private static readonly TimeSpan CaretBlinkInterval = TimeSpan.FromMilliseconds(530);

    private XPlatFont _font = XPlatTheme.DefaultFont;
    private string _placeholderText = string.Empty;
    private int _caret;
    private int _anchor;
    private float _scrollX;
    private bool _caretVisible = true;
    private bool _selecting;
    private TimeSpan _blinkElapsed;
    private int _maxLength;
    private bool _readOnly;
    private bool _isPassword;
    private char _passwordChar = '\u2022';

    public XPlatTextBox()
    {
        TabStop = true;
        Cursor = XPlatCursor.IBeam;
        BackColor = XPlatTheme.InputBackground;
        BorderColor = XPlatTheme.Border;
        BorderWidth = 1f;
        CornerRadius = 6f;
        Padding = new XPlatThickness(8f, 6f);
        MinimumSize = new SKSize(0f, 30f);
    }

    public XPlatTextBox(string text)
        : this()
    {
        Text = text;
    }

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

    /// <summary>Index of the insertion point (0 .. <see cref="XPlatControl.Text"/>.Length).</summary>
    public int CaretIndex
    {
        get => _caret;
        set => MoveCaretTo(value, extendSelection: false);
    }

    /// <summary>Lower bound of the selection; equals <see cref="CaretIndex"/> when nothing is selected.</summary>
    public int SelectionStart => Math.Min(_caret, _anchor);

    public int SelectionLength => Math.Abs(_caret - _anchor);

    public bool HasSelection => _caret != _anchor;

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
        start = Math.Clamp(start, 0, Text.Length);
        int end = Math.Clamp(start + Math.Max(0, length), start, Text.Length);

        _anchor = start;
        _caret = end;
        ResetCaretBlink();
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

        _anchor = _caret;
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

    protected override SKSize MeasureContent(SKSize availableSize)
    {
        var sample = Text.Length == 0 ? "W" : Text;
        float width = MathF.Min(MathF.Ceiling(_font.MeasureWidth(sample)) + 4f, 320f);
        return new SKSize(width, MathF.Ceiling(_font.LineHeight) + 2f);
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

        if (IsFocused && Enabled && _caretVisible)
        {
            float caretX = originX + MeasureX(display, _caret);
            paint.Color = XPlatTheme.Caret;
            canvas.DrawRect(new SKRect(caretX, top, caretX + CaretWidth, top + lineHeight), paint);
        }

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
            _anchor = _caret = index;
            ResetCaretBlink();
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
                MoveCaretTo(control ? PreviousWordIndex(_caret) : _caret - 1, shift);
                e.Handled = true;
                break;

            case XPlatKey.Right:
                MoveCaretTo(control ? NextWordIndex(_caret) : _caret + 1, shift);
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
                else if (_caret > 0)
                {
                    ReplaceRange(_caret - 1, 1, string.Empty);
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
                else if (_caret < Text.Length)
                {
                    ReplaceRange(_caret, 1, string.Empty);
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
        _caret = Math.Clamp(_caret, 0, Text.Length);
        _anchor = Math.Clamp(_anchor, 0, Text.Length);
        ClampScroll();

        base.OnTextChanged(e);
    }

    protected override void OnTick(TimeSpan elapsed)
    {
        base.OnTick(elapsed);

        if (!IsFocused || !Enabled)
        {
            return;
        }

        _blinkElapsed += elapsed;
        if (_blinkElapsed < CaretBlinkInterval)
        {
            return;
        }

        _blinkElapsed = TimeSpan.Zero;
        _caretVisible = !_caretVisible;
        Invalidate();
    }

    protected override void OnGotFocus(EventArgs e)
    {
        base.OnGotFocus(e);
        ResetCaretBlink();
        EnsureCaretVisible();
        Invalidate();
    }

    protected override void OnLostFocus(EventArgs e)
    {
        base.OnLostFocus(e);
        _selecting = false;
        _caretVisible = true;
        _blinkElapsed = TimeSpan.Zero;
        Invalidate();
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

        _anchor = _caret = start + replacement.Length;
        Text = Text.Remove(start, length).Insert(start, replacement);

        ResetCaretBlink();
        EnsureCaretVisible();
        Invalidate();
    }

    private void MoveCaretTo(int index, bool extendSelection)
    {
        _caret = Math.Clamp(index, 0, Text.Length);
        if (!extendSelection)
        {
            _anchor = _caret;
        }

        ResetCaretBlink();
        EnsureCaretVisible();
        Invalidate();
    }

    private void ResetCaretBlink()
    {
        _caretVisible = true;
        _blinkElapsed = TimeSpan.Zero;
    }

    private void EnsureCaretVisible()
    {
        float width = ContentRect.Width;
        if (width <= 0f)
        {
            _scrollX = 0f;
            return;
        }

        float caretX = MeasureX(DisplayText, _caret);
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
        float max = MathF.Max(0f, textWidth - width + HorizontalCaretPadding + CaretWidth);
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