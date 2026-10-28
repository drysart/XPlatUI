using SkiaSharp;

namespace XPlatUI;

/// <summary>
/// Minimal text layout engine: measurement, greedy word wrapping, alignment and drawing.
/// Kept intentionally small (and allocation-light) so the library stays AOT friendly.
/// </summary>
public static class XPlatTextRenderer
{
    public static SKSize Measure(string? text, XPlatFont font, float maxWidth, bool wrap)
    {
        if (string.IsNullOrEmpty(text))
        {
            return SKSize.Empty;
        }

        var lines = BuildLines(text, font, maxWidth, wrap);
        float width = 0f;
        foreach (var line in lines)
        {
            width = MathF.Max(width, font.MeasureWidth(line));
        }

        return new SKSize(width, lines.Count * font.LineHeight);
    }

    public static void Draw(
        SKCanvas canvas,
        string? text,
        XPlatFont font,
        SKColor color,
        SKRect bounds,
        XPlatContentAlignment alignment = XPlatContentAlignment.MiddleLeft,
        bool wrap = false)
    {
        if (string.IsNullOrEmpty(text) || bounds.Width <= 0f || bounds.Height <= 0f)
        {
            return;
        }

        var lines = BuildLines(text, font, bounds.Width, wrap);
        if (lines.Count == 0)
        {
            return;
        }

        float lineHeight = font.LineHeight;
        float blockHeight = lines.Count * lineHeight;

        float top = alignment switch
        {
            XPlatContentAlignment.MiddleLeft or XPlatContentAlignment.MiddleCenter or XPlatContentAlignment.MiddleRight
                => bounds.Top + ((bounds.Height - blockHeight) / 2f),
            XPlatContentAlignment.BottomLeft or XPlatContentAlignment.BottomCenter or XPlatContentAlignment.BottomRight
                => bounds.Bottom - blockHeight,
            _ => bounds.Top,
        };

        using var paint = new SKPaint
        {
            Color = color,
            IsAntialias = true,
            Style = SKPaintStyle.Fill,
        };

        for (int i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            float lineWidth = font.MeasureWidth(line);
            float x = alignment switch
            {
                XPlatContentAlignment.TopCenter or XPlatContentAlignment.MiddleCenter or XPlatContentAlignment.BottomCenter
                    => bounds.Left + ((bounds.Width - lineWidth) / 2f),
                XPlatContentAlignment.TopRight or XPlatContentAlignment.MiddleRight or XPlatContentAlignment.BottomRight
                    => bounds.Right - lineWidth,
                _ => bounds.Left,
            };

            float baseline = top + (i * lineHeight) + font.BaselineOffset;
            canvas.DrawText(line, x, baseline, SKTextAlign.Left, font.SkFont, paint);
        }
    }

    /// <summary>Returns the individual lines that <paramref name="text"/> occupies.</summary>
    public static List<string> BuildLines(string? text, XPlatFont font, float maxWidth, bool wrap)
    {
        var lines = new List<string>();
        if (string.IsNullOrEmpty(text))
        {
            return lines;
        }

        foreach (var rawLine in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            if (!wrap || maxWidth <= 0f || font.MeasureWidth(rawLine) <= maxWidth)
            {
                lines.Add(rawLine);
            }
            else
            {
                WrapLine(rawLine, font, maxWidth, lines);
            }
        }

        return lines;
    }

    private static void WrapLine(string line, XPlatFont font, float maxWidth, List<string> output)
    {
        var current = string.Empty;
        foreach (var word in line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = current.Length == 0 ? word : current + " " + word;
            if (font.MeasureWidth(candidate) <= maxWidth)
            {
                current = candidate;
                continue;
            }

            if (current.Length > 0)
            {
                output.Add(current);
                current = string.Empty;
            }

            // A single word that is wider than the available space gets hard-broken.
            var rest = word;
            while (font.MeasureWidth(rest) > maxWidth)
            {
                int take = HardBreakIndex(rest, font, maxWidth);
                if (take <= 0)
                {
                    break;
                }

                output.Add(rest[..take]);
                rest = rest[take..];
            }

            current = rest;
        }

        if (current.Length > 0)
        {
            output.Add(current);
        }
    }

    private static int HardBreakIndex(string text, XPlatFont font, float maxWidth)
    {
        int best = 0;
        for (int i = 1; i <= text.Length; i++)
        {
            if (font.MeasureWidth(text[..i]) > maxWidth)
            {
                break;
            }

            best = i;
        }

        return best;
    }
}
