using System.Diagnostics;
using System.Linq;
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
using Avalonia.Threading;
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

    // Arc light neutral: MixOklab(#FFFFFF, #353535, 0.05).
    private static readonly IBrush SelectedRowBackground = new ImmutableSolidColorBrush(Color.Parse("#F4F4F4"));

    private static readonly IBrush GridLineBrush = new ImmutableSolidColorBrush(Color.Parse("#E8E8E8"));
    private static readonly IBrush SearchMatchBackground = new ImmutableSolidColorBrush(Color.Parse("#FFF3CD"));
    private static readonly IBrush CurrentSearchMatchBackground = new ImmutableSolidColorBrush(Color.Parse("#FFE082"));

    private Border? _border;
    private Control? _displayElement;
    private Control? _editingElement;
    private object? _uneditedValue;
    private bool _built;

    // Undo/redo feedback: a dark grey wash painted over the cell content. The cell owns it so a
    // viewport refresh during a history replay cannot drop it, and it is drawn instead of being
    // composited, so every frame is guaranteed to land.
    private HistoryFlashLayer? _historyFlashLayer;
    private DispatcherTimer? _historyFlashTimer;
    private long _historyFlashStarted;
    private double _historyFlashWash;
    private const double HistoryFlashFadeInMilliseconds = 120;
    private const double HistoryFlashHoldMilliseconds = 320;
    private const double HistoryFlashFadeOutMilliseconds = 880;
    private const double HistoryFlashDurationMilliseconds =
        HistoryFlashFadeInMilliseconds + HistoryFlashHoldMilliseconds + HistoryFlashFadeOutMilliseconds;
    private const byte HistoryFlashAlpha = 0x66;
    private static readonly Color HistoryFlashColor = Color.Parse("#3A3A3A");
    private static readonly Avalonia.Animation.Easings.SplineEasing HistoryFlashFadeOutEasing = new(0.42, 0, 1, 1);

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
    public bool IsSelected { get => GetValue(IsSelectedProperty); set { if (IsSelected == value) return; SetValue(IsSelectedProperty, value); UpdateBg(); } }
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

        // A child of the content grid: it is painted after the displayed value in the same pass, so
        // neither the border background nor a display/edit swap can hide the wash.
        _historyFlashLayer = new HistoryFlashLayer(this) { IsHitTestVisible = false };
        Grid.SetColumn(_historyFlashLayer, 0);
        Grid.SetColumnSpan(_historyFlashLayer, 2);
        _innerPanel.Children.Add(_historyFlashLayer);

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
        AutoCompleteDiagnostics.Log($"Cell.BeginEdit.start cell={GetHashCode():X8} column={Column?.GetType().Name ?? "<null>"} dataItem={DataItem?.GetType().Name ?? "<null>"} readOnly={Column?.IsReadOnly ?? false} editing={IsEditing}");
        if (Column == null || Column.IsReadOnly || IsEditing)
        {
            AutoCompleteDiagnostics.Log($"Cell.BeginEdit.skip cell={GetHashCode():X8} reason=invalid-state");
            return;
        }

        _editingElement = Column.GenerateEditingElement(this, DataItem);
        AutoCompleteDiagnostics.Log($"Cell.BeginEdit.generated cell={GetHashCode():X8} element={_editingElement?.GetType().Name ?? "<null>"}");
        if (_editingElement == null)
        {
            AutoCompleteDiagnostics.Log($"Cell.BeginEdit.skip cell={GetHashCode():X8} reason=no-editing-element");
            return;
        }

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
        AutoCompleteDiagnostics.Log($"Cell.BeginEdit.prepared cell={GetHashCode():X8} uneditedType={_uneditedValue?.GetType().Name ?? "<null>"} border={_border != null}");

        // During editing, bypass the grid and put editing element directly in the border
        if (_border != null)
        {
            _border.ClipToBounds = Column is not DataGridAutoCompleteColumn;
            _border.Child = _editingElement;
            ContentControl = _editingElement;
            AutoCompleteDiagnostics.Log($"Cell.BeginEdit.mounted cell={GetHashCode():X8} borderChild={_border.Child.GetType().Name}");
        }
        IsEditing = true;
        var focused = _editingElement.Focus();
        AutoCompleteDiagnostics.Log($"Cell.BeginEdit.focused cell={GetHashCode():X8} returned={focused} editing={IsEditing} elementFocused={_editingElement.IsFocused} elementFocusWithin={_editingElement.IsKeyboardFocusWithin}");
    }

    /// <summary>
    /// Commits the current edit and returns to display mode.
    /// </summary>
    public object? CommitEdit()
    {
        if (!IsEditing || _editingElement == null || Column == null)
            return null;

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
        if (!IsEditing || _editingElement == null || Column == null)
            return;

        Column.CancelCellEdit(_editingElement, _uneditedValue);
        IsEditing = false;
        SwapToDisplayElement();
    }

    private void SwapToDisplayElement()
    {
        if (_border == null) return;
        _border.ClipToBounds = true;
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

    /// <summary>
    /// Plays the undo/redo wash on this cell: quick fade in, a short hold, then a slow fade out.
    /// The cell owns the layer so a viewport refresh during a history replay cannot drop it.
    /// </summary>
    internal void FlashHistoryChange()
    {
        _historyFlashStarted = Stopwatch.GetTimestamp();
        if (_historyFlashTimer == null)
        {
            _historyFlashTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
            _historyFlashTimer.Tick += OnHistoryFlashTick;
        }
        _historyFlashTimer.Start();
        AdvanceHistoryFlash();
    }

    private void OnHistoryFlashTick(object? sender, EventArgs e) => AdvanceHistoryFlash();

    private void AdvanceHistoryFlash()
    {
        var elapsed = Stopwatch.GetElapsedTime(_historyFlashStarted).TotalMilliseconds;
        if (elapsed >= HistoryFlashDurationMilliseconds)
        {
            StopHistoryFlash();
            return;
        }

        double wash;
        if (elapsed < HistoryFlashFadeInMilliseconds)
            wash = elapsed / HistoryFlashFadeInMilliseconds;
        else if (elapsed < HistoryFlashFadeInMilliseconds + HistoryFlashHoldMilliseconds)
            wash = 1;
        else
            wash = 1 - HistoryFlashFadeOutEasing.Ease(Math.Clamp(
                (elapsed - HistoryFlashFadeInMilliseconds - HistoryFlashHoldMilliseconds) /
                HistoryFlashFadeOutMilliseconds, 0, 1));

        _historyFlashWash = Math.Clamp(wash, 0, 1);
        _historyFlashLayer?.InvalidateVisual();
    }

    private void StopHistoryFlash()
    {
        _historyFlashTimer?.Stop();
        if (_historyFlashWash <= 0) return;
        _historyFlashWash = 0;
        _historyFlashLayer?.InvalidateVisual();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        // A recycled cell now shows a different record; the old highlight no longer applies.
        if (change.Property == DataItemProperty)
            StopHistoryFlash();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        RemoveHandler(DoubleTappedEvent, OnDoubleTapped);
        base.OnDetachedFromVisualTree(e);
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
        else if (IsSelected && OwningRow?.OwningGrid?.SelectionUnit != DataGridSelectionUnit.Cell)
        {
            _border.Background = ValidationSeverity switch
            {
                DataGridValidationSeverity.InValid or DataGridValidationSeverity.Error =>
                    new SolidColorBrush(Color.Parse("#fab6b6")),
                DataGridValidationSeverity.Warning =>
                    new SolidColorBrush(Color.Parse("#f3d19e")),
                DataGridValidationSeverity.Info =>
                    new SolidColorBrush(Color.Parse("#EEF6FF")),
                _ => SelectedRowBackground,
            };
            _border.BorderBrush = new SolidColorBrush(Color.Parse("#0078D4"));
            if (OwningRow?.OwningGrid?.SelectionUnit == DataGridSelectionUnit.Cell)
            {
                _border.BorderBrush = GridLineBrush;
                _border.BorderThickness = new Thickness(0, 0, right, bottom);
            }
            else _border.BorderThickness = new Thickness(0.5);
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
            _border.Background = OwningRow?.IsSelected == true
                ? SelectedRowBackground
                : OwningRow?.IsPointerOver == true
                    ? grid?.RowHoverBackground ?? Brushes.Transparent
                    : grid?.RowBackground ?? new SolidColorBrush(Colors.White);
            _border.BorderBrush = GridLineBrush;
            _border.BorderThickness = new Thickness(0, 0, right, bottom);
        }
        else
        {
            var grid = OwningRow?.OwningGrid;
            _border.Background = OwningRow?.IsSelected == true
                ? SelectedRowBackground
                : OwningRow?.IsPointerOver == true
                    ? grid?.RowHoverBackground ?? Brushes.Transparent
                    : Brushes.Transparent;
            _border.BorderBrush = GridLineBrush;
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
            _border.Background = isCurrent ? CurrentSearchMatchBackground : SearchMatchBackground;
        }
    }

    private bool IsFrozenCell()
    {
        var grid = OwningRow?.OwningGrid;
        if (grid == null || Column == null) return false;
        int idx = grid.Columns.IndexOf(Column);
        return idx < grid.LeftFrozenColumnCount || idx >= grid.Columns.Count - grid.RightFrozenColumnCount;
    }

    // Draws the wash itself, with the fade baked into the brush alpha, so no compositor layer or
    // child-visual bookkeeping stands between the animation and the screen.
    private sealed class HistoryFlashLayer(DataGridCell owner) : Control
    {
        public override void Render(DrawingContext context)
        {
            var wash = owner._historyFlashWash;
            if (wash <= 0) return;
            var alpha = (byte)Math.Clamp(HistoryFlashAlpha * wash, 0, 255);
            var brush = new ImmutableSolidColorBrush(
                Color.FromArgb(alpha, HistoryFlashColor.R, HistoryFlashColor.G, HistoryFlashColor.B));
            context.FillRectangle(brush, new Rect(Bounds.Size));
        }
    }
}