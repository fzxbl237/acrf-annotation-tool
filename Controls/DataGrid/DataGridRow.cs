using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace PatChes.Controls.DataGrid;

internal sealed class DataGridRowDragHandle : Border
{
    private readonly DataGridRow _row;
    private bool _isPointerOver;

    internal bool IsPointerOver => _isPointerOver;

    public DataGridRowDragHandle(DataGridRow row)
    {
        _row = row;
        Background = Brushes.Transparent;
        Cursor = new Cursor(StandardCursorType.SizeAll);
        IsHitTestVisible = true;
        ZIndex = 100;
    }

    protected override void OnPointerEntered(PointerEventArgs e)
    {
        base.OnPointerEntered(e);
        _isPointerOver = true;
        _row.InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _isPointerOver = false;
        _row.InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (_row.OwningGrid?.TryBeginRowDrag(_row, e) != true)
            return;

        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        _row.OwningGrid?.UpdateRowDrag(_row, e);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _row.OwningGrid?.CompleteRowDrag(_row, e);
        if (ReferenceEquals(e.Pointer.Captured, this))
            e.Pointer.Capture(null);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        _row.OwningGrid?.CancelRowDrag(_row, e.Pointer);
        base.OnPointerCaptureLost(e);
    }
}

public class DataGridRow : Control
{
    public static readonly new StyledProperty<double> HeightProperty =
        AvaloniaProperty.Register<DataGridRow, double>(nameof(Height), 28);
    public static readonly StyledProperty<int> IndexProperty =
        AvaloniaProperty.Register<DataGridRow, int>(nameof(Index));
    public static readonly StyledProperty<bool> IsSelectedProperty =
        AvaloniaProperty.Register<DataGridRow, bool>(nameof(IsSelected));
    public static readonly StyledProperty<bool> IsValidProperty =
        AvaloniaProperty.Register<DataGridRow, bool>(nameof(IsValid), true);
    public static readonly StyledProperty<DataGridValidationSeverity> ValidationSeverityProperty =
        AvaloniaProperty.Register<DataGridRow, DataGridValidationSeverity>(nameof(ValidationSeverity));

    private readonly List<DataGridCell> _cells = new();
    private readonly DataGridRowDragHandle _dragHandle;
    private bool _isPointerOver;
    private bool _isDragging;

    public int Index { get => GetValue(IndexProperty); set => SetValue(IndexProperty, value); }
    internal bool IsPointerOver => _isPointerOver;
    internal bool IsDragging => _isDragging;
    public bool IsSelected { get => GetValue(IsSelectedProperty); set { SetValue(IsSelectedProperty, value); RefreshVisuals(); } }
    public bool IsValid { get => GetValue(IsValidProperty); set => SetValue(IsValidProperty, value); }
    public DataGridValidationSeverity ValidationSeverity { get => GetValue(ValidationSeverityProperty); set => SetValue(ValidationSeverityProperty, value); }
    public new double Height { get => GetValue(HeightProperty); set => SetValue(HeightProperty, value); }
    public IReadOnlyList<DataGridCell> Cells => _cells;
    public DataGrid? OwningGrid { get; internal set; }

    public DataGridRow()
    {
        _dragHandle = new DataGridRowDragHandle(this);
        LogicalChildren.Add(_dragHandle);
        VisualChildren.Add(_dragHandle);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        foreach (var cell in _cells) cell.EnsureBuilt();
        double w = OwningGrid?.Columns.Sum(c => c.GetEffectiveWidth()) ?? 0;
        return new Size(w, availableSize.Height);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (OwningGrid?.CanUserReorderRows != true)
            return;

        var handleBounds = new Rect(6, Math.Max(4, (Bounds.Height - 24) / 2), 24, 24);
        if (_dragHandle.IsPointerOver)
            context.DrawRectangle(new SolidColorBrush(Color.Parse("#F5F5F5")), null, handleBounds, 4, 4);

        var handleBrush = new SolidColorBrush(Color.Parse(_isDragging ? "#1677FF" : "#8C8C8C"));
        var centerX = 18d;
        var centerY = Bounds.Height / 2;
        for (var row = -1; row <= 1; row++)
        {
            for (var column = -1; column <= 0; column++)
                context.DrawEllipse(handleBrush, null, new Point(centerX + column * 5, centerY + row * 5), 1.1, 1.1);
        }

        if (OwningGrid.GridLinesVisibility is DataGridGridLinesVisibility.Horizontal or DataGridGridLinesVisibility.Both)
        {
            context.DrawLine(
                new Pen(new SolidColorBrush(Color.Parse("#E8E8E8")), 0.5),
                new Point(0, Math.Max(0, Bounds.Height - 0.25)),
                new Point(Bounds.Width, Math.Max(0, Bounds.Height - 0.25)));
        }
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var grid = OwningGrid;
        if (grid == null) return base.ArrangeOverride(finalSize);

        var columns = grid.Columns;
        int leftFrozen = Math.Min(grid.LeftFrozenColumnCount, columns.Count);
        int rightFrozen = Math.Min(grid.RightFrozenColumnCount, columns.Count);
        int totalCount = columns.Count;
        double hOff = grid.HorizontalOffset;
        double vpW = finalSize.Width;

        double leftFrozenW = 0;
        for (int i = 0; i < leftFrozen; i++)
            leftFrozenW += columns[i].GetEffectiveWidth();

        double scrollableW = 0;
        for (int i = leftFrozen; i < totalCount - rightFrozen; i++)
            scrollableW += columns[i].GetEffectiveWidth();

        double rightFrozenW = 0;
        for (int i = totalCount - rightFrozen; i < totalCount; i++)
            rightFrozenW += columns[i].GetEffectiveWidth();

        double scrollableEnd = leftFrozenW + scrollableW - hOff;
        double rightFrozenMaxX = vpW - rightFrozenW;
        if (rightFrozenMaxX < 0) rightFrozenMaxX = 0;
        scrollableEnd = Math.Min(scrollableEnd, rightFrozenMaxX);

        _dragHandle.IsVisible = grid.CanUserReorderRows;
        _dragHandle.ZIndex = 100;
        _dragHandle.Arrange(new Rect(0, 0, grid.RowDragHandleOffset, finalSize.Height));

        foreach (var cell in _cells)
        {
            var col = cell.Column;
            if (col == null) continue;
            int colIdx = columns.IndexOf(col);
            double w = col.GetEffectiveWidth();

            double cellX;
            if (colIdx < leftFrozen)
            {
                double pos = 0;
                for (int i = 0; i < colIdx; i++) pos += columns[i].GetEffectiveWidth();
                cellX = pos;
            }
            else if (colIdx >= totalCount - rightFrozen)
            {
                double pos = scrollableEnd;
                for (int i = totalCount - rightFrozen; i < colIdx; i++) pos += columns[i].GetEffectiveWidth();
                cellX = pos;
            }
            else
            {
                double pos = leftFrozenW;
                for (int i = leftFrozen; i < colIdx; i++) pos += columns[i].GetEffectiveWidth();
                cellX = pos - hOff;
            }

            cell.Arrange(new Rect(cellX + grid.RowDragHandleOffset, 0, w, finalSize.Height));
        }

        ClipToBounds = (leftFrozen > 0 || rightFrozen > 0);
        return finalSize;
    }

    public void UpdateCells()
    {
        // Remove only previously-built cells, keep the persistent horizontal line
        foreach (var cell in _cells)
        {
            LogicalChildren.Remove(cell);
            VisualChildren.Remove(cell);
        }
        _cells.Clear();
        if (OwningGrid == null || DataContext == null)
            return;

        var columns = OwningGrid.Columns;
        int leftFrozen = Math.Min(OwningGrid.LeftFrozenColumnCount, columns.Count);
        int rightFrozen = Math.Min(OwningGrid.RightFrozenColumnCount, columns.Count);
        int totalCount = columns.Count;

        for (int ci = 0; ci < columns.Count; ci++)
        {
            if (!columns[ci].IsVisible) continue;
            if (ci >= leftFrozen && ci < totalCount - rightFrozen)
            {
                var column = columns[ci];
                var cell = new DataGridCell { Column = column, DataItem = DataContext, OwningRow = this, Width = column.GetEffectiveWidth() };
                cell.ContentControl = column.GenerateElement(cell, DataContext);
                cell.EnsureBuilt();
                _cells.Add(cell); LogicalChildren.Add(cell); VisualChildren.Add(cell);
            }
        }
        for (int ci = 0; ci < columns.Count; ci++)
        {
            if (!columns[ci].IsVisible) continue;
            if (ci < leftFrozen || ci >= totalCount - rightFrozen)
            {
                var column = columns[ci];
                var cell = new DataGridCell { Column = column, DataItem = DataContext, OwningRow = this, Width = column.GetEffectiveWidth() };
                cell.ContentControl = column.GenerateElement(cell, DataContext);
                cell.EnsureBuilt();
                _cells.Add(cell); LogicalChildren.Add(cell); VisualChildren.Add(cell);
            }
        }

        RefreshVisuals();
    }

    protected override void OnPointerEntered(PointerEventArgs e)
    {
        base.OnPointerEntered(e);
        _isPointerOver = true;
        _dragHandle.InvalidateVisual();
        RefreshVisuals();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _isPointerOver = false;
        _dragHandle.InvalidateVisual();
        RefreshVisuals();
    }

    internal void SetDragVisualState(bool isDragging)
    {
        _isDragging = isDragging;
        RefreshVisuals();
        InvalidateVisual();
    }

    public void RefreshVisuals()
    {
        foreach (var cell in _cells)
        {
            if (cell == null) continue;
            cell.UpdateBg();
        }
    }

    public void RefreshSearchHighlights()
    {
        foreach (var cell in _cells)
            cell.UpdateSearchHighlight();
    }
}

