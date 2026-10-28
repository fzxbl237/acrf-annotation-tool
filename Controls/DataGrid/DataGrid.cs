﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Text;
using System.Net;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using AtomLineEdit = AtomUI.Desktop.Controls.LineEdit;
using AtomButton = AtomUI.Desktop.Controls.Button;
using AtomIconButton = AtomUI.Desktop.Controls.IconButton;
using AtomUI.Icons.AntDesign;
using Avalonia.Threading;
using PatChes.Controls.DataGrid.Searching;

namespace PatChes.Controls.DataGrid;

public enum DataGridGridLinesVisibility
{
    None,
    Horizontal,
    Vertical,
    Both
}

public enum DataGridSelectionUnit
{
    FullRow,
    Cell
}

public enum DataGridSelectionMode
{
    Single,
    Extended
}

public class DataGrid : TemplatedControl
{
    // Change sets retain row identity and explicit accessors, never virtualized row indices.
    private sealed record CellChange(object Item, DataGridBoundColumn.ItemProperty Property, object? Before, object? After);
    private sealed record HistoryEntry(List<CellChange> Cells, Action? Undo = null, Action? Redo = null, object? RestoredItem = null);
    private readonly LinkedList<HistoryEntry> _past = new();
    private readonly Stack<HistoryEntry> _future = new();
    private List<CellChange>? _pendingChanges;
    private CellChange? _editingSnapshot;
    private bool _replayingHistory;
    private bool _canUndo;
    private bool _canRedo;
    private AtomIconButton? _undoButton;
    private AtomIconButton? _redoButton;
    private AtomIconButton? _errorFilterButton;
    private ErrorFilterIcon? _errorFilterIcon;
    private bool _showErrorsOnly;
    private bool _errorFilterRefreshQueued;
    private readonly HashSet<INotifyDataErrorInfo> _errorFilterItems = new(ReferenceEqualityComparer.Instance);

    private void ToggleErrorFilter()
    {
        EndEditBeforeLayoutChange();
        _showErrorsOnly = !_showErrorsOnly;
        if (_errorFilterIcon != null) _errorFilterIcon.IsActive = _showErrorsOnly;
        if (_errorFilterButton != null)
        {
            AtomUI.Desktop.Controls.ToolTip.SetTip(_errorFilterButton,
                _showErrorsOnly ? "取消错误行筛选" : "仅显示有错误的行");
            Avalonia.Automation.AutomationProperties.SetName(_errorFilterButton,
                _showErrorsOnly ? "取消错误行筛选" : "仅显示有错误的行");
        }
        SyncErrorFilterSubscriptions();
        ApplyFilterChange();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        SyncErrorFilterSubscriptions();
        if (_showErrorsOnly) ApplyFilterChange();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        foreach (var item in _errorFilterItems) item.ErrorsChanged -= OnErrorFilterItemChanged;
        _errorFilterItems.Clear();
        base.OnDetachedFromVisualTree(e);
    }

    private bool HasRowError(object item)
    {
        if (item is not INotifyDataErrorInfo errors || !errors.HasErrors) return false;
        foreach (var path in Columns.Select(GetColumnBindingPath).Where(path => !string.IsNullOrWhiteSpace(path))
                     .Prepend(null).Distinct())
        {
            if (errors.GetErrors(path) is { } values &&
                ValidationUtil.GetValidationSeverity(ValidationUtil.CreateValidationExceptions(values)) >= DataGridValidationSeverity.Error)
                return true;
        }
        return false;
    }

    private void SyncErrorFilterSubscriptions()
    {
        var items = _showErrorsOnly && TopLevel.GetTopLevel(this) != null && ItemsSource != null
            ? new HashSet<INotifyDataErrorInfo>(ItemsSource.OfType<INotifyDataErrorInfo>(), ReferenceEqualityComparer.Instance)
            : new HashSet<INotifyDataErrorInfo>(ReferenceEqualityComparer.Instance);
        foreach (var item in _errorFilterItems.ToArray())
            if (!items.Contains(item))
            {
                item.ErrorsChanged -= OnErrorFilterItemChanged;
                _errorFilterItems.Remove(item);
            }
        foreach (var item in items)
            if (_errorFilterItems.Add(item)) item.ErrorsChanged += OnErrorFilterItemChanged;
    }

    private void OnErrorFilterItemChanged(object? sender, DataErrorsChangedEventArgs e)
    {
        if (_errorFilterRefreshQueued) return;
        _errorFilterRefreshQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _errorFilterRefreshQueued = false;
            if (_showErrorsOnly && TopLevel.GetTopLevel(this) != null) ApplyFilterChange();
        }, DispatcherPriority.Background);
    }

    public static readonly StyledProperty<int> UndoLimitProperty =
        AvaloniaProperty.Register<DataGrid, int>(nameof(UndoLimit), 100, coerce: (_, value) => Math.Max(0, value));
    public static readonly DirectProperty<DataGrid, bool> CanUndoProperty =
        AvaloniaProperty.RegisterDirect<DataGrid, bool>(nameof(CanUndo), grid => grid.CanUndo);
    public static readonly DirectProperty<DataGrid, bool> CanRedoProperty =
        AvaloniaProperty.RegisterDirect<DataGrid, bool>(nameof(CanRedo), grid => grid.CanRedo);

    public int UndoLimit { get => GetValue(UndoLimitProperty); set => SetValue(UndoLimitProperty, value); }
    public bool CanUndo => _canUndo;
    public bool CanRedo => _canRedo;

    private sealed class HistoryScope(DataGrid grid) : IDisposable
    {
        public void Dispose()
        {
            var changes = grid._pendingChanges;
            grid._pendingChanges = null;
            if (changes is { Count: > 0 })
                grid.PushHistory(changes);
        }
    }

    private IDisposable BeginHistory()
    {
        _pendingChanges = new List<CellChange>();
        return new HistoryScope(this);
    }

    private void RecordChange(object item, DataGridBoundColumn.ItemProperty property, object? before)
    {
        var after = property.GetValue(item);
        if (_replayingHistory || Equals(before, after)) return;
        var change = new CellChange(item, property, before, after);
        if (_pendingChanges != null) _pendingChanges.Add(change);
        else PushHistory(new List<CellChange> { change });
    }

    private void WriteValue(object item, DataGridBoundColumn.ItemProperty property, object? value)
    {
        var before = property.GetValue(item);
        property.SetValue(item, value);
        RecordChange(item, property, before);
    }

    private void PushHistory(List<CellChange> changes) => PushHistory(new HistoryEntry(changes));

    private void PushHistory(HistoryEntry entry)
    {
        _future.Clear();
        if (UndoLimit > 0) _past.AddLast(entry);
        TrimHistory();
    }

    // The owner performs collection and domain-specific changes; they share the cell-edit timeline.
    public void RecordUndoableAction(Action undo, Action redo, object? restoredItem = null)
    {
        if (_replayingHistory) return;
        PushHistory(new HistoryEntry(new List<CellChange>(), undo, redo, restoredItem));
    }

    private void TrimHistory()
    {
        while (_past.Count > UndoLimit) _past.RemoveFirst();
        if (UndoLimit == 0) _future.Clear();
        UpdateUndoState();
    }

    private void UpdateUndoState()
    {
        SetAndRaise(CanUndoProperty, ref _canUndo, _past.Count > 0);
        SetAndRaise(CanRedoProperty, ref _canRedo, _future.Count > 0);
        if (_undoButton != null) _undoButton.IsEnabled = CanUndo && !IsReadOnly;
        if (_redoButton != null) _redoButton.IsEnabled = CanRedo && !IsReadOnly;
    }

    public void ClearHistory()
    {
        _past.Clear();
        _future.Clear();
        UpdateUndoState();
    }

    public void Undo() => ReplayHistory(true);
    public void Redo() => ReplayHistory(false);

    private void ReplayHistory(bool undo)
    {
        if (IsReadOnly) return;
        CancelEdit();
        var entry = undo ? _past.Last?.Value : _future.Count > 0 ? _future.Peek() : null;
        if (entry == null) return;
        var changes = entry.Cells;
        var appliedChanges = new List<CellChange>();
        _replayingHistory = true;
        try
        {
            // Domain actions mutate values behind our back (drag, restore); snapshot the bound
            // columns so those edits still identify the cells they changed.
            var beforeAction = entry.Undo != null ? SnapshotBoundValues(entry.RestoredItem) : null;
            (undo ? entry.Undo : entry.Redo)?.Invoke();
            InvalidateItemsCache();
            if (beforeAction != null)
            {
                foreach (var (item, property, before) in beforeAction)
                {
                    if (FindItemIndex(item) < 0) continue;
                    var after = property.GetValue(item);
                    if (!Equals(before, after)) appliedChanges.Add(new CellChange(item, property, before, after));
                }
            }
            foreach (var change in undo ? changes.AsEnumerable().Reverse() : changes)
            {
                if (FindItemIndex(change.Item) >= 0)
                {
                    var before = change.Property.GetValue(change.Item);
                    change.Property.SetValue(change.Item, undo ? change.Before : change.After);
                    if (!Equals(before, change.Property.GetValue(change.Item)))
                        appliedChanges.Add(change);
                }
            }
        }
        catch
        {
            // A rejecting model setter invalidates the remaining replay assumptions.
            ClearHistory();
            throw;
        }
        finally
        {
            _replayingHistory = false;
        }
        if (undo)
        {
            _past.RemoveLast();
            _future.Push(entry);
        }
        else
        {
            _future.Pop();
            _past.AddLast(entry);
        }
        TrimHistory();
        foreach (var row in _realizedRows)
            foreach (var cell in row.Cells)
                cell.ResetDisplay();
        RefreshSearchResults();
        UpdateReplaceStatus();
        RevealHistoryChanges(appliedChanges);
        if (entry.Undo != null)
        {
            ClearSelectedCells();
            ClearPendingCurrentCellRestore();
            _currentCell = null;
            _currentRow = null;
            if (undo && entry.RestoredItem is { } item && FindItemIndex(item) is var index && index >= 0)
            {
                SelectedItem = item;
                SelectedIndex = index;
                ScrollIntoView(item);
                var column = Columns.Select((value, i) => (value, i)).FirstOrDefault(pair => pair.value.IsVisible);
                if (column.value != null) FocusCell(index, column.i);
                else Focus();
            }
            else
            {
                SelectedIndex = SelectedItem is { } selected ? FindItemIndex(selected) : -1;
                Focus();
            }
        }
        // Queue after all history-specific scrolling. The target row is then realized before the
        // overlay starts, including a restored row that was outside the viewport.
        ScheduleHistoryFlash(entry, appliedChanges);
    }

    private HashSet<DataGridCellPosition> GetHistoryChangePositions(IReadOnlyList<CellChange> changes)
    {
        var items = GetItemsList();
        var rowIndices = new Dictionary<object, int>(ReferenceEqualityComparer.Instance);
        for (var row = 0; row < items.Count; row++)
            if (items[row] is { } item) rowIndices.TryAdd(item, row);

        var positions = new HashSet<DataGridCellPosition>();
        foreach (var change in changes)
        {
            if (!rowIndices.TryGetValue(change.Item, out var row)) continue;
            for (var column = 0; column < Columns.Count; column++)
            {
                if (Columns[column] is DataGridBoundColumn { IsVisible: true, BindingPath: { } path } &&
                    ReferenceEquals(DataGridBoundColumn.GetItemProperty(change.Item, path), change.Property))
                    positions.Add(new DataGridCellPosition(row, column));
            }
        }
        return positions;
    }

    private void RevealHistoryChanges(IReadOnlyList<CellChange> changes)
    {
        if (SelectionUnit != DataGridSelectionUnit.Cell || changes.Count == 0) return;

        // Resolve after search refresh: history retains item identity, not filtered row indices.
        var items = GetItemsList();
        var selection = GetHistoryChangePositions(changes);
        if (selection.Count == 0) return;

        var anchor = selection.OrderBy(position => position.RowIndex).ThenBy(position => position.ColumnIndex).First();
        ClearPendingCurrentCellRestore();
        _currentCell = null;
        _currentRow = null;
        SelectedIndex = anchor.RowIndex;
        SelectedItem = items[anchor.RowIndex];
        EnsureRowVisible(anchor.RowIndex);
        EnsureHistoryColumnVisible(anchor.ColumnIndex);
        UpdateScrollBars();
        SyncHorizontalOffset();
        UpdateViewport();

        _cellSelectionAnchor = anchor;
        ReplaceCellSelection(selection);
        _selectionOverlay?.SelectionChanged();
        _currentRow = _realizedRows.FirstOrDefault(row => row.Index == anchor.RowIndex);
        _currentCell = _currentRow?.Cells.FirstOrDefault(cell => ReferenceEquals(cell.Column, Columns[anchor.ColumnIndex]));
        if (_currentCell != null) _currentCell.Focus();
        else Focus();
    }

    // The wash is independent of cell selection: it also covers full-row mode, undo of a delete and
    // undo of a moved annotation, where the change is a restored record rather than a bound value.
    private void ScheduleHistoryFlash(HistoryEntry entry, IReadOnlyList<CellChange> changes)
    {
        Dispatcher.UIThread.Post(() => FlashHistoryChanges(entry, changes), DispatcherPriority.Render);
    }

    private void FlashHistoryChanges(HistoryEntry entry, IReadOnlyList<CellChange> changes)
    {
        if (SelectionUnit == DataGridSelectionUnit.Cell && _selectionOverlay is { } overlay)
        {
            // Resolve by item/property instead of realized controls. The target row may have been
            // outside the viewport when the replay started, but RevealHistoryChanges has already
            // scrolled it into view before this callback runs.
            var positions = GetHistoryChangePositions(changes);
            if (positions.Count == 0 && entry.RestoredItem is { } restored)
            {
                var items = GetItemsList();
                var row = items.IndexOf(restored);
                var column = Columns
                    .Select((value, index) => (value, index))
                    .FirstOrDefault(pair => pair.value is DataGridBoundColumn { IsVisible: true });
                if (row >= 0 && column.value != null)
                    positions.Add(new DataGridCellPosition(row, column.index));
            }

            if (positions.Count > 0)
            {
                // Draw the wash in the grid overlay so adjacent cells share one continuous
                // surface and the normal cell separators cannot show through.
                overlay.Flash(positions);
                return;
            }
        }

        var flashed = new HashSet<DataGridCell>();
        foreach (var change in changes)
        {
            foreach (var row in _realizedRows)
            {
                if (!ReferenceEquals(row.DataContext, change.Item)) continue;
                foreach (var cell in row.Cells)
                {
                    if (cell.Column is DataGridBoundColumn { BindingPath: { } path } &&
                        ReferenceEquals(DataGridBoundColumn.GetItemProperty(change.Item, path), change.Property))
                        flashed.Add(cell);
                }
            }
        }
        foreach (var cell in flashed) cell.FlashHistoryChange();
    }

    // Items touched by a domain undo action: the restored row, the selection, or both.
    private List<(object Item, DataGridBoundColumn.ItemProperty Property, object? Before)> SnapshotBoundValues(object? item)
    {
        var result = new List<(object, DataGridBoundColumn.ItemProperty, object?)>();
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        foreach (var candidate in new[] { item, SelectedItem })
        {
            if (candidate is null || !seen.Add(candidate)) continue;
            foreach (var column in Columns)
            {
                if (column is not DataGridBoundColumn { IsVisible: true, BindingPath: { } path }) continue;
                var property = DataGridBoundColumn.GetItemProperty(candidate, path);
                if (property?.CanRead != true) continue;
                result.Add((candidate, property, property.GetValue(candidate)));
            }
        }
        return result;
    }

    private void EnsureHistoryColumnVisible(int columnIndex)
    {
        if (IsColumnFrozen(columnIndex)) return;
        var left = GetLeftFrozenWidth() + RowDragHandleOffset;
        var right = GetViewportWidth() - GetRightFrozenWidth();
        if (right <= left) return;
        var columnLeft = RowDragHandleOffset;
        for (var column = 0; column < columnIndex; column++)
            columnLeft += Columns[column].GetEffectiveWidth();
        var width = Columns[columnIndex].GetEffectiveWidth();
        var visibleLeft = columnLeft - _horizontalOffset;
        if (visibleLeft < left || width > right - left)
            _horizontalOffset = Math.Max(0, columnLeft - left);
        else if (visibleLeft + width > right)
            _horizontalOffset = Math.Max(0, columnLeft + width - right);
    }

    private bool _viewportUpdateScheduled;
    private int _realizedFirstIndex = -1;
    private int _realizedLastIndex = -1;
    private DataGridScrollPanel? _scrollPanel;
    private DataGridColumnHeadersPresenter? _headersPresenter;
    private Border? _headerClipper;
    private ScrollBar? _vScrollBar;
    private ScrollBar? _hScrollBar;
    private bool _updatingScrollBars;
    private readonly List<DataGridRow> _realizedRows = new();
    private readonly List<DataGridRow> _rowPool = new();
    private const int MaxRowPoolSize = 128;
    private DataGridRow? _currentRow;
    private DataGridCell? _currentCell;
    private int? _pendingCurrentCellRowIndex;
    private DataGridColumn? _pendingCurrentCellColumn;
    private bool _pendingCurrentCellHadFocus;
    private int _selectionAnchorIndex = -1;
    private readonly HashSet<DataGridCellPosition> _selectedCells = new();
    private DataGridCellPosition? _cellSelectionAnchor;
    private bool _isCellSelectionDragging;
    private bool _cellSelectionDidDrag;
    private bool _cellSelectionAppend;
    private HashSet<DataGridCellPosition>? _dragSelectionBase;
    private DataGridSelectionOverlay? _selectionOverlay;
    private Point _cellSelectionStartPoint;
    private IPointer? _cellSelectionPointer;
    private DataGridCellPosition? _lastDragSelectionTarget;
    private DataGridCell? _lastDragSelectionCell;
    private object? _lastDragSelectionItem;
    private long _cellSelectionRevision;
    private long _lastDragSelectionRevision = -1;
    private bool _cellSelectionVisualsDirty = true;
    private bool _isEditing;
    private bool _templateApplied;
    private double _verticalOffset;
    private double _horizontalOffset;
    private double _lastArrangeHeight;
    private bool _itemsDirty = true;
    private List<object>? _cachedItems;
    private TextBlock? _statusBar;
    private Border? _emptyState;
    private TextBlock? _emptyTitle;
    private AtomButton? _emptyClearButton;
    private DataGridRow? _draggedRow;
    private DataGridRow? _dragPreviewRow;
    private RowDragAnimation? _dragPreviewAnimation;
    private double? _dragPreviewVisualY;
    private double _dragPreviewTargetY;
    private Control? _rowDragFeedback;
    private Point _rowDragStartPosition;
    private Point _rowDragPosition;
    private bool _isRowDragging;
    private int _rowDropIndex = -1;
    private const double RowDragHandleWidth = 36;
    private static readonly TimeSpan RowDragAnimationDuration = TimeSpan.FromMilliseconds(360);
    private static readonly TimeSpan RowDragAnimationInterval = TimeSpan.FromMilliseconds(16);
    private readonly Dictionary<DataGridRow, RowDragAnimation> _rowDragAnimations = new();
    private DispatcherTimer? _rowDragAnimationTimer;

    private sealed class RowDragAnimation
    {
        public double StartOffset { get; init; }
        public double TargetOffset { get; init; }
        public DateTime StartTime { get; init; }
    }

    // Validation tracking
    private readonly HashSet<INotifyDataErrorInfo> _validationTrackedItems = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<INotifyDataErrorInfo> _validationItemsWithError = new(ReferenceEqualityComparer.Instance);
    private bool _isValid = true;

    // Search support
    private SearchModel? _searchModel;
    private DataGridSearchAdapter? _searchAdapter;
    private AtomLineEdit? _searchTextBox;
    private DispatcherTimer? _searchDebounceTimer;
    private string? _pendingSearchQuery;
    private const int SearchDebounceMilliseconds = 200;
    private TextBlock? _searchCountText;
    private Border? _searchBarBorder;
    private AtomLineEdit? _replaceFindTextBox;
    private AtomLineEdit? _replaceTextBox;
    private TextBlock? _replaceStatusText;
    private Window? _replaceDialog;
    private bool _replaceMode;
    private readonly List<ReplaceCellTarget> _replaceScope = new();
    private int _replaceCurrentIndex;
    private bool _usesExternalSearchModel;
    private bool _synchronizingSearchText;
    private SearchDescriptor[]? _clearedSearchDescriptors;

    private sealed record ReplaceCellTarget(object Item, int ColumnIndex);
    private sealed record ReplaceMatch(ReplaceCellTarget Target, DataGridBoundColumn.ItemProperty Property, string Text);

    public static readonly StyledProperty<bool> IsSearchBarVisibleProperty =
        AvaloniaProperty.Register<DataGrid, bool>(nameof(IsSearchBarVisible), true);

    public static readonly StyledProperty<SearchHighlightMode> SearchHighlightModeProperty =
        AvaloniaProperty.Register<DataGrid, SearchHighlightMode>(nameof(SearchHighlightMode), SearchHighlightMode.Cell);
    public static readonly StyledProperty<SearchModel?> SearchModelProperty =
        AvaloniaProperty.Register<DataGrid, SearchModel?>(nameof(SearchModel));
    public static readonly StyledProperty<object?> SearchBarRightContentProperty =
        AvaloniaProperty.Register<DataGrid, object?>(nameof(SearchBarRightContent));

    public bool IsSearchBarVisible { get => GetValue(IsSearchBarVisibleProperty); set => SetValue(IsSearchBarVisibleProperty, value); }
    public SearchHighlightMode SearchHighlightMode { get => GetValue(SearchHighlightModeProperty); set => SetValue(SearchHighlightModeProperty, value); }
    public SearchModel? SearchModel { get => GetValue(SearchModelProperty); set => SetValue(SearchModelProperty, value); }
    public object? SearchBarRightContent { get => GetValue(SearchBarRightContentProperty); set => SetValue(SearchBarRightContentProperty, value); }

    public static readonly StyledProperty<string?> EmptyMessageProperty =
        AvaloniaProperty.Register<DataGrid, string?>(nameof(EmptyMessage));

    public string? EmptyMessage { get => GetValue(EmptyMessageProperty); set => SetValue(EmptyMessageProperty, value); }

    private bool HasActiveSearch => _searchModel?.Descriptors.Any(descriptor =>
        !string.IsNullOrEmpty(descriptor.Query) || descriptor.AllowEmpty) == true;

    public static readonly StyledProperty<IEnumerable?> ItemsSourceProperty =
        AvaloniaProperty.Register<DataGrid, IEnumerable?>(nameof(ItemsSource));
    public static readonly StyledProperty<object?> SelectedItemProperty =
        AvaloniaProperty.Register<DataGrid, object?>(nameof(SelectedItem));
    public static readonly StyledProperty<int> SelectedIndexProperty =
        AvaloniaProperty.Register<DataGrid, int>(nameof(SelectedIndex), -1);
    public static readonly StyledProperty<double> RowHeightProperty =
        AvaloniaProperty.Register<DataGrid, double>(nameof(RowHeight), 28);
    public static readonly StyledProperty<double> ColumnHeaderHeightProperty =
        AvaloniaProperty.Register<DataGrid, double>(nameof(ColumnHeaderHeight), 30);
    public static readonly StyledProperty<IBrush?> HeaderBackgroundProperty =
        AvaloniaProperty.Register<DataGrid, IBrush?>(nameof(HeaderBackground),
            new SolidColorBrush(Color.Parse("#fafafb")));
    public static readonly StyledProperty<IBrush?> RowBackgroundProperty =
        AvaloniaProperty.Register<DataGrid, IBrush?>(nameof(RowBackground),
            new SolidColorBrush(Colors.White));
    public static readonly StyledProperty<IBrush?> AlternatingRowBackgroundProperty =
        AvaloniaProperty.Register<DataGrid, IBrush?>(nameof(AlternatingRowBackground),
            new SolidColorBrush(Color.Parse("#F8F8F8")));
    public static readonly StyledProperty<IBrush?> RowHoverBackgroundProperty =
        AvaloniaProperty.Register<DataGrid, IBrush?>(nameof(RowHoverBackground),
            new SolidColorBrush(Color.Parse("#F5FAFF")));
    public static readonly StyledProperty<int> LeftFrozenColumnCountProperty =
        AvaloniaProperty.Register<DataGrid, int>(nameof(LeftFrozenColumnCount), 0);
    public static readonly StyledProperty<int> RightFrozenColumnCountProperty =
        AvaloniaProperty.Register<DataGrid, int>(nameof(RightFrozenColumnCount), 0);
    public static readonly StyledProperty<bool> IsReadOnlyProperty =
        AvaloniaProperty.Register<DataGrid, bool>(nameof(IsReadOnly));
    public static readonly StyledProperty<DataGridGridLinesVisibility> GridLinesVisibilityProperty =
        AvaloniaProperty.Register<DataGrid, DataGridGridLinesVisibility>(nameof(GridLinesVisibility), DataGridGridLinesVisibility.Both);
    public static readonly StyledProperty<bool> CanUserReorderRowsProperty =
        AvaloniaProperty.Register<DataGrid, bool>(nameof(CanUserReorderRows));
    public static readonly StyledProperty<DataGridSelectionUnit> SelectionUnitProperty =
        AvaloniaProperty.Register<DataGrid, DataGridSelectionUnit>(nameof(SelectionUnit), DataGridSelectionUnit.FullRow);
    public static readonly StyledProperty<DataGridSelectionMode> SelectionModeProperty =
        AvaloniaProperty.Register<DataGrid, DataGridSelectionMode>(nameof(SelectionMode), DataGridSelectionMode.Single);
    public static readonly StyledProperty<IDataTemplate?> RowDragFeedbackTemplateProperty =
        AvaloniaProperty.Register<DataGrid, IDataTemplate?>(nameof(RowDragFeedbackTemplate));

    public IEnumerable? ItemsSource { get => GetValue(ItemsSourceProperty); set => SetValue(ItemsSourceProperty, value); }
    public object? SelectedItem { get => GetValue(SelectedItemProperty); set => SetValue(SelectedItemProperty, value); }
    public int SelectedIndex { get => GetValue(SelectedIndexProperty); set => SetValue(SelectedIndexProperty, value); }
    public double RowHeight { get => GetValue(RowHeightProperty); set => SetValue(RowHeightProperty, value); }
    public double ColumnHeaderHeight { get => GetValue(ColumnHeaderHeightProperty); set => SetValue(ColumnHeaderHeightProperty, value); }
    public IBrush? HeaderBackground { get => GetValue(HeaderBackgroundProperty); set => SetValue(HeaderBackgroundProperty, value); }
    public IBrush? RowBackground { get => GetValue(RowBackgroundProperty); set => SetValue(RowBackgroundProperty, value); }
    public IBrush? AlternatingRowBackground { get => GetValue(AlternatingRowBackgroundProperty); set => SetValue(AlternatingRowBackgroundProperty, value); }
    public IBrush? RowHoverBackground { get => GetValue(RowHoverBackgroundProperty); set => SetValue(RowHoverBackgroundProperty, value); }
    public int LeftFrozenColumnCount { get => GetValue(LeftFrozenColumnCountProperty); set => SetValue(LeftFrozenColumnCountProperty, value); }
    public int RightFrozenColumnCount { get => GetValue(RightFrozenColumnCountProperty); set => SetValue(RightFrozenColumnCountProperty, value); }
    public bool IsReadOnly { get => GetValue(IsReadOnlyProperty); set => SetValue(IsReadOnlyProperty, value); }
    public DataGridGridLinesVisibility GridLinesVisibility { get => GetValue(GridLinesVisibilityProperty); set => SetValue(GridLinesVisibilityProperty, value); }
    public bool CanUserReorderRows { get => GetValue(CanUserReorderRowsProperty); set => SetValue(CanUserReorderRowsProperty, value); }
    public DataGridSelectionUnit SelectionUnit { get => GetValue(SelectionUnitProperty); set => SetValue(SelectionUnitProperty, value); }
    public DataGridSelectionMode SelectionMode { get => GetValue(SelectionModeProperty); set => SetValue(SelectionModeProperty, value); }
    public IDataTemplate? RowDragFeedbackTemplate { get => GetValue(RowDragFeedbackTemplateProperty); set => SetValue(RowDragFeedbackTemplateProperty, value); }

    public ObservableCollection<DataGridColumn> Columns { get; } = new();
    public IReadOnlyCollection<DataGridCellPosition> SelectedCells => _selectedCells;
    public DataGridRow? CurrentRow => _currentRow;
    internal DataGridRow? DragPreviewRow => _dragPreviewRow;
    public DataGridCell? CurrentCell => _currentCell;
    public bool IsEditing => _isEditing;
    internal double HorizontalOffset => _horizontalOffset;
    internal double VerticalOffset => _verticalOffset;
    internal DataGridColumnHeadersPresenter? HeadersPresenter => _headersPresenter;
    internal double RowDragHandleOffset => CanUserReorderRows ? RowDragHandleWidth : 0;

    internal double GetLeftFrozenWidth()
    {
        double w = 0;
        int stop = Math.Min(LeftFrozenColumnCount, Columns.Count);
        for (int i = 0; i < stop; i++)
            w += Columns[i].GetEffectiveWidth();
        return w;
    }

    internal double GetRightFrozenWidth()
    {
        double w = 0;
        int start = Math.Max(0, Columns.Count - RightFrozenColumnCount);
        for (int i = start; i < Columns.Count; i++)
            w += Columns[i].GetEffectiveWidth();
        return w;
    }

    internal bool IsColumnFrozen(int columnIndex)
    {
        return columnIndex < LeftFrozenColumnCount
            || columnIndex >= Columns.Count - RightFrozenColumnCount;
    }

    public static readonly RoutedEvent<DataGridPreparingCellForEditEventArgs> PreparingCellForEditEvent =
        RoutedEvent.Register<DataGrid, DataGridPreparingCellForEditEventArgs>(
            nameof(PreparingCellForEdit), RoutingStrategies.Bubble);

    public event EventHandler<DataGridColumnEventArgs>? ColumnHeaderClick;
    public event EventHandler? SelectionChanged;
    public event EventHandler? SelectedCellsChanged;
    public event EventHandler<DataGridPreparingCellForEditEventArgs> PreparingCellForEdit
    {
        add => AddHandler(PreparingCellForEditEvent, value);
        remove => RemoveHandler(PreparingCellForEditEvent, value);
    }
    public event EventHandler<DataGridCellEditEndingEventArgs>? CellEditEnding;
    public event EventHandler<DataGridRowReorderedEventArgs>? RowReordered;

    public bool IsValid
    {
        get => _isValid;
        internal set
        {
            _isValid = value;
            PseudoClasses.Set(":invalid", !value);
        }
    }

    protected override Type StyleKeyOverride => typeof(DataGrid);

    public DataGrid()
    {
        Columns.CollectionChanged += OnColumnsChanged;
        PointerMoved += OnCellSelectionPointerMoved;
        PointerReleased += OnCellSelectionPointerReleased;
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        EnsureDefaultVisualTree();
    }

    private Border BuildSearchBar()
    {
        _usesExternalSearchModel = SearchModel != null;
        AttachSearchModel(SearchModel ?? new SearchModel
        {
            HighlightMode = SearchHighlightMode,
        });

        _searchTextBox = new AtomLineEdit
        {
            PlaceholderText = "搜索...",
            Width = 240,
            Margin = new Thickness(4),
            VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Center,
            FontSize = 13,
            IsAllowClear = true
        };

        SynchronizeSearchText();
        _searchTextBox.TextChanged += OnSearchTextChanged;
        _searchTextBox.KeyDown += OnSearchKeyDown;

        var prevBtn = new AtomIconButton
        {
            Icon = new ArrowUpOutlined(),
            Width = 26,
            Height = 26,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            FontSize = 14,
            Margin = new Thickness(2, 0),
            Padding = new Thickness(0),
            MinWidth = 0,
        };
        prevBtn.Click += (_, _) => _searchModel?.MovePrevious();

        var nextBtn = new AtomIconButton
        {
            Icon = new ArrowDownOutlined(),
            Width = 26,
            Height = 26,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            FontSize = 14,
            Margin = new Thickness(2, 0),
            Padding = new Thickness(0),
            MinWidth = 0,
        };
        nextBtn.Click += (_, _) => _searchModel?.MoveNext();

        _searchCountText = new TextBlock
        {
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.Parse("#666666")),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
        };

        var searchControls = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
        searchControls.Children.Add(_searchTextBox);
        searchControls.Children.Add(prevBtn);
        searchControls.Children.Add(nextBtn);
        searchControls.Children.Add(_searchCountText);
        _undoButton = new AtomIconButton
        {
            Icon = new UndoOutlined(),
            IconWidth = 18,
            IconHeight = 18,
            Foreground = new SolidColorBrush(Color.Parse("#353535")),
            Width = 26,
            Height = 26,
            Margin = new Thickness(8, 0, 2, 0),
            Padding = new Thickness(0),
            MinWidth = 0,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Focusable = false
        };
        _redoButton = new AtomIconButton
        {
            Icon = new RedoOutlined(),
            IconWidth = 18,
            IconHeight = 18,
            Foreground = new SolidColorBrush(Color.Parse("#353535")),
            Width = 26,
            Height = 26,
            Margin = new Thickness(2, 0),
            Padding = new Thickness(0),
            MinWidth = 0,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Focusable = false
        };
        AtomUI.Desktop.Controls.ToolTip.SetTip(_undoButton, "撤销 (Ctrl+Z)");
        AtomUI.Desktop.Controls.ToolTip.SetTip(_redoButton, "重做 (Ctrl+Y / Ctrl+Shift+Z)");
        Avalonia.Automation.AutomationProperties.SetName(_undoButton, "撤销");
        Avalonia.Automation.AutomationProperties.SetName(_redoButton, "重做");
        _undoButton.Click += (_, _) => Undo();
        _redoButton.Click += (_, _) => Redo();
        searchControls.Children.Add(_undoButton);
        searchControls.Children.Add(_redoButton);
        _errorFilterIcon = new ErrorFilterIcon { IsActive = _showErrorsOnly };
        _errorFilterButton = new AtomIconButton
        {
            Icon = _errorFilterIcon,
            IconWidth = 20,
            IconHeight = 20,
            Width = 28,
            Height = 28,
            Margin = new Thickness(6, 0, 2, 0),
            Padding = new Thickness(0),
            MinWidth = 0,
            Focusable = false,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
        };
        AtomUI.Desktop.Controls.ToolTip.SetTip(_errorFilterButton, "仅显示有错误的行");
        Avalonia.Automation.AutomationProperties.SetName(_errorFilterButton, "仅显示有错误的行");
        _errorFilterButton.Click += (_, _) => ToggleErrorFilter();
        searchControls.Children.Add(_errorFilterButton);
        UpdateUndoState();

        var content = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
        content.Children.Add(searchControls);

        var rightContent = new ContentControl
        {
            Content = SearchBarRightContent,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
        Grid.SetColumn(rightContent, 1);
        content.Children.Add(rightContent);

        return new Border
        {
            //Background = new SolidColorBrush(Color.Parse("#F5F5F5")),
            Background = new SolidColorBrush(Color.Parse("#FFFFFF")),
            BorderBrush = new SolidColorBrush(Color.Parse("#E0E0E0")),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(6),
            Margin = new Thickness(0, 8, 8, 6),
            Padding = new Thickness(4, 2),
            Child = content,
        };
    }

    private void AttachSearchModel(SearchModel searchModel)
    {
        if (ReferenceEquals(_searchModel, searchModel))
        {
            SynchronizeSearchText();
            return;
        }

        if (_searchModel != null)
        {
            _searchModel.ResultsChanged -= OnSearchResultsChanged;
            _searchModel.CurrentChanged -= OnSearchCurrentChanged;
            _searchModel.SearchChanged -= OnSearchModelChanged;
        }
        _searchAdapter?.Dispose();
        _searchDebounceTimer?.Stop();
        _pendingSearchQuery = null;
        _clearedSearchDescriptors = null;

        _searchModel = searchModel;
        _searchAdapter = new DataGridSearchAdapter(_searchModel, this);
        _searchModel.ResultsChanged += OnSearchResultsChanged;
        _searchModel.CurrentChanged += OnSearchCurrentChanged;
        _searchModel.SearchChanged += OnSearchModelChanged;
        SynchronizeSearchText();
    }

    private void OnSearchModelChanged(object? sender, SearchChangedEventArgs e)
    {
        if (e.NewDescriptors.Count > 0)
            _clearedSearchDescriptors = null;
        SynchronizeSearchText();
    }

    private void SynchronizeSearchText()
    {
        if (_searchTextBox == null || _searchModel == null)
            return;

        var query = _searchModel.Descriptors.FirstOrDefault()?.Query ?? string.Empty;
        if (_searchTextBox.InnerRightContent is Control clearButton)
            clearButton.IsVisible = !string.IsNullOrWhiteSpace(query);
        if (string.Equals(_searchTextBox.Text, query, StringComparison.Ordinal))
            return;

        _synchronizingSearchText = true;
        try
        {
            _searchTextBox.Text = query;
        }
        finally
        {
            _synchronizingSearchText = false;
        }
    }

    private void UpdateSearchBarVisibility()
    {
        if (_searchBarBorder == null) return;
        _searchBarBorder.IsVisible = IsSearchBarVisible;
        if (!IsSearchBarVisible)
        {
            _searchModel?.Clear();
            _replaceMode = false;
            _replaceScope.Clear();
            _replaceCurrentIndex = 0;
        }
    }

    private void OpenFindBar(bool replaceMode)
    {
        IsSearchBarVisible = true;
        _searchTextBox?.Focus();
    }

    private async void OpenReplaceDialog()
    {
        if (_replaceDialog != null)
        {
            _replaceDialog.Activate();
            return;
        }

        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner == null)
            return;

        CommitEdit();
        _replaceMode = true;
        CaptureReplaceScope();

        _replaceFindTextBox = new AtomLineEdit
        {
            PlaceholderText = "查找内容",
            MinWidth = 280,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
        };
        _replaceTextBox = new AtomLineEdit
        {
            PlaceholderText = "替换为",
            MinWidth = 280,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            IsAllowClear = true,
        };
        _replaceStatusText = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.Parse("#666666")),
            Margin = new Thickness(0, 4, 0, 0),
        };

        _replaceFindTextBox.TextChanged += (_, _) => UpdateReplaceStatus();
        _replaceFindTextBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                ReplaceCurrentMatch();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                _replaceDialog?.Close();
                e.Handled = true;
            }
        };
        _replaceTextBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                ReplaceAllMatches();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                _replaceDialog?.Close();
                e.Handled = true;
            }
        };

        var replaceButton = new AtomButton { Content = "替换", MinWidth = 84 };
        replaceButton.Click += (_, _) => ReplaceCurrentMatch();
        var replaceAllButton = new AtomButton { Content = "全部替换", MinWidth = 84 };
        replaceAllButton.Click += (_, _) => ReplaceAllMatches();
        var closeButton = new AtomButton { Content = "关闭", MinWidth = 84 };
        closeButton.Click += (_, _) => _replaceDialog?.Close();

        var form = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto"),
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            RowSpacing = 8,
            ColumnSpacing = 10,
            Margin = new Thickness(18),
        };
        AddReplaceDialogRow(form, 0, "查找：", _replaceFindTextBox);
        AddReplaceDialogRow(form, 1, "替换：", _replaceTextBox);
        Grid.SetRow(_replaceStatusText, 2);
        Grid.SetColumn(_replaceStatusText, 1);
        form.Children.Add(_replaceStatusText);

        var buttons = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            Spacing = 8,
        };
        buttons.Children.Add(replaceButton);
        buttons.Children.Add(replaceAllButton);
        buttons.Children.Add(closeButton);
        Grid.SetRow(buttons, 3);
        Grid.SetColumn(buttons, 1);
        form.Children.Add(buttons);

        var dialog = new Window
        {
            Title = "查找和替换",
            Width = 440,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new Border
            {
                Background = new SolidColorBrush(Colors.White),
                Child = form,
            },
        };
        _replaceDialog = dialog;
        dialog.Closed += (_, _) =>
        {
            _replaceDialog = null;
            _replaceFindTextBox = null;
            _replaceTextBox = null;
            _replaceStatusText = null;
            _replaceMode = false;
            _replaceScope.Clear();
            _replaceCurrentIndex = 0;
        };
        dialog.Opened += (_, _) =>
        {
            _replaceFindTextBox?.Focus();
            UpdateReplaceStatus();
        };

        try
        {
            await dialog.ShowDialog(owner);
        }
        catch (InvalidOperationException)
        {
            dialog.Close();
        }
    }

    private static void AddReplaceDialogRow(Grid form, int row, string label, Control editor)
    {
        var labelControl = new TextBlock
        {
            Text = label,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
        Grid.SetRow(labelControl, row);
        Grid.SetColumn(labelControl, 0);
        form.Children.Add(labelControl);
        Grid.SetRow(editor, row);
        Grid.SetColumn(editor, 1);
        form.Children.Add(editor);
    }

    private void CloseFindBar()
    {
        IsSearchBarVisible = false;
        if (_searchTextBox != null) _searchTextBox.Text = string.Empty;
        if (_replaceTextBox != null) _replaceTextBox.Text = string.Empty;
    }

    private void CaptureReplaceScope()
    {
        _replaceScope.Clear();
        _replaceCurrentIndex = 0;
        if (SelectionUnit != DataGridSelectionUnit.Cell || _selectedCells.Count == 0)
            return;

        var items = GetItemsList();
        foreach (var position in _selectedCells.OrderBy(p => p.RowIndex).ThenBy(p => p.ColumnIndex))
        {
            if (position.RowIndex < 0 || position.RowIndex >= items.Count ||
                position.ColumnIndex < 0 || position.ColumnIndex >= Columns.Count)
                continue;
            _replaceScope.Add(new ReplaceCellTarget(items[position.RowIndex], position.ColumnIndex));
        }
    }

    private List<ReplaceMatch> GetReplaceMatches()
    {
        var query = _replaceFindTextBox?.Text;
        if (string.IsNullOrEmpty(query) || _replaceScope.Count == 0)
            return new List<ReplaceMatch>();

        var result = new List<ReplaceMatch>();
        foreach (var target in _replaceScope)
        {
            if (target.ColumnIndex < 0 || target.ColumnIndex >= Columns.Count)
                continue;
            var column = Columns[target.ColumnIndex];
            if (column.IsReadOnly || column is not DataGridBoundColumn boundColumn ||
                string.IsNullOrWhiteSpace(boundColumn.BindingPath))
                continue;

            var property = DataGridBoundColumn.GetItemProperty(target.Item, boundColumn.BindingPath);
            if (property?.CanRead != true || property.PropertyType == typeof(bool))
                continue;
            var text = property.GetValue(target.Item)?.ToString();
            if (string.IsNullOrEmpty(text))
                continue;
            if (text.IndexOf(query, StringComparison.CurrentCultureIgnoreCase) >= 0)
                result.Add(new ReplaceMatch(target, property, text));
        }
        return result;
    }

    private void UpdateReplaceStatus()
    {
        if (!_replaceMode || _replaceStatusText == null)
            return;
        var matches = GetReplaceMatches();
        _replaceCurrentIndex = matches.Count == 0 ? 0 : Math.Clamp(_replaceCurrentIndex, 0, matches.Count - 1);
        _replaceStatusText.Text = matches.Count == 0 ? "0 个匹配" : $"{matches.Count} 个匹配";
    }

    private void ReplaceCurrentMatch()
    {
        if (!_replaceMode) return;
        CommitEdit();
        var matches = GetReplaceMatches();
        if (matches.Count == 0)
        {
            UpdateReplaceStatus();
            return;
        }

        var match = matches[Math.Clamp(_replaceCurrentIndex, 0, matches.Count - 1)];
        var query = _replaceFindTextBox?.Text ?? string.Empty;
        var replacement = _replaceTextBox?.Text ?? string.Empty;
        var index = match.Text.IndexOf(query, StringComparison.CurrentCultureIgnoreCase);
        if (index >= 0 && TrySetReplacementValue(match, index, query.Length, replacement))
        {
            RefreshReplacedCell(match.Target);
            _replaceCurrentIndex = (_replaceCurrentIndex + 1) % matches.Count;
            RefreshSearchResults();
        }
        UpdateReplaceStatus();
    }

    private void ReplaceAllMatches()
    {
        if (!_replaceMode) return;
        CommitEdit();
        var matches = GetReplaceMatches();
        if (matches.Count == 0)
        {
            UpdateReplaceStatus();
            return;
        }

        var query = _replaceFindTextBox?.Text ?? string.Empty;
        var replacement = _replaceTextBox?.Text ?? string.Empty;
        var replaced = 0;
        using var history = BeginHistory();
        foreach (var match in matches)
        {
            var updated = ReplaceText(match.Text, query, replacement, out var count);
            if (count == 0 || !TrySetBoundValue(match.Target.Item, match.Property, updated))
                continue;
            replaced += count;
            RefreshReplacedCell(match.Target);
        }

        _replaceCurrentIndex = 0;
        RefreshSearchResults();
        if (_replaceStatusText != null)
            _replaceStatusText.Text = $"已替换 {replaced} 处";
    }

    private bool TrySetReplacementValue(ReplaceMatch match, int index, int length, string replacement)
    {
        var updated = match.Text.Remove(index, length).Insert(index, replacement);
        return TrySetBoundValue(match.Target.Item, match.Property, updated);
    }

    private static string ReplaceText(string text, string query, string replacement, out int count)
    {
        count = 0;
        if (string.IsNullOrEmpty(query)) return text;
        var builder = new System.Text.StringBuilder();
        var start = 0;
        while (start < text.Length)
        {
            var index = text.IndexOf(query, start, StringComparison.CurrentCultureIgnoreCase);
            if (index < 0) break;
            builder.Append(text, start, index - start);
            builder.Append(replacement);
            start = index + query.Length;
            count++;
        }
        if (count == 0) return text;
        builder.Append(text, start, text.Length - start);
        return builder.ToString();
    }

    private bool TrySetBoundValue(object item, DataGridBoundColumn.ItemProperty property, string text)
    {
        if (IsReadOnly || property.CanWrite != true) return false;
        if (!TryConvertClipboardValue(text, property.PropertyType, out var converted))
            return false;
        if (Equals(property.GetValue(item), converted)) return false;
        WriteValue(item, property, converted);
        return true;
    }

    private void RefreshReplacedCell(ReplaceCellTarget target)
    {
        var cell = _realizedRows
            .Where(row => ReferenceEquals(row.DataContext, target.Item))
            .SelectMany(row => row.Cells)
            .FirstOrDefault(candidate => Columns.IndexOf(candidate.Column!) == target.ColumnIndex);
        cell?.ResetDisplay();
    }

    private void RefreshSearchResults()
    {
        InvalidateItemsCache();
        UpdateViewport();
        _searchAdapter?.RefreshResults();
    }

    private void OnSearchTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_searchTextBox == null || _searchModel == null || _synchronizingSearchText) return;

        var query = _searchTextBox.Text ?? string.Empty;
        if (_searchTextBox.InnerRightContent is Control clearButton)
            clearButton.IsVisible = !string.IsNullOrWhiteSpace(query);

        if (_usesExternalSearchModel)
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                if (_searchModel.Descriptors.Count > 0)
                    _clearedSearchDescriptors = _searchModel.Descriptors.ToArray();
                _searchModel.Clear();
                return;
            }

            var descriptors = _searchModel.Descriptors.ToArray();
            if (descriptors.Length == 0 && _clearedSearchDescriptors is { Length: > 0 } cleared)
            {
                descriptors = cleared;
            }

            if (descriptors.Length == 0)
            {
                _searchModel.Apply(new[] { new SearchDescriptor(query) });
                return;
            }

            var first = descriptors[0];
            descriptors[0] = new SearchDescriptor(query, first.MatchMode, first.TermMode,
                first.Scope, first.ColumnNames, first.Comparison, first.Culture,
                first.WholeWord, first.NormalizeWhitespace, first.IgnoreDiacritics, first.AllowEmpty);
            _searchModel.Apply(descriptors);
            return;
        }

        if (string.IsNullOrWhiteSpace(query))
        {
            _searchDebounceTimer?.Stop();
            _pendingSearchQuery = null;
            _searchModel.Clear();
            return;
        }

        // Debounce: keep typing without re-running the full grid scan on every keystroke.
        if (_searchDebounceTimer == null)
        {
            _searchDebounceTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(SearchDebounceMilliseconds),
            };
            _searchDebounceTimer.Tick += (_, _) =>
            {
                _searchDebounceTimer.Stop();
                var pending = _pendingSearchQuery;
                _pendingSearchQuery = null;
                if (pending != null)
                    _searchModel.Apply(new[] { new SearchDescriptor(pending) });
            };
        }

        _pendingSearchQuery = query;
        _searchDebounceTimer.Stop();
        _searchDebounceTimer.Start();
    }

    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (_searchModel == null) return;
        if (e.Key == Key.Enter)
        {
            if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
                _searchModel.MovePrevious();
            else
                _searchModel.MoveNext();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            CloseFindBar();
            e.Handled = true;
        }
    }

    private void OnSearchResultsChanged(object? sender, SearchResultsChangedEventArgs e)
    {
        if (_searchCountText == null || _searchModel == null) return;
        var count = _searchModel.Results.Select(result => result.RowIndex).Distinct().Count();
        _searchCountText.Text = count > 0 ? $"{count} 行结果" : "无结果";

        ClearRows();
        InvalidateItemsCache();
        RefreshRows();
        _scrollPanel?.InvalidateMeasure();
        _scrollPanel?.InvalidateArrange();
    }

    private void OnSearchCurrentChanged(object? sender, SearchCurrentChangedEventArgs e)
    {
        if (_searchModel?.CurrentResult == null) return;
        var result = _searchModel.CurrentResult;
        var items = GetItemsList();
        int filteredIndex = items.IndexOf(result.Item);
        if (filteredIndex >= 0)
        {
            EnsureRowVisible(filteredIndex);
            UpdateScrollBars();
            UpdateViewport();

            var row = _realizedRows.FirstOrDefault(realizedRow => realizedRow.Index == filteredIndex);
            var cell = row?.Cells.FirstOrDefault(candidate => Columns.IndexOf(candidate.Column!) == result.ColumnIndex);
            if (row != null && cell != null)
            {
                SetRowSelection(row, clearOthers: true);
                _selectionAnchorIndex = filteredIndex;
                SelectedIndex = filteredIndex;
                SelectedItem = row.DataContext;

                if (_currentCell != null && _currentCell != cell) _currentCell.IsSelected = false;
                ClearPendingCurrentCellRestore();
                cell.IsSelected = true;
                _currentCell = cell;
            }
        }

        foreach (var row in _realizedRows)
            row.RefreshSearchHighlights();
    }

    internal SearchHighlightMode EffectiveSearchHighlightMode => _searchModel?.HighlightMode ?? SearchHighlightMode.None;

    internal SearchResult? GetCellSearchResult(DataGridCell cell)
    {
        if (_searchModel == null || _searchModel.Results.Count == 0) return null;
        if (cell.Column == null || cell.DataItem == null) return null;

        var columnIndex = Columns.IndexOf(cell.Column);
        var result = _searchModel.CurrentResult;
        if (result != null && result.Item == cell.DataItem && result.ColumnIndex == columnIndex)
            return result;

        return null;
    }

    internal IReadOnlyList<SearchMatch>? GetCellSearchMatches(DataGridCell cell)
    {
        if (_searchModel == null || _searchModel.Results.Count == 0) return null;
        if (cell.Column == null || cell.DataItem == null) return null;

        var columnIndex = Columns.IndexOf(cell.Column);
        foreach (var result in _searchModel.Results)
        {
            if (result.Item == cell.DataItem && result.ColumnIndex == columnIndex && result.Matches.Count > 0)
                return result.Matches;
        }
        return null;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        EnsureDefaultVisualTree();
        return base.MeasureOverride(availableSize);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        EnsureDefaultVisualTree();
        _lastArrangeHeight = finalSize.Height;
        var r = base.ArrangeOverride(finalSize);
        UpdateStarColumnWidths(GetViewportWidth());
        UpdateViewport();
        UpdateScrollBars();
        return r;
    }

    private Border BuildEmptyState()
    {
        _emptyTitle = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.Parse("#666666")),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
        };
        _emptyClearButton = new AtomButton
        {
            Content = new StackPanel
            {
                Orientation = Avalonia.Layout.Orientation.Horizontal,
                Spacing = 6,
                Children =
                {
                    new Avalonia.Controls.Shapes.Path
                    {
                        Data = Geometry.Parse("M12.531 3H3a1 1 0 0 0-.742 1.67l7.225 7.989A2 2 0 0 1 10 14v6a1 1 0 0 0 .553.895l2 1A1 1 0 0 0 14 21v-7a2 2 0 0 1 .517-1.341l.427-.473 M16.5 3.5l5 5 M21.5 3.5l-5 5"),
                        Stroke = new SolidColorBrush(Color.Parse("#666666")),
                        StrokeThickness = 1.75,
                        Stretch = Stretch.Uniform,
                        Width = 16,
                        Height = 16,
                        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                    },
                    new TextBlock { Text = "清除筛选", VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center },
                },
            },
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            Padding = new Thickness(10, 4),
            FontSize = 12,
        };
        Avalonia.Automation.AutomationProperties.SetName(_emptyClearButton, "清除筛选");
        _emptyClearButton.Click += (_, _) =>
        {
            _searchDebounceTimer?.Stop();
            _pendingSearchQuery = null;
            _searchModel?.Clear();
            ClearAllFilters();
            if (_showErrorsOnly) ToggleErrorFilter();
            Focus();
        };
        return new Border
        {
            IsVisible = false,
            Padding = new Thickness(24),
            ClipToBounds = true,
            Child = new StackPanel
            {
                Spacing = 12,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                Children = { _emptyTitle, _emptyClearButton },
            },
        };
    }

    private void UpdateEmptyState()
    {
        var empty = GetItemCount() == 0;
        PseudoClasses.Set(":empty", empty);
        if (_emptyState == null || _emptyTitle == null || _emptyClearButton == null) return;
        _emptyState.IsVisible = empty;
        if (!empty) return;
        var filtering = _showErrorsOnly || HasActiveSearch || Columns.Any(column => column.IsFiltered);
        _emptyTitle.Text = filtering ? "没有匹配的行" : EmptyMessage ?? "暂无数据";
        _emptyClearButton.IsVisible = filtering;
    }

    private void EnsureDefaultVisualTree()
    {
        if (_templateApplied) return;

        _headersPresenter = new DataGridColumnHeadersPresenter { OwningGrid = this };
        _headerClipper = new Border
        {
            ClipToBounds = true,
            BorderBrush = new SolidColorBrush(Color.Parse("#D0D0D0")),
            BorderThickness = new Thickness(0, 0.5, 0, 0.5),
            Background = HeaderBackground,
            ZIndex = 10,
        };
        _headerClipper.Child = _headersPresenter;

        _scrollPanel = new DataGridScrollPanel
        {
            OwningGrid = this,
            ClipToBounds = true,
        };

        _selectionOverlay = new DataGridSelectionOverlay(this) { IsHitTestVisible = false, ZIndex = 40 };
        _scrollPanel.Children.Add(_selectionOverlay);

        _vScrollBar = new ScrollBar
        {
            Orientation = Avalonia.Layout.Orientation.Vertical,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
        };
        _vScrollBar.ValueChanged += OnVScrollBarChanged;

        _hScrollBar = new ScrollBar
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch,
        };
        _hScrollBar.ValueChanged += OnHScrollBarChanged;

        var root = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto,Auto"),
            ClipToBounds = true,
        };

        Grid.SetRow(_headerClipper, 1);
        Grid.SetColumn(_headerClipper, 0);
        root.Children.Add(_headerClipper);

        // Search bar
        _searchBarBorder = BuildSearchBar();
        Grid.SetRow(_searchBarBorder, 0);
        Grid.SetColumn(_searchBarBorder, 0);
        Grid.SetColumnSpan(_searchBarBorder, 2);
        root.Children.Add(_searchBarBorder);
        UpdateSearchBarVisibility();

        Grid.SetRow(_scrollPanel, 2);
        Grid.SetColumn(_scrollPanel, 0);
        root.Children.Add(_scrollPanel);

        // A body sibling stays centered independently of row virtualization and scroll offsets.
        _emptyState = BuildEmptyState();
        Grid.SetRow(_emptyState, 2);
        Grid.SetColumn(_emptyState, 0);
        root.Children.Add(_emptyState);

        Grid.SetRow(_vScrollBar, 2);
        Grid.SetColumn(_vScrollBar, 1);
        root.Children.Add(_vScrollBar);

        _statusBar = new TextBlock
        {
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.Parse("#666666")),
            Margin = new Thickness(8, 2),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
        var statusBarBorder = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#F5F5F5")),
            BorderBrush = new SolidColorBrush(Color.Parse("#E0E0E0")),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Child = _statusBar,
        };
        Grid.SetRow(statusBarBorder, 3);
        Grid.SetColumn(statusBarBorder, 0);
        root.Children.Add(statusBarBorder);

        Grid.SetRow(_hScrollBar, 4);
        Grid.SetColumn(_hScrollBar, 0);
        root.Children.Add(_hScrollBar);

        ClipToBounds = true;
        LogicalChildren.Clear();
        VisualChildren.Clear();
        LogicalChildren.Add(root);
        VisualChildren.Add(root);

        _templateApplied = true;
        BuildHeaders();
        RefreshRows();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        double vpH = GetViewportHeight();
        double vpW = GetViewportWidth();
        int itemCount = GetItemCount();
        double totalH = itemCount * RowHeight;
        double totalW = Columns.Sum(c => c.GetEffectiveWidth());
        double maxV = Math.Max(0, totalH - vpH);
        double maxH = Math.Max(0, totalW - vpW);

        if (e.Delta.Y != 0)
            _verticalOffset = Math.Clamp(_verticalOffset - e.Delta.Y * RowHeight * 3, 0, maxV);
        if (e.Delta.X != 0)
            _horizontalOffset = Math.Clamp(_horizontalOffset - e.Delta.X * 30, 0, maxH);

        UpdateScrollBars();
        // Coalesce rapid wheel events into one realization pass per render frame.
        ScheduleViewportUpdate();
        e.Handled = true;
    }

    private void UpdateStatusBar()
    {
        if (_statusBar == null) return;
        int total = GetTotalItemCount();
        int filtered = GetItemCount();
        _statusBar.Text = total == filtered
            ? $"共 {total} 行"
            : $"共 {filtered} 行（筛选自 {total} 行）";
    }

    private int GetTotalItemCount()
    {
        if (ItemsSource == null) return 0;
        if (ItemsSource is ICollection collection) return collection.Count;
        if (ItemsSource is IReadOnlyCollection<object> readOnly) return readOnly.Count;
        return ItemsSource.Cast<object>().Count();
    }

    private double GetViewportHeight()
    {
        if (_scrollPanel != null && _scrollPanel.Bounds.Height > 1)
            return _scrollPanel.Bounds.Height;

        double vpH = _lastArrangeHeight > 0 ? _lastArrangeHeight : Bounds.Height;
        vpH -= ColumnHeaderHeight;
        if (_statusBar != null) vpH -= 26;
        if (_hScrollBar != null && _hScrollBar.IsVisible) vpH -= _hScrollBar.DesiredSize.Height;
        return Math.Max(0, vpH);
    }

    private double GetViewportWidth()
    {
        if (_scrollPanel != null && _scrollPanel.Bounds.Width > 1)
            return _scrollPanel.Bounds.Width;

        double vpW = Bounds.Width;
        if (_vScrollBar != null && _vScrollBar.IsVisible) vpW -= _vScrollBar.DesiredSize.Width;
        return Math.Max(0, vpW);
    }

    private void SyncHorizontalOffset()
    {
        if (_headersPresenter != null)
        {
            _headersPresenter.HorizontalOffset = _horizontalOffset;
            _headersPresenter.InvalidateArrange();
        }
        foreach (var row in _realizedRows)
            row.InvalidateArrange();
        _selectionOverlay?.InvalidateVisual();
    }

    private void OnVScrollBarChanged(object? sender, RoutedEventArgs e)
    {
        if (_updatingScrollBars) return;
        _verticalOffset = _vScrollBar!.Value;
        ScheduleViewportUpdate();
    }

    private void ScheduleViewportUpdate()
    {
        if (_viewportUpdateScheduled)
            return;

        _viewportUpdateScheduled = true;
        Dispatcher.UIThread.Post(() =>
        {
            _viewportUpdateScheduled = false;
            if (_templateApplied)
                UpdateViewport();
        }, DispatcherPriority.Render);
    }

    private void OnHScrollBarChanged(object? sender, RoutedEventArgs e)
    {
        if (_updatingScrollBars) return;
        double maxOffset = Math.Max(0, Columns.Sum(column => column.GetEffectiveWidth()) - GetViewportWidth());
        _horizontalOffset = Math.Clamp(_hScrollBar!.Value, 0, maxOffset);
        SyncHorizontalOffset();
    }

    private void UpdateScrollBars()
    {
        if (_vScrollBar == null || _hScrollBar == null) return;
        if (_lastArrangeHeight <= 0 && Bounds.Height <= 0) return;
        _updatingScrollBars = true;

        int itemCount = GetItemCount();
        double totalH = itemCount * RowHeight;
        double totalW = Columns.Sum(c => c.GetEffectiveWidth());

        // First pass: determine which scrollbars are needed
        double vpH = GetViewportHeight();
        double vpW = GetViewportWidth();
        bool vNeeded = totalH > vpH + 1;
        bool hNeeded = totalW > vpW + 1;

        // Show/hide scrollbars
        _vScrollBar.IsVisible = vNeeded;
        _hScrollBar.IsVisible = hNeeded;

        // Second pass: recalculate with actual scrollbar visibility
        if (vNeeded)
        {
            vpH = GetViewportHeight();
            _hScrollBar.IsVisible = totalW > vpW;
            hNeeded = _hScrollBar.IsVisible;
        }
        if (hNeeded)
        {
            vpH = GetViewportHeight();
        }

        if (vNeeded)
        {
            _vScrollBar.Minimum = 0;
            _vScrollBar.Maximum = Math.Max(0, totalH - vpH);
            _vScrollBar.ViewportSize = vpH;
            _vScrollBar.LargeChange = vpH;
            _vScrollBar.SmallChange = RowHeight;
            _verticalOffset = Math.Clamp(_verticalOffset, 0, _vScrollBar.Maximum);
            _vScrollBar.Value = _verticalOffset;
        }
        else
        {
            _verticalOffset = 0;
        }

        double finalVpW = GetViewportWidth();
        if (hNeeded)
        {
            _hScrollBar.Minimum = 0;
            _hScrollBar.Maximum = Math.Max(0, totalW - finalVpW);
            _hScrollBar.ViewportSize = finalVpW;
            _hScrollBar.LargeChange = finalVpW;
            _hScrollBar.SmallChange = 50;
            _horizontalOffset = Math.Clamp(_horizontalOffset, 0, _hScrollBar.Maximum);
            _hScrollBar.Value = _horizontalOffset;
        }
        else
        {
            _horizontalOffset = 0;
        }

        _updatingScrollBars = false;
        SyncHorizontalOffset();
    }

    private void UpdateStarColumnWidths(double viewportWidth)
    {
        var stars = Columns.Where(column => column.IsVisible && column.Width.IsStar).ToList();
        if (stars.Count == 0) return;

        double fixedWidth = Columns
            .Where(column => column.IsVisible && !column.Width.IsStar)
            .Sum(column => column.GetEffectiveWidth());
        double remainingWidth = Math.Max(0, viewportWidth - fixedWidth);
        var unresolved = new List<DataGridColumn>(stars);

        while (unresolved.Count > 0)
        {
            double totalWeight = unresolved.Sum(column => column.Width.Value);
            bool constrained = false;
            foreach (var column in unresolved.ToList())
            {
                double targetWidth = totalWeight > 0
                    ? remainingWidth * column.Width.Value / totalWeight
                    : 0;
                double constrainedWidth = Math.Clamp(targetWidth, column.MinWidth, column.MaxWidth);
                if (Math.Abs(constrainedWidth - targetWidth) < 0.01) continue;

                column.SetActualWidth(constrainedWidth);
                remainingWidth = Math.Max(0, remainingWidth - constrainedWidth);
                unresolved.Remove(column);
                constrained = true;
            }

            if (constrained) continue;
            foreach (var column in unresolved)
            {
                double width = totalWeight > 0
                    ? remainingWidth * column.Width.Value / totalWeight
                    : 0;
                column.SetActualWidth(width);
            }
            break;
        }

        _headersPresenter?.InvalidateArrange();
        foreach (var row in _realizedRows)
            row.InvalidateArrange();
    }

    private void OnColumnsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        CancelEdit();
        ClearHistory();
        _rowPool.Clear();
        for (int i = 0; i < Columns.Count; i++) { Columns[i].Index = i; Columns[i].DataGridOwner = this; }
        if (_templateApplied) { BuildHeaders(); RefreshRows(); }
    }

    private void BuildHeaders()
    {
        if (_headersPresenter == null) return;
        _headersPresenter.Children.Clear();
        if (CanUserReorderRows)
        {
            _headersPresenter.Children.Add(new Border
            {
                Width = RowDragHandleWidth,
                Height = ColumnHeaderHeight,
                Background = HeaderBackground ?? new SolidColorBrush(Color.Parse("#fafafb")),
                IsHitTestVisible = false,
            });
        }

        int leftFrozen = Math.Min(LeftFrozenColumnCount, Columns.Count);
        int rightFrozen = Math.Min(RightFrozenColumnCount, Columns.Count);
        int totalCount = Columns.Count;

        for (int ci = 0; ci < Columns.Count; ci++)
        {
            if (!Columns[ci].IsVisible) continue;
            if (ci >= leftFrozen && ci < totalCount - rightFrozen)
            {
                var header = new DataGridColumnHeader { OwningColumn = Columns[ci], Height = ColumnHeaderHeight };
                _headersPresenter.Children.Add(header);
            }
        }
        for (int ci = 0; ci < Columns.Count; ci++)
        {
            if (!Columns[ci].IsVisible) continue;
            if (ci < leftFrozen || ci >= totalCount - rightFrozen)
            {
                var header = new DataGridColumnHeader { OwningColumn = Columns[ci], Height = ColumnHeaderHeight };
                _headersPresenter.Children.Add(header);
            }
        }
        if (_headerClipper != null)
        {
            bool hasVisibleHeader = Columns.Any(column =>
                column.IsVisible && !string.IsNullOrWhiteSpace(column.Header?.ToString()));
            _headerClipper.BorderThickness = hasVisibleHeader
                ? new Thickness(0, 0.5, 0, 0.5)
                : new Thickness(0, 0, 0, 0.5);
        }

        _headersPresenter.InvalidateMeasure();
        _headersPresenter.InvalidateArrange();
    }

    internal void OnColumnHeaderClick(DataGridColumn column)
    {
        ColumnHeaderClick?.Invoke(this, new DataGridColumnEventArgs(column));
    }

    internal void OnColumnWidthChanged(DataGridColumn column)
    {
        foreach (var row in _realizedRows)
        {
            foreach (var cell in row.Cells)
            {
                if (cell.Column != null)
                    cell.Width = cell.Column.GetEffectiveWidth();
            }
            row.InvalidateMeasure();
            row.InvalidateArrange();
        }

        _headersPresenter?.InvalidateMeasure();
        _headersPresenter?.InvalidateArrange();
        _scrollPanel?.InvalidateMeasure();
        _scrollPanel?.InvalidateArrange();
        UpdateScrollBars();
        UpdateViewport();
    }

    internal void OnColumnVisibilityChanged(DataGridColumn column)
    {
        if (!_templateApplied) return;
        _rowPool.Clear();
        UpdateStarColumnWidths(GetViewportWidth());
        BuildHeaders();
        foreach (var row in _realizedRows)
        {
            row.UpdateCells();
            row.InvalidateMeasure();
            row.InvalidateArrange();
        }
        _headersPresenter?.InvalidateMeasure();
        _headersPresenter?.InvalidateArrange();
        _scrollPanel?.InvalidateMeasure();
        _scrollPanel?.InvalidateArrange();
        UpdateScrollBars();
        UpdateViewport();
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (!_templateApplied) return;
            UpdateScrollBars();
            UpdateViewport();
            _scrollPanel?.InvalidateArrange();
        }, Avalonia.Threading.DispatcherPriority.Loaded);
    }

    private void UpdateViewport()
    {
        UpdateEmptyState();
        if (_scrollPanel == null || ItemsSource == null)
        {
            ClearRows();
            _realizedFirstIndex = -1;
            _realizedLastIndex = -1;
            ClearPendingCurrentCellRestore();
            return;
        }
        int itemCount = GetItemCount();
        double vpH = GetViewportHeight();
        if (vpH <= 0) vpH = 600;

        int first = Math.Max(0, (int)Math.Floor(_verticalOffset / RowHeight) - 2);
        int last = Math.Min(itemCount - 1, (int)Math.Ceiling((_verticalOffset + vpH) / RowHeight) + 2);
        var items = GetItemsList();
        if (first == _realizedFirstIndex && last == _realizedLastIndex)
        {
            foreach (var row in _realizedRows)
            {
                var item = row.Index < items.Count ? items[row.Index] : null;
                if (!ReferenceEquals(row.DataContext, item))
                {
                    _cellSelectionVisualsDirty = true;
                    UnsubscribeItemValidation(row.DataContext);
                    ReuseRow(row, row.Index, item);
                    SubscribeItemValidation(item);
                    RestoreRowValidationState(row);
                }
                var selected = IsItemSelected(item);
                if (row.IsSelected != selected)
                    row.IsSelected = selected;
            }
            _scrollPanel.InvalidateArrange();
            if (SelectionUnit == DataGridSelectionUnit.Cell && _cellSelectionVisualsDirty)
                ApplyCellSelectionVisuals();
            RestorePendingCurrentCell();
            return;
        }

        _cellSelectionVisualsDirty = true;
        var realizedByIndex = new Dictionary<int, DataGridRow>(_realizedRows.Count);
        foreach (var row in _realizedRows)
            realizedByIndex[row.Index] = row;

        for (var index = _realizedRows.Count - 1; index >= 0; index--)
        {
            var row = _realizedRows[index];
            if (row.Index >= first && row.Index <= last)
                continue;

            UnsubscribeItemValidation(row.DataContext);
            _realizedRows.RemoveAt(index);
            ReturnRowToPool(row);
        }

        for (int i = first; i <= last; i++)
        {
            if (realizedByIndex.TryGetValue(i, out var existing))
            {
                var item = i < items.Count ? items[i] : null;
                if (!ReferenceEquals(existing.DataContext, item))
                {
                    UnsubscribeItemValidation(existing.DataContext);
                    ReuseRow(existing, i, item);
                    SubscribeItemValidation(item);
                    RestoreRowValidationState(existing);
                }
                var selected = IsItemSelected(item);
                if (existing.IsSelected != selected)
                    existing.IsSelected = selected;
                continue;
            }

            var row = ReuseOrCreateRow(i, items);
            _realizedRows.Add(row);
            realizedByIndex[i] = row;
            if (!_scrollPanel.Children.Contains(row)) _scrollPanel.Children.Add(row);
        }

        _realizedFirstIndex = first;
        _realizedLastIndex = last;
        _scrollPanel.InvalidateArrange();
        if (SelectionUnit == DataGridSelectionUnit.Cell && _cellSelectionVisualsDirty)
            ApplyCellSelectionVisuals();
        RestorePendingCurrentCell();
    }

    private DataGridRow ReuseOrCreateRow(int index, IList items)
    {
        object? item = index < items.Count ? items[index] : null;

        DataGridRow row;
        if (_rowPool.Count > 0)
        {
            row = _rowPool[^1];
            _rowPool.RemoveAt(_rowPool.Count - 1);
            row.IsVisible = true;
            ReuseRow(row, index, item);
            if (!_scrollPanel!.Children.Contains(row))
                _scrollPanel.Children.Add(row);
        }
        else
        {
            row = new DataGridRow { Height = RowHeight, OwningGrid = this };
            row.PointerPressed += OnRowPointerPressed;
            ReuseRow(row, index, item);
            row.UpdateCells();
        }

        bool sel = IsItemSelected(item);
        row.IsSelected = sel;
        if (sel && index == SelectedIndex) _currentRow = row;
        SubscribeItemValidation(item);
        RestoreRowValidationState(row);
        return row;
    }

    private void ReuseRow(DataGridRow row, int index, object? item)
    {
        row.Index = index;
        row.DataContext = item;
        foreach (var cell in row.Cells)
        {
            if (cell.IsEditing)
                cell.CancelEdit();
            cell.DataItem = item;
            if (cell.Column != null)
                cell.Width = cell.Column.GetEffectiveWidth();
        }
    }

    private void ReturnRowToPool(DataGridRow row)
    {
        foreach (var cell in row.Cells)
        {
            if (cell.IsEditing)
                cell.CancelEdit();
        }

        var currentCellInRow = row.Cells.Any(cell => ReferenceEquals(cell, _currentCell));

        if (currentCellInRow && _currentCell?.Column is { } column)
        {
            _pendingCurrentCellRowIndex = row.Index;
            _pendingCurrentCellColumn = column;
            _pendingCurrentCellHadFocus = _currentCell.IsFocused;
            _currentCell.IsSelected = false;
            _currentCell = null;
        }

        row.Index = -1;
        row.IsVisible = false;
        if (_rowPool.Count >= MaxRowPoolSize)
        {
            _scrollPanel?.Children.Remove(row);
            return;
        }

        _rowPool.Add(row);
    }

    private void RestorePendingCurrentCell()
    {
        if (_pendingCurrentCellRowIndex is not { } rowIndex || _pendingCurrentCellColumn is not { } column)
            return;

        var row = _realizedRows.FirstOrDefault(candidate => candidate.Index == rowIndex);
        var cell = row?.Cells.FirstOrDefault(candidate => ReferenceEquals(candidate.Column, column));
        if (cell == null)
            return;

        var restoreFocus = _pendingCurrentCellHadFocus;
        ClearPendingCurrentCellRestore();
        cell.IsSelected = true;
        _currentCell = cell;
        if (restoreFocus)
            cell.Focus();
    }

    private void ClearPendingCurrentCellRestore()
    {
        _pendingCurrentCellRowIndex = null;
        _pendingCurrentCellColumn = null;
        _pendingCurrentCellHadFocus = false;
    }

    private void ClearRows()
    {
        foreach (var row in _realizedRows)
        {
            UnsubscribeItemValidation(row.DataContext);
            ReturnRowToPool(row);
        }

        if (_scrollPanel != null)
        {
            // Remove only realized rows, keeping the frozen-column shadows intact.
            for (int i = _scrollPanel.Children.Count - 1; i >= 0; i--)
            {
                if (_scrollPanel.Children[i] is DataGridRow)
                    _scrollPanel.Children.RemoveAt(i);
            }
        }
        _realizedRows.Clear();
        _realizedFirstIndex = -1;
        _realizedLastIndex = -1;
    }

    private void InvalidateItemsCache()
    {
        _selectionOverlay?.Reset();
        _itemsDirty = true;
        _cachedItems = null;
    }

    private IList GetItemsList()
    {
        if (_itemsDirty)
        {
            if (ItemsSource == null) { _cachedItems = new List<object>(); }
            else
            {
                var source = ItemsSource.Cast<object>();
                if (_showErrorsOnly) source = source.Where(HasRowError);
                foreach (var col in Columns.Where(c => c.IsFiltered))
                    source = source.Where(item => col.PassesFilter(item));
                if (HasActiveSearch && _searchModel != null)
                {
                    var matched = new HashSet<object>(_searchModel.Results.Select(r => r.Item));
                    source = source.Where(item => matched.Contains(item));
                }
                _cachedItems = source.ToList();
            }
            _itemsDirty = false;
        }
        return _cachedItems!;
    }

    internal IEnumerable<object> GetFilterContextItems(DataGridColumn currentColumn)
    {
        if (ItemsSource == null) return Array.Empty<object>();

        IEnumerable<object> source = ItemsSource.Cast<object>();
        if (_showErrorsOnly) source = source.Where(HasRowError);
        foreach (var column in Columns.Where(column => column != currentColumn && column.IsFiltered))
            source = source.Where(column.PassesFilter);

        if (HasActiveSearch && _searchModel != null)
        {
            var matched = new HashSet<object>(_searchModel.Results.Select(result => result.Item));
            source = source.Where(matched.Contains);
        }

        return source;
    }

    internal int GetItemCount() => GetItemsList().Count;

    private int _filterUpdateDepth;

    internal void OnFilterChanged(DataGridColumn column)
    {
        if (_filterUpdateDepth > 0)
            return;

        ApplyFilterChange();
    }

    public void ClearAllFilters()
    {
        var changed = false;
        _filterUpdateDepth++;
        try
        {
            foreach (var column in Columns)
            {
                if (!column.IsFiltered)
                    continue;
                column.ClearFilterSilent();
                changed = true;
            }
        }
        finally
        {
            _filterUpdateDepth--;
        }

        if (changed)
            ApplyFilterChange();
    }

    private void ApplyFilterChange()
    {
        EndEditBeforeLayoutChange();
        var selectedItem = SelectedItem ?? FindSelectedItem();
        _currentRow = null;
        ClearPendingCurrentCellRestore();
        _currentCell = null;
        ClearSelectedCells();
        ClearRows();
        InvalidateItemsCache();

        var selectedIndex = selectedItem is null ? -1 : FindItemIndex(selectedItem);
        SelectedIndex = selectedIndex;
        _selectionAnchorIndex = selectedIndex;

        UpdateStatusBar();
        UpdateScrollBars();
        UpdateViewport();
        RefreshFilterIcons();

        if (selectedItem is not null)
            ScrollIntoView(selectedItem);
    }

    private object? FindSelectedItem()
    {
        if (ItemsSource == null)
            return null;

        foreach (var item in ItemsSource.Cast<object>())
        {
            if (IsItemSelected(item))
                return item;
        }

        return null;
    }

    private void RefreshFilterIcons()
    {
        if (_headersPresenter == null)
            return;

        foreach (var child in _headersPresenter.Children)
        {
            if (child is DataGridColumnHeader header)
                header.RefreshFilterIcon();
        }
    }

    private void OnRowPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (SelectionUnit == DataGridSelectionUnit.Cell && _cellSelectionVisualsDirty)
            return;
        if (sender is not DataGridRow row) return;
        var pos = e.GetPosition(row);
        var pt = e.GetCurrentPoint(this);
        if (!pt.Properties.IsLeftButtonPressed) return;

        DataGridCell? cell = HitTestCell(row, pos.X);
        bool isCheckBox = cell?.Column is DataGridCheckBoxColumn;
        if (isCheckBox)
        {
            // A checkbox cell is an editor for the bound model property, not row selection.
            e.Handled = true;
            return;
        }

        if (_isEditing && cell != null && cell != _currentCell)
            CommitEdit();

        var modifiers = e.KeyModifiers;
        bool isShift = modifiers.HasFlag(KeyModifiers.Shift);
        bool isCtrl = modifiers.HasFlag(KeyModifiers.Control);

        if (isShift)
        {
            if (_selectionAnchorIndex < 0)
                _selectionAnchorIndex = _currentRow?.Index ?? row.Index;
            SelectRange(_selectionAnchorIndex, row.Index);
        }
        else if (isCtrl)
        {
            SetItemSelected(row.DataContext, !IsItemSelected(row.DataContext));
            row.IsSelected = IsItemSelected(row.DataContext);
            _selectionAnchorIndex = row.Index;
        }
        else
        {
            SetRowSelection(row, clearOthers: true);
            _selectionAnchorIndex = row.Index;
        }

        row.IsSelected = IsItemSelected(row.DataContext);
        _currentRow = row;
        SelectedIndex = row.Index;
        SelectedItem = row.DataContext;

        if (cell != null)
        {
            if (_currentCell != null && _currentCell != cell) _currentCell.IsSelected = false;
            ClearPendingCurrentCellRestore();
            cell.IsSelected = true;
            _currentCell = cell;
        }

        SelectionChanged?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    internal bool TryBeginRowDrag(DataGridRow row, PointerPressedEventArgs e)
    {
        if (!CanUserReorderRows
            || IsReadOnly
            || _isEditing
            || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed
            || Columns.Any(column => column.IsFiltered)
            || (_searchModel?.Results.Count ?? 0) != 0)
        {
            return false;
        }

        _draggedRow = row;
        _rowDragStartPosition = e.GetPosition(this);
        _rowDragPosition = _rowDragStartPosition;
        _rowDropIndex = row.Index;
        return true;
    }

    internal void UpdateRowDrag(DataGridRow row, PointerEventArgs e)
    {
        if (_draggedRow == null || !ReferenceEquals(row, _draggedRow))
            return;

        var position = e.GetPosition(this);
        _rowDragPosition = position;
        if (!_isRowDragging)
        {
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed
                || Math.Abs(position.Y - _rowDragStartPosition.Y) < 4)
                return;

            _isRowDragging = true;
            _draggedRow.Opacity = 0;
            _draggedRow.ZIndex = 0;
            CreateDragPreview();
        }

        UpdateDragLayout(position);
        e.Handled = true;
    }

    internal void CompleteRowDrag(DataGridRow row, PointerReleasedEventArgs e)
    {
        if (_draggedRow == null || !ReferenceEquals(row, _draggedRow))
            return;

        if (_isRowDragging)
        {
            CompleteRowDrag();
            e.Handled = true;
        }

        ResetRowDrag(e.Pointer);
    }

    internal void CancelRowDrag(DataGridRow row, IPointer pointer)
    {
        if (_draggedRow != null && ReferenceEquals(row, _draggedRow))
            ResetRowDrag(pointer);
    }

    private void CreateDragPreview()
    {
        if (_scrollPanel == null || _draggedRow == null || _dragPreviewRow != null)
            return;

        _dragPreviewRow = new DataGridRow
        {
            Index = _draggedRow.Index,
            DataContext = _draggedRow.DataContext,
            Height = RowHeight,
            OwningGrid = this,
            IsHitTestVisible = false,
            ZIndex = 35,
            Opacity = 0.98
        };
        _dragPreviewRow.UpdateCells();
        _dragPreviewRow.SetDragVisualState(true);
        _scrollPanel.Children.Add(_dragPreviewRow);
        CreateRowDragFeedback();
    }

    private void CreateRowDragFeedback()
    {
        if (_scrollPanel == null || _draggedRow == null || RowDragFeedbackTemplate == null || _rowDragFeedback != null)
            return;

        var content = RowDragFeedbackTemplate.Build(_draggedRow.DataContext) as Control;
        if (content == null)
            return;

        content.DataContext = _draggedRow.DataContext;
        content.IsHitTestVisible = false;
        content.ZIndex = 60;
        content.Opacity = 0.98;
        _rowDragFeedback = content;
        _scrollPanel.Children.Add(content);
        content.Measure(new Size(Math.Min(420, Math.Max(0, _scrollPanel.Bounds.Width)), 120));
    }

    private void UpdateDragLayout(Point position)
    {
        UpdateRowDropIndex(position);
        _scrollPanel?.InvalidateArrange();
    }

    private void ApplyDragRowOffsets()
    {
        if (!_isRowDragging || _draggedRow == null)
            return;

        var sourceIndex = _draggedRow.Index;
        var targetIndex = _rowDropIndex;
        foreach (var row in _realizedRows)
        {
            if (row == _draggedRow || row == _dragPreviewRow)
                continue;

            var targetOffset = 0d;
            if (sourceIndex < targetIndex && row.Index > sourceIndex && row.Index <= targetIndex)
                targetOffset = -RowHeight;
            else if (sourceIndex > targetIndex && row.Index >= targetIndex && row.Index < sourceIndex)
                targetOffset = RowHeight;

            AnimateRowDragOffset(row, targetOffset);
        }
    }

    private void AnimateRowDragOffset(DataGridRow row, double targetOffset)
    {
        var now = DateTime.UtcNow;
        var currentOffset = GetCurrentRowDragOffset(row, now);
        if (Math.Abs(currentOffset - targetOffset) < 0.01)
            return;

        _rowDragAnimations[row] = new RowDragAnimation
        {
            StartOffset = currentOffset,
            TargetOffset = targetOffset,
            StartTime = now
        };
        EnsureRowDragAnimationTimer();
    }

    private double GetCurrentRowDragOffset(DataGridRow row, DateTime now)
    {
        if (!_rowDragAnimations.TryGetValue(row, out var animation))
            return row.RenderTransform is TranslateTransform transform ? transform.Y : 0;

        var progress = Math.Clamp(
            (now - animation.StartTime).TotalMilliseconds / RowDragAnimationDuration.TotalMilliseconds,
            0,
            1);
        var easedProgress = 1 - Math.Pow(1 - progress, 3);
        return animation.StartOffset + (animation.TargetOffset - animation.StartOffset) * easedProgress;
    }

    private void EnsureRowDragAnimationTimer()
    {
        _rowDragAnimationTimer ??= new DispatcherTimer
        {
            Interval = RowDragAnimationInterval
        };
        if (!_rowDragAnimationTimer.IsEnabled)
        {
            _rowDragAnimationTimer.Tick += OnRowDragAnimationTick;
            _rowDragAnimationTimer.Start();
        }
    }

    private void OnRowDragAnimationTick(object? sender, EventArgs e)
    {
        var now = DateTime.UtcNow;
        foreach (var pair in _rowDragAnimations.ToArray())
        {
            var row = pair.Key;
            var animation = pair.Value;
            var progress = Math.Clamp(
                (now - animation.StartTime).TotalMilliseconds / RowDragAnimationDuration.TotalMilliseconds,
                0,
                1);
            var easedProgress = 1 - Math.Pow(1 - progress, 3);
            var offset = animation.StartOffset + (animation.TargetOffset - animation.StartOffset) * easedProgress;

            if (Math.Abs(offset) < 0.01 && animation.TargetOffset == 0 && progress >= 1)
                row.RenderTransform = null;
            else
                row.RenderTransform = new TranslateTransform(0, offset);

            if (progress >= 1)
                _rowDragAnimations.Remove(row);
        }

        if (_dragPreviewAnimation != null && _dragPreviewRow != null)
        {
            var animation = _dragPreviewAnimation;
            var progress = Math.Clamp(
                (now - animation.StartTime).TotalMilliseconds / RowDragAnimationDuration.TotalMilliseconds,
                0,
                1);
            var easedProgress = 1 - Math.Pow(1 - progress, 3);
            _dragPreviewVisualY = animation.StartOffset +
                (animation.TargetOffset - animation.StartOffset) * easedProgress;
            SetDragPreviewVisualOffset();

            if (progress >= 1)
            {
                _dragPreviewVisualY = animation.TargetOffset;
                _dragPreviewAnimation = null;
                SetDragPreviewVisualOffset();
            }
        }

        if (_rowDragAnimations.Count == 0 && _dragPreviewAnimation == null && _rowDragAnimationTimer != null)
        {
            _rowDragAnimationTimer.Stop();
            _rowDragAnimationTimer.Tick -= OnRowDragAnimationTick;
        }
    }

    internal void ArrangeDragPreview(DataGridScrollPanel panel, Size finalSize, double verticalOffset)
    {
        if (_dragPreviewRow == null || _rowDropIndex < 0)
            return;

        var targetY = _rowDropIndex * RowHeight - verticalOffset;
        var now = DateTime.UtcNow;
        var currentY = GetCurrentDragPreviewY(now, targetY);
        if (_dragPreviewVisualY == null)
        {
            _dragPreviewVisualY = targetY;
        }
        else if (_dragPreviewAnimation == null || Math.Abs(_dragPreviewAnimation.TargetOffset - targetY) > 0.01)
        {
            _dragPreviewAnimation = new RowDragAnimation
            {
                StartOffset = currentY,
                TargetOffset = targetY,
                StartTime = now
            };
            EnsureRowDragAnimationTimer();
        }

        _dragPreviewTargetY = targetY;
        _dragPreviewVisualY = GetCurrentDragPreviewY(now, targetY);
        _dragPreviewRow.Arrange(new Rect(0, targetY, finalSize.Width, RowHeight));
        SetDragPreviewVisualOffset();
    }

    private double GetCurrentDragPreviewY(DateTime now, double fallbackTargetY)
    {
        if (_dragPreviewAnimation == null)
            return _dragPreviewVisualY ?? fallbackTargetY;

        var progress = Math.Clamp(
            (now - _dragPreviewAnimation.StartTime).TotalMilliseconds / RowDragAnimationDuration.TotalMilliseconds,
            0,
            1);
        var easedProgress = 1 - Math.Pow(1 - progress, 3);
        return _dragPreviewAnimation.StartOffset +
            (_dragPreviewAnimation.TargetOffset - _dragPreviewAnimation.StartOffset) * easedProgress;
    }

    private void SetDragPreviewVisualOffset()
    {
        if (_dragPreviewRow == null || _dragPreviewVisualY == null)
            return;

        var offset = _dragPreviewVisualY.Value - _dragPreviewTargetY;
        _dragPreviewRow.RenderTransform = Math.Abs(offset) < 0.01
            ? null
            : new TranslateTransform(0, offset);
    }

    internal void ArrangeRowDragFeedback(DataGridScrollPanel panel, Size finalSize)
    {
        if (_rowDragFeedback == null || !_isRowDragging)
            return;

        var panelOrigin = panel.TranslatePoint(default, this);
        if (panelOrigin == null)
            return;

        var pointer = _rowDragPosition - panelOrigin.Value;
        var desired = _rowDragFeedback.DesiredSize;
        var x = Math.Clamp(pointer.X + 18, 0, Math.Max(0, finalSize.Width - desired.Width));
        var y = pointer.Y - desired.Height - 10;
        if (y < 4)
            y = Math.Min(finalSize.Height - desired.Height - 4, pointer.Y + 10);
        y = Math.Clamp(y, 4, Math.Max(4, finalSize.Height - desired.Height - 4));
        _rowDragFeedback.Arrange(new Rect(x, y, desired.Width, desired.Height));
    }

    private void UpdateRowDropIndex(Point position)
    {
        if (_scrollPanel == null || _draggedRow == null)
            return;

        var itemCount = GetItemCount();
        if (itemCount == 0)
            return;

        var panelPosition = position - _scrollPanel.TranslatePoint(default, this)!.Value;
        _rowDropIndex = Math.Clamp(
            (int)Math.Floor((panelPosition.Y + _verticalOffset) / RowHeight),
            0,
            itemCount - 1);

        ApplyDragRowOffsets();
        _scrollPanel.InvalidateArrange();
    }

    private void CompleteRowDrag()
    {
        if (_draggedRow == null || _rowDropIndex < 0)
            return;

        var oldIndex = _draggedRow.Index;
        var newIndex = _rowDropIndex;
        if (newIndex == oldIndex)
            return;

        var item = _draggedRow.DataContext;
        var moved = false;
        if (ItemsSource is IList sourceList && !sourceList.IsReadOnly && !sourceList.IsFixedSize
            && oldIndex >= 0 && oldIndex < sourceList.Count)
        {
            sourceList.RemoveAt(oldIndex);
            sourceList.Insert(newIndex, item);
            moved = true;
        }

        RowReordered?.Invoke(this, new DataGridRowReorderedEventArgs(item, oldIndex, newIndex, moved));
        if (moved)
        {
            InvalidateItemsCache();
            UpdateViewport();
        }
    }

    private void ResetRowDrag(IPointer pointer)
    {
        _rowDragAnimationTimer?.Stop();
        if (_rowDragAnimationTimer != null)
            _rowDragAnimationTimer.Tick -= OnRowDragAnimationTick;
        _rowDragAnimations.Clear();
        _dragPreviewAnimation = null;
        _dragPreviewVisualY = null;
        _dragPreviewTargetY = 0;

        foreach (var row in _realizedRows)
            row.RenderTransform = null;

        if (_dragPreviewRow != null)
        {
            _dragPreviewRow.RenderTransform = null;
            _dragPreviewRow.SetDragVisualState(false);
            _scrollPanel?.Children.Remove(_dragPreviewRow);
            _dragPreviewRow = null;
        }

        if (_rowDragFeedback != null)
        {
            _scrollPanel?.Children.Remove(_rowDragFeedback);
            _rowDragFeedback = null;
        }

        if (_draggedRow != null)
        {
            _draggedRow.Opacity = 1;
            _draggedRow.ZIndex = 0;
            pointer.Capture(null);
        }

        _draggedRow = null;
        _isRowDragging = false;
        _rowDragPosition = default;
        _rowDropIndex = -1;
    }

    private static bool IsItemSelected(object? item)
    {
        if (item == null) return false;
        var property = DataGridBoundColumn.GetItemProperty(item, "IsSelected");
        return property?.GetValue(item) is bool selected && selected;
    }

    private static void SetItemSelected(object? item, bool selected)
    {
        if (item == null) return;
        var property = DataGridBoundColumn.GetItemProperty(item, "IsSelected");
        if (property?.CanWrite == true && property.PropertyType == typeof(bool))
            property.SetValue(item, selected);
    }

    private void SetRowSelection(DataGridRow row, bool clearOthers)
    {
        if (clearOthers)
        {
            foreach (var item in GetItemsList())
                SetItemSelected(item, false);
            foreach (var realizedRow in _realizedRows)
                realizedRow.IsSelected = false;
        }

        SetItemSelected(row.DataContext, true);
        row.IsSelected = true;
    }

    private void SelectRange(int firstIndex, int lastIndex)
    {
        var items = GetItemsList();
        if (items.Count == 0) return;
        int start = Math.Clamp(Math.Min(firstIndex, lastIndex), 0, items.Count - 1);
        int end = Math.Clamp(Math.Max(firstIndex, lastIndex), 0, items.Count - 1);

        foreach (var item in items)
            SetItemSelected(item, false);
        for (int i = start; i <= end; i++)
            SetItemSelected(items[i], true);

        foreach (var realizedRow in _realizedRows)
            realizedRow.IsSelected = IsItemSelected(realizedRow.DataContext);
    }

    private DataGridCell? HitTestCell(DataGridRow row, double x)
    {
        foreach (var cell in row.Cells)
        {
            var bounds = cell.Bounds;
            if (x >= bounds.X && x < bounds.X + bounds.Width)
                return cell;
        }
        return null;
    }

    public void OnCellPressed(DataGridCell cell, PointerPressedEventArgs e)
    {
        if (cell.Column is DataGridCheckBoxColumn)
        {
            e.Handled = true;
            return;
        }

        if (SelectionUnit != DataGridSelectionUnit.Cell ||
            !e.GetCurrentPoint(cell).Properties.IsLeftButtonPressed ||
            cell.OwningRow == null)
        {
            return;
        }

        var rowIndex = cell.OwningRow.Index;
        var columnIndex = Columns.IndexOf(cell.Column!);
        if (rowIndex < 0 || columnIndex < 0)
            return;

        if (_isEditing && !ReferenceEquals(cell, _currentCell))
            CommitEdit();

        var position = new DataGridCellPosition(rowIndex, columnIndex);
        var modifiers = e.KeyModifiers;
        var isExtended = SelectionMode == DataGridSelectionMode.Extended;
        var isCtrl = isExtended && modifiers.HasFlag(KeyModifiers.Control);
        var isShift = isExtended && modifiers.HasFlag(KeyModifiers.Shift);
        var anchor = _cellSelectionAnchor ?? position;
        _dragSelectionBase = isCtrl ? new HashSet<DataGridCellPosition>(_selectedCells) : null;

        if (isShift)
        {
            ApplyCellSelectionRange(anchor, position, append: isCtrl);
        }
        else if (isCtrl)
        {
            ToggleCellSelection(position);
            _cellSelectionAnchor = position;
        }
        else
        {
            ApplyCellSelectionRange(position, position, append: false);
            _cellSelectionAnchor = position;
        }

        _currentRow = cell.OwningRow;
        SelectedIndex = rowIndex;
        SelectedItem = cell.DataItem;
        ClearPendingCurrentCellRestore();
        _currentCell = cell;
        _isCellSelectionDragging = true;
        _cellSelectionDidDrag = false;
        _cellSelectionAppend = isCtrl;
        _cellSelectionStartPoint = e.GetPosition(this);
        _cellSelectionPointer = e.Pointer;
        // Ctrl-click toggles the anchor; the first drag must still build its range.
        _lastDragSelectionTarget = isCtrl ? null : position;
        _lastDragSelectionCell = cell;
        _lastDragSelectionItem = cell.DataItem;
        _lastDragSelectionRevision = _cellSelectionRevision;
        _selectionOverlay?.SelectionChanged();
        Focus();
    }

    private void OnCellSelectionPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isCellSelectionDragging || !ReferenceEquals(e.Pointer, _cellSelectionPointer))
            return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        var point = e.GetPosition(this);
        if (Math.Abs(point.X - _cellSelectionStartPoint.X) < 3 &&
            Math.Abs(point.Y - _cellSelectionStartPoint.Y) < 3)
            return;

        _cellSelectionDidDrag = true;
        if (!ReferenceEquals(e.Pointer.Captured, this)) e.Pointer.Capture(this);
        var target = FindCellAtPoint(point);
        if (target?.OwningRow == null)
            return;

        var targetPosition = new DataGridCellPosition(
            target.OwningRow.Index,
            Columns.IndexOf(target.Column!));
        if (targetPosition.RowIndex < 0 || targetPosition.ColumnIndex < 0)
            return;

        if (_lastDragSelectionTarget == targetPosition &&
            ReferenceEquals(_lastDragSelectionCell, target) &&
            ReferenceEquals(_lastDragSelectionItem, target.DataItem) &&
            _lastDragSelectionRevision == _cellSelectionRevision)
        {
            e.Handled = true;
            return;
        }

        _lastDragSelectionTarget = targetPosition;
        _lastDragSelectionCell = target;
        _lastDragSelectionItem = target.DataItem;
        ApplyCellSelectionRange(_cellSelectionAnchor ?? targetPosition, targetPosition, append: _cellSelectionAppend);
        _lastDragSelectionRevision = _cellSelectionRevision;
        _currentRow = target.OwningRow;
        SelectedIndex = targetPosition.RowIndex;
        SelectedItem = target.DataItem;
        _currentCell = target;
        target.IsSelected = true;
        e.Handled = true;
    }

    private void OnCellSelectionPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isCellSelectionDragging || !ReferenceEquals(e.Pointer, _cellSelectionPointer))
            return;

        var target = FindCellAtPoint(e.GetPosition(this));
        if (_cellSelectionDidDrag && target?.OwningRow != null)
        {
            var targetPosition = new DataGridCellPosition(
                target.OwningRow.Index,
                Columns.IndexOf(target.Column!));
            if (targetPosition.RowIndex >= 0 && targetPosition.ColumnIndex >= 0 &&
                (_lastDragSelectionTarget != targetPosition ||
                 !ReferenceEquals(_lastDragSelectionCell, target) ||
                 !ReferenceEquals(_lastDragSelectionItem, target.DataItem) ||
                 _lastDragSelectionRevision != _cellSelectionRevision))
                ApplyCellSelectionRange(_cellSelectionAnchor ?? targetPosition, targetPosition, append: _cellSelectionAppend);
        }

        var didDrag = _cellSelectionDidDrag;
        e.Pointer.Capture(null);
        _cellSelectionPointer = null;
        _cellSelectionAppend = false;
        _dragSelectionBase = null;
        _lastDragSelectionTarget = null;
        _lastDragSelectionCell = null;
        _lastDragSelectionItem = null;
        _isCellSelectionDragging = false;
        _cellSelectionDidDrag = false;
        if (didDrag)
            e.Handled = true;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        if (ReferenceEquals(e.Pointer, _cellSelectionPointer))
        {
            _cellSelectionPointer = null;
            _cellSelectionAppend = false;
            _isCellSelectionDragging = false;
            _cellSelectionDidDrag = false;
        }
    }

    private DataGridCell? FindCellAtPoint(Point point)
    {
        foreach (var row in _realizedRows)
        {
            var rowOrigin = row.TranslatePoint(default, this);
            if (rowOrigin is not { } rowPoint ||
                point.Y < rowPoint.Y || point.Y >= rowPoint.Y + row.Bounds.Height)
                continue;

            foreach (var cell in row.Cells)
            {
                var cellOrigin = cell.TranslatePoint(default, this);
                if (cellOrigin is not { } cellPoint)
                    continue;
                if (point.X >= cellPoint.X && point.X < cellPoint.X + cell.Bounds.Width)
                    return cell;
            }
        }
        return null;
    }

    public void SelectCells(DataGridCellPosition anchor, DataGridCellPosition target, bool append = false)
    {
        ApplyCellSelectionRange(anchor, target, append);
        _cellSelectionAnchor = anchor;
        _selectionOverlay?.SelectionChanged();
    }

    public void ClearSelectedCells()
    {
        if (_selectedCells.Count == 0)
            return;
        _cellSelectionRevision++;
        _selectedCells.Clear();
        ApplyCellSelectionVisuals();
        _selectionOverlay?.SelectionChanged();
        SelectedCellsChanged?.Invoke(this, EventArgs.Empty);
    }

    private static readonly DataFormat<byte[]> HtmlClipboardFormat = DataFormat.CreateBytesPlatformFormat(
        OperatingSystem.IsWindows() ? "HTML Format" : OperatingSystem.IsMacOS() ? "public.html" : "text/html");

    internal DataGridCellPosition? ActiveSelectionCell => _cellSelectionAnchor;
    internal IEnumerable<DataGridCell> RealizedCells => _realizedRows.SelectMany(row => row.Cells);

    private async Task CopySelectedCellsAsync()
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard == null || _selectedCells.Count == 0)
            return;

        var minRow = _selectedCells.Min(position => position.RowIndex);
        var maxRow = _selectedCells.Max(position => position.RowIndex);
        var minColumn = _selectedCells.Min(position => position.ColumnIndex);
        var maxColumn = _selectedCells.Max(position => position.ColumnIndex);
        var items = GetItemsList();
        var matrix = new List<IReadOnlyList<string>>(maxRow - minRow + 1);
        var copiedSelection = new HashSet<DataGridCellPosition>(_selectedCells);
        var source = ItemsSource;

        for (var rowIndex = minRow; rowIndex <= maxRow; rowIndex++)
        {
            var values = new List<string>(maxColumn - minColumn + 1);
            for (var columnIndex = minColumn; columnIndex <= maxColumn; columnIndex++)
            {
                var position = new DataGridCellPosition(rowIndex, columnIndex);
                if (!_selectedCells.Contains(position) || rowIndex < 0 || rowIndex >= items.Count ||
                    columnIndex < 0 || columnIndex >= Columns.Count)
                {
                    values.Add(string.Empty);
                    continue;
                }

                values.Add(GetCellClipboardText(items[rowIndex], Columns[columnIndex]));
            }
            matrix.Add(values);
        }

        var tsv = DataGridClipboardText.ToTsv(matrix);
        try
        {
            try
            {
                var item = new DataTransferItem();
                item.SetText(tsv);
                var html = DataGridClipboardText.ToHtml(matrix);
                item.Set(HtmlClipboardFormat, Encoding.UTF8.GetBytes(html));
                var transfer = new DataTransfer();
                transfer.Add(item);
                await clipboard.SetDataAsync(transfer);
            }
            catch (Exception)
            {
                await clipboard.SetTextAsync(tsv);
            }
            if (ReferenceEquals(source, ItemsSource)) _selectionOverlay?.Flash(copiedSelection);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"DataGrid copy failed: {ex.Message}");
        }
    }

    private async Task PasteSelectedCellsAsync()
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard == null || IsReadOnly)
            return;

        var source = ItemsSource;
        var selection = new HashSet<DataGridCellPosition>(_selectedCells);
        var startBeforeRead = GetClipboardStartPosition();
        List<List<string>>? matrix = null;
        try
        {
            using var data = await clipboard.TryGetDataAsync();
            if (data != null)
            {
                if (data.Contains(HtmlClipboardFormat) && await data.TryGetValueAsync(HtmlClipboardFormat) is { Length: > 0 } bytes)
                    matrix = DataGridClipboardText.ParseHtml(Encoding.UTF8.GetString(bytes));
                if (matrix == null && await data.TryGetTextAsync() is { Length: > 0 } text)
                    matrix = DataGridClipboardText.ParseTsv(text);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"DataGrid paste failed: {ex.Message}");
            return;
        }
        if (matrix is not { Count: > 0 } || !ReferenceEquals(source, ItemsSource) ||
            !selection.SetEquals(_selectedCells) || startBeforeRead != GetClipboardStartPosition()) return;
        var start = startBeforeRead;
        if (start == null) return;
        var sourceRows = matrix.Select(row => row.ToArray()).ToArray();
        var sourceColumnCount = sourceRows.Max(row => row.Length);
        if (sourceColumnCount == 0) return;
        var selectionRows = _selectedCells.Count > 0 ? _selectedCells.Max(p => p.RowIndex) - start.Value.RowIndex + 1 : 1;
        var selectionColumns = _selectedCells.Count > 0 ? _selectedCells.Max(p => p.ColumnIndex) - start.Value.ColumnIndex + 1 : 1;
        var fillSelection = (selectionRows > sourceRows.Length || selectionColumns > sourceColumnCount) &&
            selectionRows % sourceRows.Length == 0 && selectionColumns % sourceColumnCount == 0;
        var targetRowCount = fillSelection
            ? _selectedCells.Max(position => position.RowIndex) - start.Value.RowIndex + 1
            : sourceRows.Length;
        var targetColumnCount = fillSelection
            ? _selectedCells.Max(position => position.ColumnIndex) - start.Value.ColumnIndex + 1
            : sourceColumnCount;
        var items = GetItemsList();
        if (IsReadOnly || _isEditing) return;
        using var history = BeginHistory();
        var changedPositions = new List<DataGridCellPosition>();

        for (var rowOffset = 0; rowOffset < targetRowCount; rowOffset++)
        {
            var sourceValues = sourceRows[rowOffset % sourceRows.Length];
            for (var columnOffset = 0; columnOffset < targetColumnCount; columnOffset++)
            {
                var rowIndex = start.Value.RowIndex + rowOffset;
                var columnIndex = start.Value.ColumnIndex + columnOffset;
                var position = new DataGridCellPosition(rowIndex, columnIndex);
                if (fillSelection && !_selectedCells.Contains(position))
                    continue;
                if (rowIndex < 0 || rowIndex >= items.Count || columnIndex < 0 || columnIndex >= Columns.Count)
                    continue;

                var item = items[rowIndex];
                var column = Columns[columnIndex];
                if (item == null || column.IsReadOnly ||
                    column is not DataGridBoundColumn boundColumn ||
                    string.IsNullOrWhiteSpace(boundColumn.BindingPath))
                    continue;

                var property = DataGridBoundColumn.GetItemProperty(item, boundColumn.BindingPath);
                var sourceColumnIndex = columnOffset % sourceColumnCount;
                var clipboardValue = sourceColumnIndex < sourceValues.Length
                    ? sourceValues[sourceColumnIndex]
                    : string.Empty;
                if (property?.CanWrite != true ||
                    !TryConvertClipboardValue(clipboardValue, property.PropertyType, out var convertedValue))
                    continue;

                if (Equals(property.GetValue(item), convertedValue))
                    continue;

                WriteValue(item, property, convertedValue);
                changedPositions.Add(position);
            }
        }

        foreach (var position in changedPositions)
        {
            var cell = _realizedRows
                .Where(row => row.Index == position.RowIndex)
                .SelectMany(row => row.Cells)
                .FirstOrDefault(cell => Columns.IndexOf(cell.Column!) == position.ColumnIndex);
            cell?.ResetDisplay();
        }
        var target = new DataGridCellPosition(
            Math.Min(items.Count - 1, start.Value.RowIndex + targetRowCount - 1),
            Math.Min(Columns.Count - 1, start.Value.ColumnIndex + targetColumnCount - 1));
        // Search refresh resets the overlay and can change displayed row indices.
        // Keep the pasted rows by identity, then select and flash their new positions.
        var pastedRows = new List<object>();
        for (var row = start.Value.RowIndex; row <= target.RowIndex; row++)
            if (items[row] is { } item) pastedRows.Add(item);
        RefreshSearchResults();
        UpdateReplaceStatus();
        var refreshedItems = GetItemsList();
        var rowIndices = new Dictionary<object, int>(ReferenceEqualityComparer.Instance);
        for (var row = 0; row < refreshedItems.Count; row++)
            if (refreshedItems[row] is { } item) rowIndices.TryAdd(item, row);
        var pastedSelection = new HashSet<DataGridCellPosition>();
        foreach (var item in pastedRows)
        {
            if (!rowIndices.TryGetValue(item, out var row)) continue;
            for (var column = start.Value.ColumnIndex; column <= target.ColumnIndex; column++)
                pastedSelection.Add(new DataGridCellPosition(row, column));
        }
        if (pastedSelection.Count > 0)
        {
            _cellSelectionAnchor = pastedSelection.OrderBy(p => p.RowIndex).ThenBy(p => p.ColumnIndex).First();
            ReplaceCellSelection(pastedSelection);
            _selectionOverlay?.SelectionChanged();
            _selectionOverlay?.Flash(pastedSelection);
        }
    }

    private DataGridCellPosition? GetClipboardStartPosition()
    {
        if (_selectedCells.Count > 0)
        {
            return new DataGridCellPosition(
                _selectedCells.Min(position => position.RowIndex),
                _selectedCells.Min(position => position.ColumnIndex));
        }

        if (_currentCell?.OwningRow != null && _currentCell.Column != null)
        {
            var columnIndex = Columns.IndexOf(_currentCell.Column);
            if (_currentCell.OwningRow.Index >= 0 && columnIndex >= 0)
                return new DataGridCellPosition(_currentCell.OwningRow.Index, columnIndex);
        }

        return null;
    }

    private static string GetCellClipboardText(object? item, DataGridColumn column)
    {
        if (item == null || column is not DataGridBoundColumn boundColumn ||
            string.IsNullOrWhiteSpace(boundColumn.BindingPath))
            return string.Empty;

        return DataGridBoundColumn.GetItemProperty(item, boundColumn.BindingPath)?.GetValue(item)?.ToString() ?? string.Empty;
    }

    private static bool TryConvertClipboardValue(string text, Type targetType, out object? convertedValue)
    {
        convertedValue = null;
        var valueType = Nullable.GetUnderlyingType(targetType) ?? targetType;
        if (valueType == typeof(string))
        {
            convertedValue = text;
            return true;
        }

        if (string.IsNullOrWhiteSpace(text))
            return !targetType.IsValueType || Nullable.GetUnderlyingType(targetType) != null;

        if (valueType.IsEnum)
            return Enum.TryParse(valueType, text, ignoreCase: true, out convertedValue);

        try
        {
            convertedValue = Convert.ChangeType(text, valueType, CultureInfo.CurrentCulture);
            return convertedValue != null;
        }
        catch (InvalidCastException)
        {
            return false;
        }
        catch (FormatException)
        {
            return false;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private void ToggleCellSelection(DataGridCellPosition position)
    {
        var next = new HashSet<DataGridCellPosition>(_selectedCells);
        if (!next.Add(position))
            next.Remove(position);
        ReplaceCellSelection(next);
    }

    private void ApplyCellSelectionRange(DataGridCellPosition anchor, DataGridCellPosition target, bool append)
    {
        var minRow = Math.Min(anchor.RowIndex, target.RowIndex);
        var maxRow = Math.Max(anchor.RowIndex, target.RowIndex);
        var minColumn = Math.Min(anchor.ColumnIndex, target.ColumnIndex);
        var maxColumn = Math.Max(anchor.ColumnIndex, target.ColumnIndex);
        var next = append
            ? new HashSet<DataGridCellPosition>(_isCellSelectionDragging && _dragSelectionBase != null ? _dragSelectionBase : _selectedCells)
            : new HashSet<DataGridCellPosition>();

        for (var row = minRow; row <= maxRow; row++)
        {
            for (var column = minColumn; column <= maxColumn; column++)
                next.Add(new DataGridCellPosition(row, column));
        }
        ReplaceCellSelection(next);
    }

    private void ReplaceCellSelection(HashSet<DataGridCellPosition> next)
    {
        if (_selectedCells.SetEquals(next))
            return;
        _cellSelectionRevision++;
        _selectedCells.Clear();
        _selectedCells.UnionWith(next);
        ApplyCellSelectionVisuals();
        _selectionOverlay?.SelectionChanged();
        SelectedCellsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ApplyCellSelectionVisuals()
    {
        


        foreach (var row in _realizedRows)
        {
            foreach (var cell in row.Cells)
            {
                cell.IsSelected = SelectionUnit == DataGridSelectionUnit.Cell && cell.Column != null &&
                    _selectedCells.Contains(new DataGridCellPosition(row.Index, cell.Column.Index));
            }
        }

        _cellSelectionVisualsDirty = false;
        // Cell selection borders are drawn by the overlay, not neighboring cells.
    }

    internal Thickness GetCellSelectionBorderThickness(DataGridCell cell)
    {
        if (SelectionUnit != DataGridSelectionUnit.Cell || cell.OwningRow == null || cell.Column == null)
            return new Thickness(0.5);

        var rowIndex = cell.OwningRow.Index;
        var columnIndex = Columns.IndexOf(cell.Column);
        if (!_selectedCells.Contains(new DataGridCellPosition(rowIndex, columnIndex)))
            return new Thickness(0.5);

        bool hasSelectedLeft = columnIndex > 0 &&
            _selectedCells.Contains(new DataGridCellPosition(rowIndex, columnIndex - 1));
        bool hasSelectedTop = rowIndex > 0 &&
            _selectedCells.Contains(new DataGridCellPosition(rowIndex - 1, columnIndex));
        bool hasSelectedRight = columnIndex + 1 < Columns.Count &&
            _selectedCells.Contains(new DataGridCellPosition(rowIndex, columnIndex + 1));
        bool hasSelectedBottom =
            _selectedCells.Contains(new DataGridCellPosition(rowIndex + 1, columnIndex));

        return new Thickness(
            hasSelectedLeft ? 0 : 0.5,
            hasSelectedTop ? 0 : 0.5,
            hasSelectedRight ? 0 : 0.5,
            hasSelectedBottom ? 0 : 0.5);
    }

    public void BeginEdit(DataGridCell cell)
    {
        if (IsReadOnly || cell.Column?.IsReadOnly == true || cell.DataItem == null || _isEditing)
        {
            return;
        }

        _isEditing = true;
        ClearPendingCurrentCellRestore();
        _currentCell = cell;
        _editingSnapshot = null;
        if (cell.Column is DataGridBoundColumn bound && !string.IsNullOrWhiteSpace(bound.BindingPath) &&
            DataGridBoundColumn.GetItemProperty(cell.DataItem, bound.BindingPath) is { CanWrite: true } property)
            _editingSnapshot = new CellChange(cell.DataItem, property, property.GetValue(cell.DataItem), null);
        cell.BeginEdit();
        _isEditing = cell.IsEditing;
        if (!_isEditing) _editingSnapshot = null;
        if (cell.Column != null && cell.OwningRow != null && cell.ContentControl != null)
        {
            RaiseEvent(new DataGridPreparingCellForEditEventArgs(
                cell.Column,
                cell.OwningRow,
                new RoutedEventArgs(),
                cell.ContentControl)
            {
                RoutedEvent = PreparingCellForEditEvent,
                Source = this
            });
        }
    }

    internal void EndEditBeforeLayoutChange()
    {
        if (_isEditing && _currentCell != null)
            CommitEdit();

        if (_isEditing && _currentCell != null)
            CancelEdit();

        _isEditing = false;
        if (_currentCell is { IsEditing: true })
            _currentCell.CancelEdit();
    }

    public void CommitEdit()
    {
        if (!_isEditing)
        {
            return;
        }
        if (_currentCell == null)
        {
            _isEditing = false;
            return;
        }
        var cell = _currentCell;
        var args = new DataGridCellEditEndingEventArgs(cell, cell.Column, DataGridEditAction.Commit);
        CellEditEnding?.Invoke(this, args);
        if (args.Cancel) return;
        _isEditing = false;
        var value = cell.CommitEdit();
        CommitCellValue(cell, value);
        if (_editingSnapshot is { } snapshot)
        {
            _editingSnapshot = null;
            RecordChange(snapshot.Item, snapshot.Property, snapshot.Before);
        }
        cell.ResetDisplay();
        SetCurrentCell(cell);
    }

    internal void OnComboBoxDropDownClosed()
    {
        if (_isEditing)
            CommitEdit();
    }

    private static void CommitCellValue(DataGridCell cell, object? value)
    {
        if (cell.DataItem == null || cell.Column is not DataGridBoundColumn boundColumn
            || string.IsNullOrWhiteSpace(boundColumn.BindingPath))
            return;

        var property = DataGridBoundColumn.GetItemProperty(cell.DataItem, boundColumn.BindingPath);
        if (property?.CanWrite != true)
            return;

        var targetType = property.PropertyType;
        var convertedValue = ConvertCellValue(value, targetType);
        if (convertedValue == null && targetType.IsValueType && Nullable.GetUnderlyingType(targetType) == null)
            return;
        if (value != null && convertedValue == null)
            return;

        var currentValue = property.GetValue(cell.DataItem);
        if (Equals(currentValue, convertedValue) ||
            targetType == typeof(string) &&
            string.IsNullOrEmpty(currentValue as string) &&
            string.IsNullOrEmpty(convertedValue as string))
        {
            return;
        }

        property.SetValue(cell.DataItem, convertedValue);
    }

    private void ClearSelectedCellValues()
    {
        if (IsReadOnly) return;
        using var history = BeginHistory();
        var items = GetItemsList();
        foreach (var position in _selectedCells)
        {
            if (position.RowIndex < 0 || position.RowIndex >= items.Count ||
                position.ColumnIndex < 0 || position.ColumnIndex >= Columns.Count)
                continue;

            var item = items[position.RowIndex];
            var column = Columns[position.ColumnIndex];
            if (item == null || column.IsReadOnly || column is not DataGridBoundColumn boundColumn ||
                string.IsNullOrWhiteSpace(boundColumn.BindingPath))
                continue;

            var property = DataGridBoundColumn.GetItemProperty(item, boundColumn.BindingPath);
            if (property?.CanWrite != true)
                continue;

            var clearedValue = GetClearedCellValue(property.PropertyType);
            if (Equals(property.GetValue(item), clearedValue))
                continue;

            WriteValue(item, property, clearedValue);

            var realizedCell = _realizedRows
                .Where(row => row.Index == position.RowIndex)
                .SelectMany(row => row.Cells)
                .FirstOrDefault(cell => Columns.IndexOf(cell.Column!) == position.ColumnIndex);
            realizedCell?.ResetDisplay();
        }
    }

    private static object? GetClearedCellValue(Type targetType)
    {
        if (targetType == typeof(string))
            return string.Empty;
        // PropertyInfo.SetValue maps null to default(T) for non-nullable value types.
        return null;
    }

    private static object? ConvertCellValue(object? value, Type targetType)
    {
        if (value == null)
            return null;

        var valueType = Nullable.GetUnderlyingType(targetType) ?? targetType;
        if (valueType.IsInstanceOfType(value))
            return value;

        try
        {
            return Convert.ChangeType(value, valueType);
        }
        catch (InvalidCastException)
        {
            return null;
        }
        catch (FormatException)
        {
            return null;
        }
        catch (OverflowException)
        {
            return null;
        }
    }

    public void CancelEdit()
    {
        if (!_isEditing)
        {
            return;
        }
        if (_currentCell == null)
        {
            _isEditing = false;
            return;
        }
        var args = new DataGridCellEditEndingEventArgs(_currentCell, _currentCell.Column, DataGridEditAction.Cancel);
        CellEditEnding?.Invoke(this, args);
        _currentCell.CancelEdit();
        _isEditing = false;
        if (_editingSnapshot is { } snapshot)
        {
            _editingSnapshot = null;
            snapshot.Property.SetValue(snapshot.Item, snapshot.Before);
            _currentCell.ResetDisplay();
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_isEditing)
        {
            if (_currentCell?.Column is DataGridPopupColumn)
                return;

            if (e.Key == Key.Escape) { CancelEdit(); e.Handled = true; }
            else if (e.Key == Key.Enter) { CommitEdit(); Navigate(0, 1); e.Handled = true; }
            else if (e.Key == Key.Tab) { CommitEdit(); Navigate(e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1, 0); e.Handled = true; }
            return;
        }
        var isSearchInputFocused = _searchTextBox?.IsKeyboardFocusWithin == true;
        if (!e.Handled && !isSearchInputFocused && IsKeyboardFocusWithin &&
            e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Key is Key.Z or Key.Y)
        {
            if (e.Key == Key.Y || e.KeyModifiers.HasFlag(KeyModifiers.Shift)) Redo();
            else Undo();
            e.Handled = true;
            return;
        }
        switch (e.Key)
        {
            case Key.C when e.KeyModifiers.HasFlag(KeyModifiers.Control)
                && SelectionUnit == DataGridSelectionUnit.Cell && _selectedCells.Count > 0:
                _ = CopySelectedCellsAsync();
                e.Handled = true;
                break;
            case Key.V when e.KeyModifiers.HasFlag(KeyModifiers.Control)
                && SelectionUnit == DataGridSelectionUnit.Cell:
                _ = PasteSelectedCellsAsync();
                e.Handled = true;
                break;
            case Key.Delete when SelectionUnit == DataGridSelectionUnit.Cell && _selectedCells.Count > 0:
                ClearSelectedCellValues();
                e.Handled = true;
                break;
            case Key.F2:
            case Key.Enter:
                if (_currentCell?.Column is { IsReadOnly: false }) { BeginEdit(_currentCell); e.Handled = true; }
                break;
            case Key.F when e.KeyModifiers.HasFlag(KeyModifiers.Control):
                OpenFindBar(false);
                e.Handled = true;
                break;
            case Key.H when e.KeyModifiers.HasFlag(KeyModifiers.Control):
                OpenReplaceDialog();
                e.Handled = true;
                break;
            case Key.Escape when IsSearchBarVisible:
                CloseFindBar();
                e.Handled = true;
                break;
            case Key.Up: Navigate(0, -1); e.Handled = true; break;
            case Key.Down: Navigate(0, 1); e.Handled = true; break;
            case Key.Left: Navigate(-1, 0); e.Handled = true; break;
            case Key.Right: Navigate(1, 0); e.Handled = true; break;
            case Key.PageDown: Navigate(0, Math.Max(1, (int)(Bounds.Height / RowHeight))); e.Handled = true; break;
            case Key.PageUp: Navigate(0, -Math.Max(1, (int)(Bounds.Height / RowHeight))); e.Handled = true; break;
        }
    }

    private void Navigate(int colDelta, int rowDelta)
    {
        var items = GetItemsList();
        if (items.Count == 0) return;

        var currentIndex = SelectedIndex;
        if (currentIndex < 0 && _currentCell?.DataItem != null)
            currentIndex = items.IndexOf(_currentCell.DataItem);
        if (currentIndex < 0)
            currentIndex = 0;

        int newIdx = Math.Clamp(currentIndex + rowDelta, 0, items.Count - 1);
        int colIdx = _currentCell?.Column != null ? Columns.IndexOf(_currentCell.Column) : 0;
        colIdx = Math.Clamp(colIdx + colDelta, 0, Math.Max(0, Columns.Count - 1));

        SelectedIndex = newIdx;
        SelectedItem = items[newIdx];
        EnsureRowVisible(newIdx);
        UpdateScrollBars();
        UpdateViewport();
        FocusCell(newIdx, colIdx);
    }

    private void FocusCell(int rowIndex, int columnIndex)
    {
        var row = _realizedRows.FirstOrDefault(candidate => candidate.Index == rowIndex);
        var targetCell = row?.Cells.FirstOrDefault(candidate => Columns.IndexOf(candidate.Column!) == columnIndex);
        if (targetCell != null)
        {
            SetCurrentCell(targetCell);
            return;
        }

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            UpdateViewport();
            var realizedRow = _realizedRows.FirstOrDefault(candidate => candidate.Index == rowIndex);
            var realizedCell = realizedRow?.Cells.FirstOrDefault(candidate => Columns.IndexOf(candidate.Column!) == columnIndex);
            if (realizedCell != null)
                SetCurrentCell(realizedCell);
        }, Avalonia.Threading.DispatcherPriority.Loaded);
    }

    private void SetCurrentCell(DataGridCell cell)
    {
        if (_currentCell != null && _currentCell != cell)
            _currentCell.IsSelected = false;

        ClearPendingCurrentCellRestore();
        _currentCell = cell;
        if (SelectionUnit == DataGridSelectionUnit.Cell && cell.OwningRow != null && cell.Column != null)
        {
            var position = new DataGridCellPosition(cell.OwningRow.Index, Columns.IndexOf(cell.Column));
            ApplyCellSelectionRange(position, position, append: false);
            _cellSelectionAnchor = position;
            _selectionOverlay?.SelectionChanged();
        }
        else
        {
            cell.IsSelected = true;
        }
        cell.Focus();
    }

    private void EnsureRowVisible(int rowIndex)
    {
        double vpH = GetViewportHeight();
        if (vpH <= 0) return;
        double rowTop = rowIndex * RowHeight;
        double rowBottom = rowTop + RowHeight;
        if (rowTop < _verticalOffset)
            _verticalOffset = rowTop;
        else if (rowBottom > _verticalOffset + vpH)
            _verticalOffset = rowBottom - vpH;
        double maxV = Math.Max(0, GetItemCount() * RowHeight - vpH);
        _verticalOffset = Math.Clamp(_verticalOffset, 0, maxV);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e) { base.OnPointerPressed(e); Focus(); }

    private INotifyCollectionChanged? _subscribedSource;
    private bool _searchRefreshQueued;

    private void SubscribeCollectionChanged()
    {
        if (ReferenceEquals(_subscribedSource, ItemsSource)) return;

        if (_subscribedSource != null)
        {
            _subscribedSource.CollectionChanged -= OnItemsSourceCollectionChanged;
            _subscribedSource = null;
        }
        if (ItemsSource is INotifyCollectionChanged incc)
        {
            _subscribedSource = incc;
            _subscribedSource.CollectionChanged += OnItemsSourceCollectionChanged;
        }
    }

    private void OnItemsSourceCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            SyncErrorFilterSubscriptions();
            InvalidateItemsCache();
            UpdateStatusBar();
            UpdateScrollBars();
            UpdateViewport();

            if (_searchRefreshQueued || _searchAdapter == null) return;
            _searchRefreshQueued = true;
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                _searchRefreshQueued = false;
                _searchAdapter?.RefreshResults();
            }, Avalonia.Threading.DispatcherPriority.Background);
        });
    }

    public void RefreshRows()
    {
        SubscribeCollectionChanged();
        SyncErrorFilterSubscriptions();
        ClearSelectedCells();
        _cellSelectionAnchor = null;
            _cellSelectionVisualsDirty = true;
        InvalidateItemsCache();
        UpdateStatusBar();
        UpdateViewport();
    }

    public bool ScrollIntoView(object? rowModel)
    {
        if (rowModel is null)
            return false;

        var rowIndex = FindItemIndex(rowModel);
        if (rowIndex < 0)
            return false;

        EnsureRowVisible(rowIndex);
        UpdateScrollBars();
        UpdateViewport();

        Dispatcher.UIThread.Post(() =>
        {
            if (!_templateApplied)
                return;

            var currentIndex = FindItemIndex(rowModel);
            if (currentIndex < 0)
                return;

            EnsureRowVisible(currentIndex);
            UpdateScrollBars();
            UpdateViewport();
        }, DispatcherPriority.Render);

        return true;
    }

    private int FindItemIndex(object rowModel)
    {
        var items = GetItemsList();
        for (var index = 0; index < items.Count; index++)
        {
            if (ReferenceEquals(items[index], rowModel))
                return index;
        }

        return -1;
    }

    internal void OnColumnBindingChanged(DataGridColumn column) { }

    internal void SetVerticalOffset(double offset)
    {
        _verticalOffset = offset;
        UpdateScrollBars();
        UpdateViewport();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ItemsSourceProperty)
        {
            CancelEdit();
            ClearHistory();
            InvalidateItemsCache();
            UpdateStatusBar();
            RefreshRows();
        }
        else if (change.Property == EmptyMessageProperty) UpdateEmptyState();
        else if (change.Property == UndoLimitProperty) TrimHistory();
        else if (change.Property == IsReadOnlyProperty) UpdateUndoState();
        else if (change.Property == SearchModelProperty)
        {
            _usesExternalSearchModel = SearchModel != null;
            AttachSearchModel(SearchModel ?? new SearchModel { HighlightMode = SearchHighlightMode });
            _searchAdapter?.RefreshResults();
        }
        else if (change.Property == IsSearchBarVisibleProperty)
        {
            UpdateSearchBarVisibility();
        }
        else if (change.Property == SelectionUnitProperty)
        {
            ClearSelectedCells();
            _cellSelectionAnchor = null;
            _cellSelectionVisualsDirty = true;
            UpdateViewport();
        }
        else if (change.Property == SelectionModeProperty)
        {
            _cellSelectionAnchor = null;
            _cellSelectionVisualsDirty = true;
        }
        else if (change.Property == SelectedIndexProperty) UpdateViewport();
        else if (change.Property == GridLinesVisibilityProperty)
        {
            foreach (var row in _realizedRows) row.RefreshVisuals();
        }
    }

    private static void ApplyScrollBarTheme(ScrollBar scrollbar)
    {
        try
        {
            var resourceKey = scrollbar.Orientation == Avalonia.Layout.Orientation.Vertical
                ? "SemiVerticalScrollBar"
                : "SemiScrollBar";
            if (Application.Current?.TryGetResource(resourceKey, null, out var value) == true
                && value is Avalonia.Styling.ControlTheme ct)
            {
                scrollbar.Theme = ct;
                scrollbar.InvalidateVisual();
            }
        }
        catch { }
    }

    #region Validation

    internal static string? GetColumnBindingPath(DataGridColumn column)
    {
        if (column is DataGridBoundColumn bound && !string.IsNullOrEmpty(bound.BindingPath))
            return bound.BindingPath;
        return null;
    }

    internal static void ClearCellValidation(DataGridCell cell)
    {
        cell.SetValidationMessage(null, DataGridValidationSeverity.None, Colors.Transparent);
        cell.IsValid = true;
    }

    internal void RestoreRowValidationState(DataGridRow row)
    {
        if (row.DataContext is not INotifyDataErrorInfo indei)
            return;

        foreach (var cell in row.Cells)
        {
            var col = cell.Column;
            if (col == null)
            {
                cell.SetValidationMessage(null, DataGridValidationSeverity.None, Colors.Transparent);
                continue;
            }
            var path = GetColumnBindingPath(col);
            if (string.IsNullOrWhiteSpace(path))
            {
                cell.SetValidationMessage(null, DataGridValidationSeverity.None, Colors.Transparent);
                continue;
            }

            var errors = indei.GetErrors(path);
            if (errors == null)
            {
                cell.SetValidationMessage(null, DataGridValidationSeverity.None, Colors.Transparent);
                continue;
            }

            var exceptions = ValidationUtil.CreateValidationExceptions(errors);
            if (exceptions.Count == 0)
            {
                cell.SetValidationMessage(null, DataGridValidationSeverity.None, Colors.Transparent);
                continue;
            }

            var severity = ValidationUtil.GetValidationSeverity(exceptions);
            string? message = null;
            if (exceptions.Count > 0)
            {
                message = exceptions.Count == 1
                    ? exceptions[0].Message
                    : string.Join(Environment.NewLine, exceptions.Select(ex => ex.Message));
            }
            cell.IsValid = severity != DataGridValidationSeverity.InValid;
            var iconColor = severity switch
            {
                DataGridValidationSeverity.InValid => Color.Parse("#E81123"),
                DataGridValidationSeverity.Warning => Color.Parse("#FFB900"),
                DataGridValidationSeverity.Info => Color.Parse("#0078D4"),
                _ => Colors.Transparent
            };
            cell.SetValidationMessage(message, severity, iconColor);
        }

        bool hasError = false;
        foreach (var cell in row.Cells)
        {
            if (cell.ValidationSeverity == DataGridValidationSeverity.InValid)
            { hasError = true; break; }
        }
        row.IsValid = !hasError;
        row.ValidationSeverity = hasError ? DataGridValidationSeverity.InValid : DataGridValidationSeverity.None;
        UpdateGridValidationState();
    }

    internal void UpdateGridValidationState()
    {
        bool hasError = false;
        foreach (var row in _realizedRows)
        {
            if (!row.IsValid || row.ValidationSeverity == DataGridValidationSeverity.InValid)
            { hasError = true; break; }
        }
        if (!hasError) hasError = _validationItemsWithError.Count > 0;
        IsValid = !hasError;
    }

    internal void SubscribeItemValidation(object? item)
    {
        if (item is INotifyDataErrorInfo indei)
        {
            if (_validationTrackedItems.Add(indei))
                indei.ErrorsChanged += OnItemErrorsChanged;
        }
    }

    internal void UnsubscribeItemValidation(object? item)
    {
        if (item is INotifyDataErrorInfo indei)
        {
            if (_validationTrackedItems.Remove(indei))
            {
                indei.ErrorsChanged -= OnItemErrorsChanged;
                _validationItemsWithError.Remove(indei);
            }
        }
    }

    private void OnItemErrorsChanged(object? sender, DataErrorsChangedEventArgs e)
    {
        if (sender is not INotifyDataErrorInfo indei) return;

        bool hasItemError = false;
        foreach (var col in Columns)
        {
            var path = GetColumnBindingPath(col);
            if (string.IsNullOrWhiteSpace(path)) continue;
            var errors = indei.GetErrors(path);
            if (errors == null) continue;
            var exceptions = ValidationUtil.CreateValidationExceptions(errors);
            if (exceptions.Count > 0 && ValidationUtil.GetValidationSeverity(exceptions) == DataGridValidationSeverity.InValid)
            { hasItemError = true; break; }
        }

        if (hasItemError) _validationItemsWithError.Add(indei);
        else _validationItemsWithError.Remove(indei);

        foreach (var row in _realizedRows)
        {
            if (ReferenceEquals(row.DataContext, indei))
            {
                RestoreRowValidationState(row);
                row.RefreshVisuals();
                return;
            }
        }
        UpdateGridValidationState();
    }

    #endregion
}

// A single non-interactive layer keeps the wash above cell content and below editors/popups.
internal sealed class DataGridSelectionOverlay : Control
{
    private readonly DataGrid _grid;
    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private HashSet<DataGridCellPosition> _flash = new();
    private double _flashStart = -1000;
    private readonly Dictionary<int, SelectionSpring> _ranges = new();
    private SelectionSpring? _active;
    private int _activeRegion;
    private bool _selectionCacheDirty;
    private bool _selectionCacheInitialized;
    private bool _hasSelectedCells;
    private bool _selectionIsRectangular;
    private int _minSelectedRow;
    private int _maxSelectedRow;
    private int _minSelectedColumn;
    private int _maxSelectedColumn;

    // Arc Snappy: visualDuration = 0.26s, bounce = 0.12. Retarget from the
    // current position AND velocity, including when input arrives between frames.
    private sealed class SelectionSpring
    {
        private readonly double[] _value = new double[4];
        private readonly double[] _velocity = new double[4];
        private readonly double[] _target = new double[4];
        private double _time;
        public bool IsMoving { get; private set; }
        public Rect Value => new(_value[0], _value[1], Math.Max(0, _value[2]), Math.Max(0, _value[3]));

        public SelectionSpring(Rect rect, double now)
        {
            _value[0] = _target[0] = rect.X;
            _value[1] = _target[1] = rect.Y;
            _value[2] = _target[2] = rect.Width;
            _value[3] = _target[3] = rect.Height;
            _time = now;
        }

        public void Update(Rect target, double now)
        {
            Advance(now);
            if (_target[0] == target.X && _target[1] == target.Y &&
                _target[2] == target.Width && _target[3] == target.Height) return;
            _target[0] = target.X;
            _target[1] = target.Y;
            _target[2] = target.Width;
            _target[3] = target.Height;
            IsMoving = true;
        }

        private void Advance(double now)
        {
            var dt = Math.Max(0, (now - _time) / 1000);
            _time = now;
            if (!IsMoving) return;
            var frequency = 2 * Math.PI / (1.2 * 0.26);
            var decay = 0.88 * frequency;
            var damped = frequency * Math.Sqrt(1 - 0.88 * 0.88);
            var exp = Math.Exp(-decay * dt);
            var cos = Math.Cos(damped * dt);
            var sin = Math.Sin(damped * dt);
            var moving = false;
            for (var i = 0; i < 4; i++)
            {
                var x = _value[i] - _target[i];
                var b = (_velocity[i] + decay * x) / damped;
                var displacement = exp * (x * cos + b * sin);
                _velocity[i] = exp * ((b * damped - decay * x) * cos - (x * damped + decay * b) * sin);
                _value[i] = _target[i] + displacement;
                moving |= Math.Abs(displacement) > 0.005 || Math.Abs(_velocity[i]) > 0.01;
            }
            IsMoving = moving;
            if (!moving)
            {
                Array.Copy(_target, _value, 4);
                Array.Clear(_velocity);
            }
        }
    }
    // Arc's neutral accent with 9% selection fill and the shared gray clipboard/history wash.
    private const double FlashFadeInMilliseconds = 120;
    internal const double FlashDurationMilliseconds = 900;
    private static readonly IBrush Accent = new SolidColorBrush(Color.Parse("#353535"));
    private static readonly IBrush SelectionFill = new SolidColorBrush(Color.Parse("#17353535"));
    internal static readonly IBrush WashFill = new Avalonia.Media.Immutable.ImmutableSolidColorBrush(Color.Parse("#663A3A3A"));
    private static readonly Pen SelectionPen = new(Accent, 1);
    private static readonly Pen ActivePen = new(Accent, 2);
    private static readonly Avalonia.Animation.Easings.SplineEasing WashEasing = new(0, 0, 0.58, 1);

    internal static double GetFlashWash(double elapsed) => elapsed < FlashFadeInMilliseconds
        ? WashEasing.Ease(Math.Clamp(elapsed / FlashFadeInMilliseconds, 0, 1))
        : 1 - WashEasing.Ease(Math.Clamp(
            (elapsed - FlashFadeInMilliseconds) / (FlashDurationMilliseconds - FlashFadeInMilliseconds), 0, 1));

    public DataGridSelectionOverlay(DataGrid grid)
    {
        _grid = grid;
        ClipToBounds = true;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _timer.Tick += (_, _) =>
        {
            InvalidateVisual();
            var flashExpired = _clock.Elapsed.TotalMilliseconds - _flashStart >= FlashDurationMilliseconds;
            if (flashExpired) _flash.Clear();
            if (flashExpired && !_ranges.Values.Any(range => range.IsMoving) && _active?.IsMoving != true)
                _timer.Stop();
        };
    }

    public void SelectionChanged()
    {
        // Resolve geometry during rendering, after row arrangement. Repeated
        // notifications for the same target must not restart an in-flight spring.
        _selectionCacheDirty = true;
        if (TopLevel.GetTopLevel(this) != null) _timer.Start();
        InvalidateVisual();
    }

    private void UpdateSelectionCache(IReadOnlyCollection<DataGridCellPosition> selected)
    {
        _selectionCacheDirty = false;
        _selectionCacheInitialized = true;
        _hasSelectedCells = selected.Count > 0;
        _selectionIsRectangular = false;
        if (!_hasSelectedCells)
        {
            _minSelectedRow = _maxSelectedRow = 0;
            _minSelectedColumn = _maxSelectedColumn = 0;
            return;
        }

        var first = true;
        foreach (var position in selected)
        {
            if (first)
            {
                _minSelectedRow = _maxSelectedRow = position.RowIndex;
                _minSelectedColumn = _maxSelectedColumn = position.ColumnIndex;
                first = false;
            }
            else
            {
                _minSelectedRow = Math.Min(_minSelectedRow, position.RowIndex);
                _maxSelectedRow = Math.Max(_maxSelectedRow, position.RowIndex);
                _minSelectedColumn = Math.Min(_minSelectedColumn, position.ColumnIndex);
                _maxSelectedColumn = Math.Max(_maxSelectedColumn, position.ColumnIndex);
            }
        }

        var rowCount = (long)_maxSelectedRow - _minSelectedRow + 1;
        var columnCount = (long)_maxSelectedColumn - _minSelectedColumn + 1;
        _selectionIsRectangular = rowCount <= selected.Count && columnCount <= selected.Count &&
            rowCount * columnCount == selected.Count;
    }

    private void ClearSelectionCache()
    {
        _selectionCacheDirty = false;
        _selectionCacheInitialized = false;
        _hasSelectedCells = false;
        _selectionIsRectangular = false;
        _minSelectedRow = _maxSelectedRow = 0;
        _minSelectedColumn = _maxSelectedColumn = 0;
    }

    public void Flash(HashSet<DataGridCellPosition> cells)
    {
        _flash = cells;
        _flashStart = _clock.Elapsed.TotalMilliseconds;
        if (TopLevel.GetTopLevel(this) != null) _timer.Start();
        InvalidateVisual();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _timer.Stop();
        _flash.Clear();
        _ranges.Clear();
        _active = null;
        ClearSelectionCache();
        base.OnDetachedFromVisualTree(e);
    }

    internal void Reset()
    {
        _timer.Stop();
        _flash.Clear();
        _ranges.Clear();
        _active = null;
        ClearSelectionCache();
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        var now = _clock.Elapsed.TotalMilliseconds;
        if (now - _flashStart >= FlashDurationMilliseconds)
            _flash.Clear();
        if (_grid.SelectionUnit != DataGridSelectionUnit.Cell) return;
        var selected = _grid.SelectedCells;
        if (_selectionCacheDirty || !_selectionCacheInitialized)
            UpdateSelectionCache(selected);
        if (!_hasSelectedCells && _flash.Count == 0 && _ranges.Count == 0 && _active == null)
            return;
        var rectangles = new Dictionary<int, Rect>();
        var elapsed = Math.Max(0, now - _flashStart);
        var wash = GetFlashWash(elapsed);
        Rect? active = null;
        var activeRegion = 0;
        // Geometry belongs to logical cells, not recycled row controls. Keep spring
        // targets alive even while every selected row is outside the realized window.
        var columnRects = new Dictionary<int, (double X, double Width, int Region)>();
        var x = _grid.RowDragHandleOffset;
        var totalWidth = _grid.Columns.Sum(column => column.GetEffectiveWidth());
        var rightStart = Math.Max(0, _grid.Columns.Count - _grid.RightFrozenColumnCount);
        var rightX = Math.Min(_grid.RowDragHandleOffset + totalWidth - _grid.GetRightFrozenWidth() - _grid.HorizontalOffset,
            Math.Max(0, Bounds.Width - _grid.GetRightFrozenWidth()) + _grid.RowDragHandleOffset);
        for (var column = 0; column < _grid.Columns.Count; column++)
        {
            var width = _grid.Columns[column].GetEffectiveWidth();
            var region = column < _grid.LeftFrozenColumnCount ? 0 : column >= rightStart ? 2 : 1;
            var bodyX = region == 0 ? x : region == 2 ? rightX : x - _grid.HorizontalOffset;
            if (_grid.Columns[column].IsVisible) columnRects[column] = (bodyX, width, region);
            x += width;
            if (column >= rightStart) rightX += width;
        }

        var itemCount = _grid.GetItemCount();
        var rowHeight = _grid.RowHeight;
        var firstVisibleRow = 0;
        var lastVisibleRow = -1;
        if (itemCount > 0 && rowHeight > 0 && Bounds.Height > 0)
        {
            firstVisibleRow = Math.Max(0, (int)Math.Floor(_grid.VerticalOffset / rowHeight));
            lastVisibleRow = Math.Min(itemCount - 1,
                (int)Math.Ceiling((_grid.VerticalOffset + Bounds.Height) / rowHeight) - 1);
        }

        if (wash > 0 && firstVisibleRow <= lastVisibleRow)
        {
            foreach (var row in Enumerable.Range(firstVisibleRow, lastVisibleRow - firstVisibleRow + 1))
            {
                var y = row * rowHeight - _grid.VerticalOffset;
                var runColumn = -1;
                var previousColumn = -1;
                var runX = 0d;
                var runRight = 0d;
                var runRegion = 0;

                void DrawRun()
                {
                    if (runColumn < 0)
                        return;

                    var rect = new Rect(runX, y, runRight - runX, rowHeight);
                    using var clip = context.PushClip(RegionClip(runRegion));
                    using var opacity = context.PushOpacity(wash);
                    context.DrawRectangle(WashFill, null, rect);
                    runColumn = -1;
                }

                foreach (var column in columnRects.Keys.OrderBy(index => index))
                {
                    if (!_flash.Contains(new DataGridCellPosition(row, column)))
                    {
                        DrawRun();
                        previousColumn = -1;
                        continue;
                    }

                    var geometry = columnRects[column];
                    if (runColumn < 0 || column != previousColumn + 1 || geometry.Region != runRegion)
                    {
                        DrawRun();
                        runColumn = column;
                        runX = geometry.X;
                        runRegion = geometry.Region;
                    }

                    runRight = geometry.X + geometry.Width;
                    previousColumn = column;
                }

                DrawRun();
            }
        }

        if (_hasSelectedCells && _selectionIsRectangular && itemCount > 0 &&
            _minSelectedRow < itemCount && _maxSelectedRow >= 0)
        {
            var rangeStartRow = Math.Max(0, _minSelectedRow);
            var rangeEndRow = Math.Min(itemCount - 1, _maxSelectedRow);
            var rangeY = rangeStartRow * rowHeight - _grid.VerticalOffset;
            var rangeHeight = ((double)rangeEndRow - rangeStartRow + 1) * rowHeight;
            var firstColumn = Math.Max(0, _minSelectedColumn);
            var lastColumn = Math.Min(_grid.Columns.Count - 1, _maxSelectedColumn);
            for (var column = firstColumn; column <= lastColumn; column++)
            {
                if (!columnRects.TryGetValue(column, out var geometry))
                    continue;
                var rect = new Rect(geometry.X, rangeY, geometry.Width, rangeHeight);
                rectangles[geometry.Region] = rectangles.TryGetValue(geometry.Region, out var existing)
                    ? existing.Union(rect)
                    : rect;
            }
        }
        else if (_hasSelectedCells && firstVisibleRow <= lastVisibleRow)
        {
            var firstColumn = Math.Max(0, _minSelectedColumn);
            var lastColumn = Math.Min(_grid.Columns.Count - 1, _maxSelectedColumn);
            for (var row = firstVisibleRow; row <= lastVisibleRow; row++)
            {
                var y = row * rowHeight - _grid.VerticalOffset;
                for (var column = firstColumn; column <= lastColumn; column++)
                {
                    var position = new DataGridCellPosition(row, column);
                    if (!selected.Contains(position) ||
                        !columnRects.TryGetValue(column, out var geometry))
                        continue;
                    var rect = new Rect(geometry.X, y, geometry.Width, rowHeight);
                    using var clip = context.PushClip(RegionClip(geometry.Region));
                    context.DrawRectangle(_grid.IsEditing ? null : SelectionFill, SelectionPen,
                        rect.Deflate(0.5), 2, 2);
                }
            }
        }

        var activePosition = _grid.ActiveSelectionCell;
        if (activePosition is { } selectedActive && selected.Contains(selectedActive) &&
            selectedActive.RowIndex >= 0 && selectedActive.RowIndex < itemCount &&
            columnRects.TryGetValue(selectedActive.ColumnIndex, out var activeGeometry))
        {
            active = new Rect(activeGeometry.X,
                selectedActive.RowIndex * rowHeight - _grid.VerticalOffset,
                activeGeometry.Width, rowHeight);
            activeRegion = activeGeometry.Region;
        }

        foreach (var region in _ranges.Keys.Where(region => !rectangles.ContainsKey(region)).ToArray())
            _ranges.Remove(region);
        foreach (var (region, target) in rectangles)
        {
            // Do not resize the animated range when virtualization realizes a different row window.
            var content = ToContent(target, region);
            if (!_ranges.TryGetValue(region, out var motion))
                _ranges[region] = motion = new SelectionSpring(content, now);
            motion.Update(content, now);
            var rect = ToBody(motion.Value, region);
            using var clip = context.PushClip(RegionClip(region));
            if (rect.Width > 1 && rect.Height > 1)
                context.DrawRectangle(_grid.IsEditing ? null : SelectionFill, SelectionPen,
                    rect.Deflate(0.5), 2, 2);
        }
        if (active is { Width: > 2, Height: > 2 } activeRect)
        {
            var content = ToContent(activeRect, activeRegion);
            if (_active == null || _activeRegion != activeRegion)
                _active = new SelectionSpring(content, now);
            _activeRegion = activeRegion;
            _active.Update(content, now);
            using var clip = context.PushClip(RegionClip(activeRegion));
            context.DrawRectangle(null, ActivePen,
                ToBody(_active.Value, activeRegion).Deflate(1), 2, 2);
        }
        else _active = null;
        if (_ranges.Values.Any(range => range.IsMoving) || _active?.IsMoving == true)
            _timer.Start();
    }

    private Rect ToContent(Rect rect, int region) => new(
        rect.X + (region == 1 ? _grid.HorizontalOffset : region == 2 ? -Bounds.Width : 0),
        rect.Y + _grid.VerticalOffset, rect.Width, rect.Height);

    private Rect ToBody(Rect rect, int region) => new(
        rect.X - (region == 1 ? _grid.HorizontalOffset : region == 2 ? -Bounds.Width : 0),
        rect.Y - _grid.VerticalOffset, rect.Width, rect.Height);

    private Rect RegionClip(int region)
    {
        var left = _grid.GetLeftFrozenWidth() + _grid.RowDragHandleOffset;
        var right = Math.Max(left, Bounds.Width - _grid.GetRightFrozenWidth());
        return region switch
        {
            0 => new Rect(0, 0, Math.Min(left, Bounds.Width), Bounds.Height),
            2 => new Rect(right, 0, Math.Max(0, Bounds.Width - right), Bounds.Height),
            _ => new Rect(left, 0, Math.Max(0, right - left), Bounds.Height)
        };
    }
}

internal class DataGridScrollPanel : Panel
{
    internal DataGrid? OwningGrid { get; set; }

    private Border? _leftFrozenShadow;
    private Border? _rightFrozenShadow;

    protected override Size MeasureOverride(Size availableSize) => availableSize;

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (OwningGrid == null) return base.ArrangeOverride(finalSize);

        double hOff = OwningGrid.HorizontalOffset;
        double rowH = OwningGrid.RowHeight;
        double vpW = finalSize.Width;
        var columns = OwningGrid.Columns;
        int leftFrozen = Math.Min(OwningGrid.LeftFrozenColumnCount, columns.Count);
        int rightFrozen = Math.Min(OwningGrid.RightFrozenColumnCount, columns.Count);
        int totalCount = columns.Count;

        double leftFrozenW = 0;
        for (int i = 0; i < leftFrozen; i++)
            leftFrozenW += columns[i].GetEffectiveWidth();

        double rightFrozenW = 0;
        for (int i = totalCount - rightFrozen; i < totalCount; i++)
            rightFrozenW += columns[i].GetEffectiveWidth();

        double totalW = columns.Sum(c => c.GetEffectiveWidth());

        double vpH = finalSize.Height;
        int itemCount = OwningGrid.GetItemCount();
        double totalH = itemCount * rowH;
        double vOff = OwningGrid.VerticalOffset;
        if (totalH > vpH)
        {
            double maxV = totalH - vpH;
            if (vOff > maxV)
            {
                vOff = maxV;
                OwningGrid.SetVerticalOffset(vOff);
            }
        }
        else
        {
            if (vOff != 0)
            {
                vOff = 0;
                OwningGrid.SetVerticalOffset(0);
            }
        }

        double scrollableW = totalW - leftFrozenW - rightFrozenW;
        double scrollableVisible = vpW - leftFrozenW - rightFrozenW;
        if (scrollableVisible < 0) scrollableVisible = 0;
        double maxHOff = Math.Max(0, scrollableW - scrollableVisible);
        hOff = Math.Clamp(hOff, 0, maxHOff);

        EnsureFrozenShadows();

        double scrollableEnd = leftFrozenW + scrollableW - hOff;
        double rightFrozenMaxX = vpW - rightFrozenW;
        if (rightFrozenMaxX < 0) rightFrozenMaxX = 0;
        scrollableEnd = Math.Min(scrollableEnd, rightFrozenMaxX);

        bool hasLeftShadow = leftFrozen > 0 && scrollableW > scrollableVisible && hOff > 0;
        bool hasRightShadow = rightFrozen > 0 && scrollableW > scrollableVisible && hOff < maxHOff - 0.5;

        if (_leftFrozenShadow != null)
        {
            _leftFrozenShadow.IsVisible = hasLeftShadow;
            if (hasLeftShadow)
                _leftFrozenShadow.Arrange(new Rect(leftFrozenW - 6, 0, 6, finalSize.Height));
        }
        if (_rightFrozenShadow != null)
        {
            _rightFrozenShadow.IsVisible = hasRightShadow;
            if (hasRightShadow)
                _rightFrozenShadow.Arrange(new Rect(scrollableEnd - 1, 0, 6, finalSize.Height));
        }

        for (int i = 0; i < Children.Count; i++)
        {
            if (Children[i] is DataGridRow row && row.IsVisible && !ReferenceEquals(row, OwningGrid.DragPreviewRow))
            {
                double y = row.Index * rowH - vOff;
                row.Arrange(new Rect(0, y, vpW, rowH));
            }
        }

        foreach (var overlay in Children.OfType<DataGridSelectionOverlay>())
        {
            overlay.Arrange(new Rect(finalSize));
            overlay.InvalidateVisual();
        }
        OwningGrid.ArrangeDragPreview(this, finalSize, vOff);
        OwningGrid.ArrangeRowDragFeedback(this, finalSize);
        return finalSize;
    }

    private void EnsureFrozenShadows()
    {
        if (_leftFrozenShadow != null) return;

        _leftFrozenShadow = new Border
        {
            IsVisible = false,
            Background = new SolidColorBrush(Colors.Transparent),
            BoxShadow = new BoxShadows(new BoxShadow
            {
                OffsetX = 3,
                Blur = 6,
                Color = Color.Parse("#18000000"),
            }),
            IsHitTestVisible = false,
            ZIndex = 5,
        };
        _rightFrozenShadow = new Border
        {
            IsVisible = false,
            Background = new SolidColorBrush(Colors.Transparent),
            BoxShadow = new BoxShadows(new BoxShadow
            {
                OffsetX = -3,
                Blur = 6,
                Color = Color.Parse("#18000000"),
            }),
            IsHitTestVisible = false,
            ZIndex = 5,
        };
        Children.Add(_leftFrozenShadow);
        Children.Add(_rightFrozenShadow);
    }
}

public class DataGridColumnEventArgs : EventArgs
{
    public DataGridColumn Column { get; }
    public DataGridColumnEventArgs(DataGridColumn c) => Column = c;
}

public class DataGridPreparingCellForEditEventArgs : RoutedEventArgs
{
    public DataGridColumn Column { get; }
    public DataGridRow Row { get; }
    public RoutedEventArgs EditingEventArgs { get; }
    public Control EditingElement { get; }

    public DataGridPreparingCellForEditEventArgs(
        DataGridColumn column,
        DataGridRow row,
        RoutedEventArgs editingEventArgs,
        Control editingElement,
        RoutedEvent? routedEvent = null,
        object? source = null)
        : base(routedEvent, source)
    {
        Column = column;
        Row = row;
        EditingEventArgs = editingEventArgs;
        EditingElement = editingElement;
    }
}

public sealed class DataGridRowReorderedEventArgs : EventArgs
{
    public object? Item { get; }
    public int OldIndex { get; }
    public int NewIndex { get; }
    public bool WasAppliedToItemsSource { get; }

    public DataGridRowReorderedEventArgs(object? item, int oldIndex, int newIndex, bool wasAppliedToItemsSource)
    {
        Item = item;
        OldIndex = oldIndex;
        NewIndex = newIndex;
        WasAppliedToItemsSource = wasAppliedToItemsSource;
    }
}

// Spreadsheet clipboard codec: quoted TSV plus UTF-8 HTML/CF_HTML; no reflection or external parser.
internal static class DataGridClipboardText
{
    public static string ToTsv(IReadOnlyList<IReadOnlyList<string>> matrix) =>
        string.Join("\n", matrix.Select(row => string.Join("\t", row.Select(value =>
            value.IndexOfAny(['\t', '\r', '\n', '"']) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value))));

    public static List<List<string>> ParseTsv(string text)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (quoted)
            {
                if (ch == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                    else quoted = false;
                }
                else cell.Append(ch);
                continue;
            }
            if (ch == '"' && cell.Length == 0) quoted = true;
            else if (ch == '\t') { row.Add(cell.ToString()); cell.Clear(); }
            else if (ch is '\n' or '\r')
            {
                if (ch == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                row.Add(cell.ToString()); cell.Clear(); rows.Add(row); row = new List<string>();
            }
            else cell.Append(ch);
        }
        if (row.Count > 0 || cell.Length > 0 || rows.Count == 0 || text.EndsWith('\t') || text.EndsWith('"'))
        { row.Add(cell.ToString()); rows.Add(row); }
        return rows;
    }

    public static string ToHtml(IReadOnlyList<IReadOnlyList<string>> matrix)
    {
        var fragment = "<meta charset=\"utf-8\"><table><tbody>" + string.Concat(matrix.Select(row =>
            "<tr>" + string.Concat(row.Select(cell => "<td style=\"white-space:pre-wrap\">" + WebUtility.HtmlEncode(cell)
                .Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", "<br>") + "</td>")) + "</tr>")) + "</tbody></table>";
        if (!OperatingSystem.IsWindows()) return fragment;
        const string header = "Version:0.9\r\nStartHTML:{0:D10}\r\nEndHTML:{1:D10}\r\nStartFragment:{2:D10}\r\nEndFragment:{3:D10}\r\n";
        const string prefix = "<html><body><!--StartFragment-->";
        const string suffix = "<!--EndFragment--></body></html>";
        var start = Encoding.UTF8.GetByteCount(string.Format(CultureInfo.InvariantCulture, header, 0, 0, 0, 0));
        var begin = start + Encoding.UTF8.GetByteCount(prefix);
        var end = begin + Encoding.UTF8.GetByteCount(fragment);
        return string.Format(CultureInfo.InvariantCulture, header, start, end + Encoding.UTF8.GetByteCount(suffix), begin, end) + prefix + fragment + suffix;
    }

    public static List<List<string>>? ParseHtml(string html)
    {
        // A bounded, non-executing tokenizer: ignore markup, preserve cell text and explicit line breaks.
        var rows = new List<List<string>>();
        List<string>? row = null;
        StringBuilder? cell = null;
        var depth = 0;
        var span = 1;
        for (var i = html.IndexOf("<table", StringComparison.OrdinalIgnoreCase); i >= 0 && i < html.Length;)
        {
            if (html[i] != '<')
            {
                var end = html.IndexOf('<', i);
                if (end < 0) end = html.Length;
                cell?.Append(WebUtility.HtmlDecode(html[i..end]));
                i = end; continue;
            }
            if (html.AsSpan(i).StartsWith("<!--"))
            {
                var end = html.IndexOf("-->", i + 4, StringComparison.Ordinal);
                i = end < 0 ? html.Length : end + 3; continue;
            }
            var close = html.IndexOf('>', i + 1);
            if (close < 0) break;
            var tag = html[(i + 1)..close].Trim();
            var closing = tag.StartsWith('/');
            var name = tag.TrimStart('/').Split([' ', '\t', '\r', '\n', '/'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.ToLowerInvariant();
            i = close + 1;
            if (name is "script" or "style" && !closing)
            {
                var end = html.IndexOf("</" + name, i, StringComparison.OrdinalIgnoreCase);
                i = end < 0 ? html.Length : end; continue;
            }
            if (name == "table")
            {
                depth += closing ? -1 : 1;
                if (depth == 0) break;
            }
            if (depth != 1) continue;
            if (name is "td" or "th" or "tr")
            {
                if (cell != null && row != null)
                {
                    row.Add(cell.ToString());
                    for (var c = 1; c < span; c++) row.Add(string.Empty);
                    cell = null;
                }
                if (name == "tr")
                {
                    if (row != null) rows.Add(row);
                    row = closing ? null : new List<string>();
                }
                else if (!closing)
                {
                    row ??= new List<string>(); cell = new StringBuilder(); span = 1;
                    var at = tag.IndexOf("colspan", StringComparison.OrdinalIgnoreCase);
                    if (at >= 0)
                    {
                        var value = tag[(at + 7)..].TrimStart(' ', '=', '\'', '"');
                        var digits = new string(value.TakeWhile(char.IsAsciiDigit).ToArray());
                        if (int.TryParse(digits, out var count)) span = Math.Clamp(count, 1, 1000);
                    }
                }
            }
            else if (name == "br") cell?.Append('\n');
        }
        if (cell != null && row != null) row.Add(cell.ToString());
        if (row != null) rows.Add(row);
        return rows.Count > 0 ? rows : null;
    }
}

public enum DataGridEditAction { Commit, Cancel }

public class DataGridCellEditEndingEventArgs : EventArgs
{
    public DataGridCell Cell { get; }
    public DataGridColumn? Column { get; }
    public DataGridEditAction EditAction { get; }
    public bool Cancel { get; set; }
    public DataGridCellEditEndingEventArgs(DataGridCell cell, DataGridColumn? column, DataGridEditAction action)
    { Cell = cell; Column = column; EditAction = action; }
}