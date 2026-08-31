using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace PatChes.Controls.DataGrid;

/// <summary>
/// Base class for DataGrid columns. Defines common properties and editing lifecycle methods.
/// Follows the ProDataGrid DataGridColumn pattern with GenerateElement/GenerateEditingElement split.
/// </summary>
public abstract class DataGridColumn : AvaloniaObject
{
    internal static readonly IBrush DefaultCellTextForeground =
        new SolidColorBrush(Color.Parse("#181D1F"));

    public static readonly StyledProperty<string?> HeaderProperty =
        AvaloniaProperty.Register<DataGridColumn, string?>(nameof(Header));
    public static readonly StyledProperty<GridLength> WidthProperty =
        AvaloniaProperty.Register<DataGridColumn, GridLength>(nameof(Width), new GridLength(120, GridUnitType.Pixel));
    public static readonly StyledProperty<double> MinWidthProperty =
        AvaloniaProperty.Register<DataGridColumn, double>(nameof(MinWidth), 20);
    public static readonly StyledProperty<double> MaxWidthProperty =
        AvaloniaProperty.Register<DataGridColumn, double>(nameof(MaxWidth), 65536);
    public static readonly StyledProperty<bool> IsReadOnlyProperty =
        AvaloniaProperty.Register<DataGridColumn, bool>(nameof(IsReadOnly));
    public static readonly StyledProperty<bool> IsFrozenProperty =
        AvaloniaProperty.Register<DataGridColumn, bool>(nameof(IsFrozen));
    public static readonly StyledProperty<IDataTemplate?> CellTemplateProperty =
        AvaloniaProperty.Register<DataGridColumn, IDataTemplate?>(nameof(CellTemplate));
    public static readonly StyledProperty<IDataTemplate?> EditingTemplateProperty =
        AvaloniaProperty.Register<DataGridColumn, IDataTemplate?>(nameof(EditingTemplate));
    public static readonly StyledProperty<bool> IsVisibleProperty =
        AvaloniaProperty.Register<DataGridColumn, bool>(nameof(IsVisible), true);
    public static readonly StyledProperty<bool> CanUserHideProperty =
        AvaloniaProperty.Register<DataGridColumn, bool>(nameof(CanUserHide), true);

    public string? Header { get => GetValue(HeaderProperty); set => SetValue(HeaderProperty, value); }
    public GridLength Width { get => GetValue(WidthProperty); set => SetValue(WidthProperty, value); }
    internal double ActualWidth { get; private set; } = 120;
    internal void SetActualWidth(double width) => ActualWidth = Math.Clamp(width, MinWidth, MaxWidth);
    public double MinWidth { get => GetValue(MinWidthProperty); set => SetValue(MinWidthProperty, value); }
    public double MaxWidth { get => GetValue(MaxWidthProperty); set => SetValue(MaxWidthProperty, value); }
    public bool IsReadOnly { get => GetValue(IsReadOnlyProperty); set => SetValue(IsReadOnlyProperty, value); }
    public bool IsFrozen { get => GetValue(IsFrozenProperty); set => SetValue(IsFrozenProperty, value); }
    public IDataTemplate? CellTemplate { get => GetValue(CellTemplateProperty); set => SetValue(CellTemplateProperty, value); }
    public IDataTemplate? EditingTemplate { get => GetValue(EditingTemplateProperty); set => SetValue(EditingTemplateProperty, value); }
    public bool IsVisible { get => GetValue(IsVisibleProperty); set => SetValue(IsVisibleProperty, value); }
    public bool CanUserHide { get => GetValue(CanUserHideProperty); set => SetValue(CanUserHideProperty, value); }
    public DataGrid? DataGridOwner { get; internal set; }
    public int Index { get; internal set; } = -1;

    public bool IsFilterable { get; set; } = true;
    public bool IsFiltered => _activeFilterValues != null && _activeFilterValues.Count > 0;
    private HashSet<string>? _activeFilterValues;

    public void SetFilter(HashSet<string> values)
    {
        _activeFilterValues = values;
        DataGridOwner?.OnFilterChanged(this);
    }

    public void ClearFilter()
    {
        if (_activeFilterValues == null)
            return;

        _activeFilterValues = null;
        DataGridOwner?.OnFilterChanged(this);
    }

    internal void ClearFilterSilent()
    {
        _activeFilterValues = null;
    }

    public bool PassesFilter(object? dataItem)
    {
        if (_activeFilterValues == null || _activeFilterValues.Count == 0) return true;
        var value = GetFilterValue(dataItem);
        return _activeFilterValues.Contains(value);
    }

    internal string GetFilterValue(object? dataItem)
    {
        if (dataItem == null) return "";

        if (this is DataGridBoundColumn bound && !string.IsNullOrEmpty(bound.BindingPath))
        {
            var prop = dataItem.GetType().GetProperty(bound.BindingPath);
            if (prop != null)
            {
                var v = prop.GetValue(dataItem);
                return v?.ToString() ?? "";
            }
        }

        return dataItem.ToString() ?? "";
    }

    /// <summary>
    /// Generates the read-only display element for a cell.
    /// </summary>
    public abstract Control GenerateElement(DataGridCell cell, object? dataItem);

    /// <summary>
    /// Generates the editable element for a cell. Called when entering edit mode.
    /// </summary>
    public virtual Control? GenerateEditingElement(DataGridCell cell, object? dataItem)
    {
        return null;
    }

    /// <summary>
    /// Called when the cell enters editing mode. Returns the unedited value for undo support.
    /// </summary>
    public virtual object? PrepareCellForEdit(Control editingElement, RoutedEventArgs? editingEventArgs)
    {
        return null;
    }

    /// <summary>
    /// Called when the cell edit is cancelled. Reverts the editing element to its unedited value.
    /// </summary>
    public virtual void CancelCellEdit(Control editingElement, object? uneditedValue)
    {
    }

    /// <summary>
    /// Called to commit the cell edit. Reads the current value from the editing element.
    /// </summary>
    public virtual object? CommitCellEdit(Control editingElement)
    {
        return null;
    }

    /// <summary>
    /// Gets the effective width clamped between MinWidth and MaxWidth.
    /// Hidden columns contribute zero width so the layout collapses them.
    /// </summary>
    internal double GetEffectiveWidth()
    {
        if (!IsVisible) return 0;
        return Width.IsStar ? ActualWidth : Math.Clamp(Width.Value, MinWidth, MaxWidth);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WidthProperty || change.Property == MinWidthProperty || change.Property == MaxWidthProperty)
        {
            DataGridOwner?.OnColumnWidthChanged(this);
        }
        else if (change.Property == IsVisibleProperty)
        {
            DataGridOwner?.OnColumnVisibilityChanged(this);
        }
    }
}
