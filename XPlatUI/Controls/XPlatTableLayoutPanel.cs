using System.Collections.Generic;
using SkiaSharp;

namespace XPlatUI;

/// <summary>
/// Arranges children on a grid of rows and columns. Sizing follows WinForms:
/// <see cref="XPlatSizeType.Absolute"/> rows/columns take a fixed size,
/// <see cref="XPlatSizeType.Percent"/> ones share the leftover space and
/// <see cref="XPlatSizeType.AutoSize"/> ones fit their content.
/// </summary>
public class XPlatTableLayoutPanel : XPlatPanel
{
    private readonly List<XPlatRowStyle> _rowStyles = new();
    private readonly List<XPlatColumnStyle> _columnStyles = new();
    private readonly Dictionary<XPlatControl, (int Column, int Row)> _cells = new();

    public XPlatTableLayoutPanel()
    {
        Padding = new XPlatThickness(0f);
        CellPadding = new XPlatThickness(4f);
    }

    public IList<XPlatRowStyle> RowStyles => _rowStyles;

    public IList<XPlatColumnStyle> ColumnStyles => _columnStyles;

    public int RowCount => _rowStyles.Count;

    public int ColumnCount => _columnStyles.Count;

    /// <summary>Space inserted around every cell.</summary>
    public XPlatThickness CellPadding { get; set; }

    /// <summary>Sets the row and column a child occupies.</summary>
    public void SetCellPosition(XPlatControl control, int column, int row)
    {
        ArgumentNullException.ThrowIfNull(control);
        _cells[control] = (Math.Max(0, column), Math.Max(0, row));
        PerformLayout();
        Invalidate();
    }

    public (int Column, int Row) GetCellPosition(XPlatControl control)
    {
        ArgumentNullException.ThrowIfNull(control);
        return _cells.TryGetValue(control, out var cell) ? cell : (0, 0);
    }

    /// <summary>Convenience helper: adds a child at a specific cell.</summary>
    public void Add(XPlatControl control, int column, int row)
    {
        Controls.Add(control);
        SetCellPosition(control, column, row);
    }

    public void AddColumn(XPlatSizeType sizeType, float value) => AddColumn(new XPlatColumnStyle(sizeType, value));

    public void AddRow(XPlatSizeType sizeType, float value) => AddRow(new XPlatRowStyle(sizeType, value));

    public void AddColumn(XPlatColumnStyle style)
    {
        ArgumentNullException.ThrowIfNull(style);
        _columnStyles.Add(style);
        PerformLayout();
        Invalidate();
    }

    public void AddRow(XPlatRowStyle style)
    {
        ArgumentNullException.ThrowIfNull(style);
        _rowStyles.Add(style);
        PerformLayout();
        Invalidate();
    }

    protected override SKSize MeasureContent(SKSize availableSize)
    {
        var (columns, rows) = ComputeTrackSizes(availableSize);

        float width = 0f;
        float height = 0f;
        foreach (var size in columns)
        {
            width += size;
        }

        foreach (var size in rows)
        {
            height += size;
        }

        return new SKSize(width, height);
    }

    public override void PerformLayout()
    {
        if (_columnStyles.Count == 0 || _rowStyles.Count == 0)
        {
            return;
        }

        var content = ContentRect;
        var (columns, rows) = ComputeTrackSizes(new SKSize(content.Width, content.Height));

        var columnOffsets = new float[columns.Length + 1];
        for (int i = 0; i < columns.Length; i++)
        {
            columnOffsets[i + 1] = columnOffsets[i] + columns[i];
        }

        var rowOffsets = new float[rows.Length + 1];
        for (int i = 0; i < rows.Length; i++)
        {
            rowOffsets[i + 1] = rowOffsets[i] + rows[i];
        }

        foreach (var child in Controls)
        {
            if (!child.Visible)
            {
                continue;
            }

            var (column, row) = GetCellPosition(child);
            column = Math.Clamp(column, 0, columns.Length - 1);
            row = Math.Clamp(row, 0, rows.Length - 1);

            var cell = new SKRect(
                content.Left + columnOffsets[column],
                content.Top + rowOffsets[row],
                content.Left + columnOffsets[column + 1],
                content.Top + rowOffsets[row + 1]);

            var inner = XPlatGeometry.Deflate(cell, CellPadding);
            if (child.AutoSize)
            {
                var desired = child.Measure(new SKSize(inner.Width, inner.Height));
                child.Size = new SKSize(
                    MathF.Min(desired.Width, inner.Width),
                    MathF.Min(desired.Height, inner.Height));
            }

            var size = child.Size;
            var bounds = new SKRect(inner.Left, inner.Top, inner.Left + size.Width, inner.Top + size.Height);
            switch (child.Dock)
            {
                case DockStyle.Fill:
                    bounds = inner;
                    break;
                case DockStyle.Right:
                    bounds = new SKRect(inner.Right - size.Width, inner.Top, inner.Right, inner.Top + size.Height);
                    break;
                case DockStyle.Bottom:
                    bounds = new SKRect(inner.Left, inner.Bottom - size.Height, inner.Left + size.Width, inner.Bottom);
                    break;
            }

            child.Bounds = bounds;
        }
    }

    private (float[] Columns, float[] Rows) ComputeTrackSizes(SKSize availableSize)
    {
        var columns = new float[_columnStyles.Count];
        var rows = new float[_rowStyles.Count];

        for (int i = 0; i < _columnStyles.Count; i++)
        {
            var style = _columnStyles[i];
            if (style.SizeType == XPlatSizeType.Absolute)
            {
                columns[i] = style.Value;
            }
        }

        for (int i = 0; i < _rowStyles.Count; i++)
        {
            var style = _rowStyles[i];
            if (style.SizeType == XPlatSizeType.Absolute)
            {
                rows[i] = style.Value;
            }
        }

        foreach (var child in Controls)
        {
            if (!child.Visible)
            {
                continue;
            }

            var (column, row) = GetCellPosition(child);
            column = Math.Clamp(column, 0, columns.Length - 1);
            row = Math.Clamp(row, 0, rows.Length - 1);

            bool autoColumn = _columnStyles[column].SizeType == XPlatSizeType.AutoSize;
            bool autoRow = _rowStyles[row].SizeType == XPlatSizeType.AutoSize;
            if (!autoColumn && !autoRow)
            {
                continue;
            }

            var desired = child.Measure(availableSize);
            if (autoColumn)
            {
                columns[column] = MathF.Max(columns[column], desired.Width + CellPadding.Horizontal);
            }

            if (autoRow)
            {
                rows[row] = MathF.Max(rows[row], desired.Height + CellPadding.Vertical);
            }
        }

        DistributePercent(_columnStyles, columns, availableSize.Width);
        DistributePercent(_rowStyles, rows, availableSize.Height);
        return (columns, rows);
    }

    private static void DistributePercent<T>(IList<T> styles, float[] sizes, float available)
        where T : XPlatRowOrColumnStyle
    {
        float used = 0f;
        float totalPercent = 0f;
        for (int i = 0; i < styles.Count; i++)
        {
            if (styles[i].SizeType == XPlatSizeType.Percent)
            {
                totalPercent += styles[i].Value;
            }
            else
            {
                used += sizes[i];
            }
        }

        if (totalPercent <= 0f)
        {
            return;
        }

        float remaining = MathF.Max(0f, available - used);
        for (int i = 0; i < styles.Count; i++)
        {
            if (styles[i].SizeType == XPlatSizeType.Percent)
            {
                sizes[i] = remaining * styles[i].Value / totalPercent;
            }
        }
    }
}
