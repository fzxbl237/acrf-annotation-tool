﻿using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Path = Avalonia.Controls.Shapes.Path;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Styling;
using PatChes.Controls.DataGrid.Searching;

namespace PatChes.Controls.DataGrid;

/// <summary>
/// Represents a cell in a DataGrid row. Manages display/edit element lifecycle.
/// Follows ProDataGrid's DataGridCell pattern with displayElement/editElement swap.
/// </summary>
public class DataGridCell : Control
{
    public DataGridCell()
    {
        Focusable = true;
    }

    public static readonly StyledProperty<DataGridColumn?> ColumnProperty =
        AvaloniaProperty.Register<DataGridCell, DataGridColumn?>(nameof(Column));
    public static readonly StyledProperty<object?> DataItemProperty =
        AvaloniaProperty.Register<DataGridCell, object?>(nameof(DataItem));
    public static readonly StyledProperty<bool> IsSelectedProperty =
        AvaloniaProperty.Register<DataGridCell, bool>(nameof(IsSelected));
    public static readonly StyledProperty<bool> IsEditingProperty =
        AvaloniaProperty.Register<DataGridCell, bool>(nameof(IsEditing));
    public static readonly StyledProperty<bool> IsValidProperty =
        AvaloniaProperty.Register<DataGridCell, bool>(nameof(IsValid), true);
    public static readonly StyledProperty<DataGridValidationSeverity> ValidationSeverityProperty =
        AvaloniaProperty.Register<DataGridCell, DataGridValidationSeverity>(nameof(ValidationSeverity));
    public new static readonly StyledProperty<double> WidthProperty =
        AvaloniaProperty.Register<DataGridCell, double>(nameof(Width));

    private Border? _border;
    private Control? _displayElement;
    private Control? _editingElement;
    private object? _uneditedValue;
    private bool _built;

    // Validation icon (ProDataGrid Path-based style)
    private Grid? _innerPanel;
    private Border? _errorIconBorder;
    private Path? _errorIcon;
    private string? _validationMessage;

    private const string ValidationIconPathData = "M14,7 A7,7 0 0,0 0,7 M0,7 A7,7 0 1,0 14,7 M7,3l0,5 M7,9l0,2";
    private const string EditingTextBoxClass = "mini-editing-textbox";
    private const string EditingComboBoxClass = "mini-editing-combobox";

    public DataGridColumn? Column { get => GetValue(ColumnProperty); set => SetValue(ColumnProperty, value); }
    public object? DataItem { get => GetValue(DataItemProperty); set => SetValue(DataItemProperty, value); }
    public bool IsSelected { get => GetValue(IsSelectedProperty); set { SetValue(IsSelectedProperty, value); UpdateBg(); } }
    public bool IsEditing { get => GetValue(IsEditingProperty); set { SetValue(IsEditingProperty, value); OnIsEditingChanged(); } }
    public bool IsValid { get => GetValue(IsValidProperty); set { SetValue(IsValidProperty, value); UpdateBg(); } }
    public DataGridValidationSeverity ValidationSeverity { get => GetValue(ValidationSeverityProperty); set { SetValue(ValidationSeverityProperty, value); UpdateBg(); } }
    public new double Width { get => GetValue(WidthProperty); set => SetValue(WidthProperty, value); }
    public DataGridRow? OwningRow { get; internal set; }
    public bool IsEditingInternal => IsEditing;

    /// <summary>
    /// The current content control displayed in the cell (display or edit element).
    /// </summary>
    public Control? ContentControl { get; internal set; }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (_border != null) { _border.Measure(availableSize); return _border.DesiredSize; }
        return new Size(0, 0);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _border?.Arrange(new Rect(finalSize));
        return finalSize;
    }

    public void EnsureBuilt()
    {
        if (_built && _border != null) return;
        if (_border == null)
        {
            _border = new Border { ClipToBounds = true };
            LogicalChildren.Add(_border);
            VisualChildren.Add(_border);
        }
        _displayElement = Column?.GenerateElement(this, DataItem);
        if (_displayElement != null && Column != null && DataItem != null)
        {
            _displayElement = FloatingBar.Build(Column, this, _displayElement, DataItem);
            ContentControl = _displayElement;
            BuildInnerContent();
        }
        _built = true;
        UpdateBg();
        UpdateSearchHighlight();
    }

    private void BuildInnerContent()
    {
        if (_border == null) return;

        _errorIcon = new Path
        {
            Data = StreamGeometry.Parse(ValidationIconPathData),
            Width = 14,
            Height = 14,
            StrokeThickness = 1.5,
            IsVisible = false,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 0, 0),
        };

        _errorIconBorder = new Border
        {
            Width = 22,
            IsVisible = false,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch,
            IsHitTestVisible = true,
            ZIndex = 1,
            Child = _errorIcon,
        };
        _errorIconBorder.PointerPressed += (_, e) =>
        {
            if (_validationMessage != null)
            {
                var tip = ToolTip.GetTip(this);
                if (tip != null)
                    ToolTip.SetIsOpen(this, true);
            }
            e.Handled = true;
        };

        _innerPanel = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("22,*"),
        };
        if (_displayElement != null)
        {
            Grid.SetColumn(_displayElement, 1);
            _innerPanel.Children.Add(_displayElement);
        }
        Grid.SetColumn(_errorIconBorder, 0);
        _innerPanel.Children.Add(_errorIconBorder);

        _border.Child = _innerPanel;
        UpdateValidationIcon();
    }

    public void ResetDisplay()
    {
        if (_border == null) return;
        _displayElement = Column?.GenerateElement(this, DataItem);
        if (_displayElement != null && Column != null && DataItem != null)
        {
            _displayElement = FloatingBar.Build(Column, this, _displayElement, DataItem);
            ContentControl = _displayElement;
            BuildInnerContent();
        }
        UpdateBg();
    }

    /// <summary>
    /// Enters editing mode: swaps display element for editing element.
    /// </summary>
    public void BeginEdit(RoutedEventArgs? editingEventArgs = null)
    {
        if (Column == null || Column.IsReadOnly || IsEditing) return;
        _editingElement = Column.GenerateEditingElement(this, DataItem);
        if (_editingElement == null) return;

        // Suppress TextBox/ComboBox built-in DataValidationErrors display (red border + error text).
        // Apply a class so the style-based selectors in PatChes.Controls.DataGridThemes.axaml take effect.
        if (_editingElement is TextBox tb)
        {
            tb.Classes.Add(EditingTextBoxClass);
        }
        else if (_editingElement is ComboBox cb)
        {
            cb.Classes.Add(EditingComboBoxClass);
        }

        _uneditedValue = Column.PrepareCellForEdit(_editingElement, editingEventArgs);

        // During editing, bypass the grid and put editing element directly in the border
        if (_border != null)
        {
            _border.Child = _editingElement;
            ContentControl = _editingElement;
        }
        IsEditing = true;
        _editingElement.Focus();
    }

    /// <summary>
    /// Commits the current edit and returns to display mode.
    /// </summary>
    public object? CommitEdit()
    {
        if (!IsEditing || _editingElement == null || Column == null) return null;
        var value = Column.CommitCellEdit(_editingElement);
        IsEditing = false;
        SwapToDisplayElement();
        return value;
    }

    /// <summary>
    /// Cancels the current edit, reverts to unedited value, returns to display mode.
    /// </summary>
    public void CancelEdit()
    {
        if (!IsEditing || _editingElement == null || Column == null) return;
        Column.CancelCellEdit(_editingElement, _uneditedValue);
        IsEditing = false;
        SwapToDisplayElement();
    }

    private void SwapToDisplayElement()
    {
        if (_border == null) return;
        _displayElement = Column?.GenerateElement(this, DataItem);
        if (_displayElement != null && Column != null && DataItem != null)
        {
            _displayElement = FloatingBar.Build(Column, this, _displayElement, DataItem);
            ContentControl = _displayElement;
            BuildInnerContent();
        }
        _editingElement = null;
        _uneditedValue = null;
    }

    private void OnIsEditingChanged()
    {
        UpdateValidationIcon();
        UpdateBg();
    }

    internal void UpdateSearchHighlight()
    {
        UpdateBg();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        EnsureBuilt();
        AddHandler(DoubleTappedEvent, OnDoubleTapped);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (IsEditing)
        {
            if (Column is DataGridAutoCompleteColumn) return;
            e.Handled = true;
            return;
        }
        if (OwningRow?.OwningGrid != null && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            OwningRow.OwningGrid.OnCellPressed(this, e);
    }

    private void OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (OwningRow?.OwningGrid != null && !IsEditing && Column is { IsReadOnly: false } && DataItem != null)
        {
            if (Column is DataGridCheckBoxColumn) return;
            OwningRow.OwningGrid.BeginEdit(this);
            e.Handled = true;
        }
    }

    internal void SetValidationMessage(string? message, DataGridValidationSeverity severity, Color iconColor)
    {
        if (_validationMessage == message && ValidationSeverity == severity)
            return;

        _validationMessage = message;
        ValidationSeverity = severity;
        UpdateValidationIcon();
    }

    private static void SuppressTextBoxValidation(Control ctrl)
    {
        DataValidationErrors.SetErrors(ctrl, null);
    }

    private void UpdateValidationIcon()
    {
        if (_errorIconBorder == null || _errorIcon == null) return;

        bool hasSeverity = ValidationSeverity != DataGridValidationSeverity.None && !string.IsNullOrEmpty(_validationMessage);

        _errorIconBorder.IsVisible = hasSeverity && !IsEditing;
        _errorIcon.IsVisible = hasSeverity && !IsEditing;
        if (_innerPanel != null && _innerPanel.ColumnDefinitions.Count > 0)
            _innerPanel.ColumnDefinitions[0].Width = new GridLength(hasSeverity ? 22 : 0, GridUnitType.Pixel);
        InvalidateMeasure();
        InvalidateArrange();

        if (!hasSeverity)
        {
            ToolTip.SetTip(this, null);
            return;
        }

        var iconColor = ValidationSeverity switch
        {
            DataGridValidationSeverity.InValid => Color.Parse("#f56c6c"),
            DataGridValidationSeverity.Error => Color.Parse("#f56c6c"),
            DataGridValidationSeverity.Warning => Color.Parse("#e6a23c"),
            DataGridValidationSeverity.Info => Color.Parse("#0078D4"),
            _ => Colors.Transparent,
        };
        _errorIcon.Stroke = new SolidColorBrush(iconColor);
        ToolTip.SetTip(this, CreateValidationToolTip(iconColor));
    }

    private ToolTip CreateValidationToolTip(Color iconColor)
    {
        var bgColor = ValidationSeverity switch
        {
            DataGridValidationSeverity.InValid => Color.Parse("#fab6b6"),
            DataGridValidationSeverity.Error => Color.Parse("#fab6b6"),
            DataGridValidationSeverity.Warning => Color.Parse("#f3d19e"),
            DataGridValidationSeverity.Info => Color.Parse("#EEF6FF"),
            _ => Color.Parse("#f3d19e"),
        };
        var borderColor = ValidationSeverity switch
        {
            DataGridValidationSeverity.InValid => Color.Parse("#f56c6c"),
            DataGridValidationSeverity.Error => Color.Parse("#f56c6c"),
            DataGridValidationSeverity.Warning => Color.Parse("#e6a23c"),
            DataGridValidationSeverity.Info => Color.Parse("#0078D4"),
            _ => Color.Parse("#e6a23c"),
        };
        var msgFgColor = Color.Parse("#303133");

        var pathIcon = new Path
        {
            Data = StreamGeometry.Parse(ValidationIconPathData),
            Width = 14,
            Height = 14,
            Stroke = new SolidColorBrush(iconColor),
            StrokeThickness = 1.5,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
        var message = new TextBlock
        {
            Text = _validationMessage ?? string.Empty,
            Foreground = new SolidColorBrush(msgFgColor),
            FontSize = 14,
            FontWeight = FontWeight.Bold,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 260,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("20,*"),
            ColumnSpacing = 6,
        };
        grid.Children.Add(pathIcon);
        grid.Children.Add(message);
        Grid.SetColumn(pathIcon, 0);
        Grid.SetColumn(message, 1);

        return new ToolTip
        {
            Content = grid,
            Background = new SolidColorBrush(bgColor),
            BorderBrush = new SolidColorBrush(borderColor),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8),
        };
    }

    internal void UpdateBg()
    {
        if (_border == null) return;
        bool showVertical = OwningRow?.OwningGrid?.GridLinesVisibility is
            DataGridGridLinesVisibility.Vertical or DataGridGridLinesVisibility.Both;
        bool showHorizontal = OwningRow?.OwningGrid?.GridLinesVisibility is
            DataGridGridLinesVisibility.Horizontal or DataGridGridLinesVisibility.Both;
        double right = showVertical ? 0.5 : 0;
        double bottom = showHorizontal ? 0.5 : 0;

        if (IsEditing)
        {
            _border.Background = new SolidColorBrush(Colors.White);
            _border.BorderBrush = Brushes.Transparent;
            _border.BorderThickness = new Thickness(0);
        }
        else if (IsSelected)
        {
            _border.Background = ValidationSeverity switch
            {
                DataGridValidationSeverity.InValid or DataGridValidationSeverity.Error =>
                    new SolidColorBrush(Color.Parse("#fab6b6")),
                DataGridValidationSeverity.Warning =>
                    new SolidColorBrush(Color.Parse("#f3d19e")),
                DataGridValidationSeverity.Info =>
                    new SolidColorBrush(Color.Parse("#EEF6FF")),
                _ => new SolidColorBrush(Color.Parse("#D6EBFF")),
            };
            _border.BorderBrush = new SolidColorBrush(Color.Parse("#0078D4"));
            _border.BorderThickness = OwningRow?.OwningGrid?.GetCellSelectionBorderThickness(this)
                ?? new Thickness(0.5);
        }
        else if (ValidationSeverity == DataGridValidationSeverity.InValid)
        {
            _border.Background = new SolidColorBrush(Color.Parse("#fab6b6"));
            _border.BorderBrush = new SolidColorBrush(Color.Parse("#f56c6c"));
            _border.BorderThickness = new Thickness(0.5, 0.5, right, bottom);
        }
        else if (ValidationSeverity == DataGridValidationSeverity.Error)
        {
            _border.Background = new SolidColorBrush(Color.Parse("#fab6b6"));
            _border.BorderBrush = new SolidColorBrush(Color.Parse("#f56c6c"));
            _border.BorderThickness = new Thickness(0.5, 0.5, right, bottom);
        }
        else if (ValidationSeverity == DataGridValidationSeverity.Warning)
        {
            _border.Background = new SolidColorBrush(Color.Parse("#f3d19e"));
            _border.BorderBrush = new SolidColorBrush(Color.Parse("#e6a23c"));
            _border.BorderThickness = new Thickness(0.5, 0.5, right, bottom);
        }
        else if (ValidationSeverity == DataGridValidationSeverity.Info)
        {
            _border.Background = new SolidColorBrush(Color.Parse("#EEF6FF"));
            _border.BorderBrush = new SolidColorBrush(Color.Parse("#0078D4"));
            _border.BorderThickness = new Thickness(0.5, 0.5, right, bottom);
        }
        else if (IsFrozenCell())
        {
            var grid = OwningRow?.OwningGrid;
            _border.Background = OwningRow?.IsPointerOver == true
                ? grid?.RowHoverBackground ?? Brushes.Transparent
                : grid?.RowBackground ?? new SolidColorBrush(Colors.White);
            _border.BorderBrush = new SolidColorBrush(Color.Parse("#E8E8E8"));
            _border.BorderThickness = new Thickness(0, 0, right, bottom);
        }
        else
        {
            var grid = OwningRow?.OwningGrid;
            _border.Background = OwningRow?.IsPointerOver == true
                ? grid?.RowHoverBackground ?? Brushes.Transparent
                : Brushes.Transparent;
            _border.BorderBrush = new SolidColorBrush(Color.Parse("#E8E8E8"));
            _border.BorderThickness = new Thickness(0, 0, right, bottom);
        }

        ApplySearchHighlight();
    }

    private void ApplySearchHighlight()
    {
        if (_border == null) return;
        var grid = OwningRow?.OwningGrid;
        if (grid?.EffectiveSearchHighlightMode is not (Searching.SearchHighlightMode.Cell or Searching.SearchHighlightMode.TextAndCell))
            return;
        var matches = grid.GetCellSearchMatches(this);
        if (matches is { Count: > 0 })
        {
            var isCurrent = grid.GetCellSearchResult(this) != null;
            _border.Background = new SolidColorBrush(Color.Parse(isCurrent ? "#FFE082" : "#FFF3CD"));
        }
    }

    private bool IsFrozenCell()
    {
        var grid = OwningRow?.OwningGrid;
        if (grid == null || Column == null) return false;
        int idx = grid.Columns.IndexOf(Column);
        return idx < grid.LeftFrozenColumnCount || idx >= grid.Columns.Count - grid.RightFrozenColumnCount;
    }
}