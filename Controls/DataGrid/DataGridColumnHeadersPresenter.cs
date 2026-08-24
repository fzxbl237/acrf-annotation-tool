using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace PatChes.Controls.DataGrid;

/// <summary>
/// Panel that arranges DataGridColumnHeader children by column widths.
/// Returns total column width from MeasureOverride (matching RowsCanvas behavior).
/// ProDataGrid pattern: measure each header at its column width, report total.
/// </summary>
public class DataGridColumnHeadersPresenter : Panel
{
    public DataGrid? OwningGrid { get; set; }
    internal double HorizontalOffset { get; set; }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (OwningGrid == null || OwningGrid.Columns.Count == 0)
            return new Size(0, 0);

        double totalWidth = OwningGrid.RowDragHandleOffset;
        double maxHeight = 0;
        foreach (var child in Children)
        {
            if (child is DataGridColumnHeader header && header.OwningColumn != null)
            {
                double w = header.OwningColumn.GetEffectiveWidth();
                header.Measure(new Size(w, double.PositiveInfinity));
                totalWidth += w;
                maxHeight = Math.Max(maxHeight, header.DesiredSize.Height);
            }
            else if (child is Border slot)
            {
                slot.Measure(new Size(OwningGrid.RowDragHandleOffset, double.PositiveInfinity));
                maxHeight = Math.Max(maxHeight, slot.DesiredSize.Height);
            }
        }
        maxHeight = Math.Max(maxHeight, OwningGrid.ColumnHeaderHeight);
        return new Size(totalWidth, maxHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (OwningGrid == null)
            return base.ArrangeOverride(finalSize);

        double height = finalSize.Height;
        var columns = OwningGrid.Columns;
        int leftFrozen = Math.Min(OwningGrid.LeftFrozenColumnCount, columns.Count);
        int rightFrozen = Math.Min(OwningGrid.RightFrozenColumnCount, columns.Count);
        int totalCount = columns.Count;
        double hOff = OwningGrid.HorizontalOffset;

        double leftFrozenW = 0;
        for (int i = 0; i < leftFrozen; i++)
            leftFrozenW += columns[i].GetEffectiveWidth();

        double scrollableW = 0;
        for (int i = leftFrozen; i < totalCount - rightFrozen; i++)
            scrollableW += columns[i].GetEffectiveWidth();

        double rightFrozenW = 0;
        for (int i = totalCount - rightFrozen; i < totalCount; i++)
            rightFrozenW += columns[i].GetEffectiveWidth();

        IBrush frozenHeaderBg = new SolidColorBrush(OwningGrid.HeaderBackground is SolidColorBrush hb ? hb.Color : Color.Parse("#F0F0F0"));
        bool hasFrozen = leftFrozen > 0 || rightFrozen > 0;

        double scrollableEnd = leftFrozenW + scrollableW - hOff;
        double rightFrozenMaxX = finalSize.Width - rightFrozenW;
        if (rightFrozenMaxX < 0) rightFrozenMaxX = 0;
        scrollableEnd = Math.Min(scrollableEnd, rightFrozenMaxX);

        foreach (var child in Children)
        {
            if (child is Border slot)
            {
                slot.Arrange(new Rect(0, 0, OwningGrid.RowDragHandleOffset, height));
                continue;
            }

            if (child is DataGridColumnHeader header && header.OwningColumn != null)
            {
                int colIdx = columns.IndexOf(header.OwningColumn);
                double w = header.OwningColumn.GetEffectiveWidth();
                bool isFrozen = colIdx < leftFrozen || colIdx >= totalCount - rightFrozen;

                double headerX;
                if (colIdx < leftFrozen)
                {
                    double pos = 0;
                    for (int i = 0; i < colIdx; i++) pos += columns[i].GetEffectiveWidth();
                    headerX = pos;
                }
                else if (colIdx >= totalCount - rightFrozen)
                {
                    double pos = scrollableEnd;
                    for (int i = totalCount - rightFrozen; i < colIdx; i++) pos += columns[i].GetEffectiveWidth();
                    headerX = pos;
                }
                else
                {
                    double pos = leftFrozenW;
                    for (int i = leftFrozen; i < colIdx; i++) pos += columns[i].GetEffectiveWidth();
                    headerX = pos - hOff;
                }

                header.Arrange(new Rect(headerX + OwningGrid.RowDragHandleOffset, 0, w, height));

                if (hasFrozen)
                {
                    header.SetFrozenBackground(isFrozen ? frozenHeaderBg : Brushes.Transparent);
                }
            }
        }
        return finalSize;
    }

    internal void OnColumnWidthChanged()
    {
        InvalidateMeasure();
        InvalidateArrange();
    }
}
