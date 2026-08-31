using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using AtomButton = AtomUI.Desktop.Controls.Button;
using AtomCheckBox = AtomUI.Desktop.Controls.CheckBox;
using AtomContextMenu = AtomUI.Desktop.Controls.ContextMenu;
using AtomMenuItem = AtomUI.Desktop.Controls.MenuItem;
using AtomFlyout = AtomUI.Desktop.Controls.Flyout;

namespace PatChes.Controls.DataGrid;

public class DataGridColumnHeader : Control
{
    public static readonly StyledProperty<DataGridColumn?> OwningColumnProperty =
        AvaloniaProperty.Register<DataGridColumnHeader, DataGridColumn?>(nameof(OwningColumn));
    public static readonly StyledProperty<bool> AreSeparatorsVisibleProperty =
        AvaloniaProperty.Register<DataGridColumnHeader, bool>(nameof(AreSeparatorsVisible), true);

    public DataGridColumn? OwningColumn { get => GetValue(OwningColumnProperty); set => SetValue(OwningColumnProperty, value); }
    public bool AreSeparatorsVisible { get => GetValue(AreSeparatorsVisibleProperty); set => SetValue(AreSeparatorsVisibleProperty, value); }

    private Border? _root;
    private Border? _resizeIndicator;
    private Border? _filterButton;
    private DrawnGeometry? _filterIcon;
    private AtomFlyout? _filterFlyout;
    private AtomFlyout? _columnChooserFlyout;
    private DataGridFilterPopup? _filterContent;
    private bool _isResizing;
    private double _resizeStartX;
    private double _resizeStartWidth;
    private bool _isMouseOverFilterButton;

    private const double ResizeRegionWidth = 8;
    private const double IndicatorLineWidth = 2;
    private const double IndicatorLineHeight = 14;
    private const double FilterButtonWidth = 22;
    private const double FilterButtonGap = 6;
    private const double FilterIconSize = 14;

    private const string FunnelGeometry =
        "M320 352h400l2.88 0.256A16 16 0 0 1 720 384H320a16 16 0 0 1-2.88-31.744L320 352z " +
        "M592 608h-160l-2.88 0.256a16 16 0 0 0 2.88 31.744h160l2.88-0.256A16 16 0 0 0 592 608z " +
        "M368 480h288a16 16 0 0 1 2.88 31.744L656 512h-288a16 16 0 0 1-2.88-31.744L368 480z";

    private const string FunnelWithDotGeometry =
        "M320 352h400l2.88 0.256A16 16 0 0 1 720 384H320a16 16 0 0 1-2.88-31.744L320 352z " +
        "M592 608h-160l-2.88 0.256a16 16 0 0 0 2.88 31.744h160l2.88-0.256A16 16 0 0 0 592 608z " +
        "M368 480h288a16 16 0 0 1 2.88 31.744L656 512h-288a16 16 0 0 1-2.88-31.744L368 480z " +
        "M832 288a96 96 0 1 1-192 0 96 96 0 0 1 192 0z";

    private static readonly IBrush FilterBgHover = new SolidColorBrush(Color.Parse("#E0E0E0"));
    private static readonly IBrush FilterBgDefault = new SolidColorBrush(Colors.Transparent);
    private static readonly IBrush FilterIconDefaultBrush = new SolidColorBrush(Color.Parse("#888888"));
    private static readonly IBrush FilterIconActiveBrush = new SolidColorBrush(Color.Parse("#0078D4"));

    protected override Size MeasureOverride(Size availableSize)
    {
        EnsureVisualTree();
        if (_root != null) { _root.Measure(availableSize); return _root.DesiredSize; }
        return base.MeasureOverride(availableSize);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (_root != null) _root.Arrange(new Rect(finalSize));

        if (_resizeIndicator != null && _resizeIndicator.IsVisible)
        {
            double ix = finalSize.Width - IndicatorLineWidth - 1;
            double iy = (finalSize.Height - IndicatorLineHeight) / 2;
            _resizeIndicator.Arrange(new Rect(ix, iy, IndicatorLineWidth, IndicatorLineHeight));
        }

        if (_filterButton != null)
        {
            bool shouldShow = OwningColumn?.IsFilterable == true && OwningColumn is not DataGridCheckBoxColumn;
            _filterButton.IsVisible = shouldShow;

            double ix = finalSize.Width - FilterButtonWidth - IndicatorLineWidth - FilterButtonGap;
            _filterButton.Arrange(new Rect(ix, 0, FilterButtonWidth, finalSize.Height));
        }

        return finalSize;
    }

    private void EnsureVisualTree()
    {
        if (_root != null) return;

        _root = new Border { ClipToBounds = true, Background = new SolidColorBrush(Color.Parse("#F0F0F0")) };

        _root.Child = new TextBlock
        {
            Text = OwningColumn?.Header ?? "",
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            FontWeight = FontWeight.SemiBold,
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.Parse("#333333")),
            Margin = new Thickness(4, 0),
        };

        _resizeIndicator = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#999999")),
            CornerRadius = new CornerRadius(1),
            IsVisible = true,
        };

        _filterButton = new Border
        {
            Background = FilterBgDefault,
            Cursor = new Cursor(StandardCursorType.Hand),
            IsVisible = OwningColumn is not DataGridCheckBoxColumn,
            CornerRadius = new CornerRadius(3),
            Margin = new Thickness(1, 2, 1, 2),
        };

        _filterIcon = new DrawnGeometry
        {
            PathData = FunnelGeometry,
            Fill = FilterIconDefaultBrush,
            Width = FilterIconSize,
            Height = FilterIconSize,
        };
        _filterButton.Child = _filterIcon;

        LogicalChildren.Clear();
        VisualChildren.Clear();
        LogicalChildren.Add(_root);
        VisualChildren.Add(_root);
        LogicalChildren.Add(_resizeIndicator);
        VisualChildren.Add(_resizeIndicator);
        LogicalChildren.Add(_filterButton);
        VisualChildren.Add(_filterButton);

        PointerPressed += OnHeaderPointerPressed;
        PointerMoved += OnHeaderPointerMoved;
        PointerReleased += OnHeaderPointerReleased;
        PointerExited += OnHeaderPointerExited;
        PointerEntered += OnHeaderPointerEntered;

        _filterButton.PointerEntered += (_, _) => { _isMouseOverFilterButton = true; SyncFilterIcon(); };
        _filterButton.PointerExited += (_, _) => { _isMouseOverFilterButton = false; SyncFilterIcon(); };

        SyncFilterIcon();
    }

    internal void SetFrozenBackground(IBrush background)
    {
        if (_root != null)
            _root.Background = background;
    }

    internal void RefreshFilterIcon() => SyncFilterIcon();

    /// <summary>
    /// Central icon state update. Rules:
    ///   filtered → funnel + dot, blue fill, always visible
    ///   hovered  → funnel (no dot), highlight background
    ///   default  → funnel (no dot), gray, no highlight, always visible
    /// </summary>
    private void SyncFilterIcon()
    {
        if (_filterIcon == null || _filterButton == null) return;
        if (OwningColumn is DataGridCheckBoxColumn)
        {
            _filterButton.IsVisible = false;
            return;
        }

        _filterButton.IsVisible = true;

        if (OwningColumn?.IsFiltered == true)
        {
            _filterIcon.PathData = FunnelWithDotGeometry;
            _filterIcon.Fill = _isMouseOverFilterButton ? FilterIconActiveBrush : FilterIconActiveBrush;
            _filterButton.Background = _isMouseOverFilterButton ? FilterBgHover : FilterBgDefault;
        }
        else if (_isMouseOverFilterButton)
        {
            _filterIcon.PathData = FunnelGeometry;
            _filterIcon.Fill = FilterIconDefaultBrush;
            _filterButton.Background = FilterBgHover;
        }
        else
        {
            _filterIcon.PathData = FunnelGeometry;
            _filterIcon.Fill = FilterIconDefaultBrush;
            _filterButton.Background = FilterBgDefault;
        }
    }

    private void OnHeaderPointerEntered(object? sender, PointerEventArgs e)
    {
        SyncFilterIcon();
        InvalidateArrange();
    }

    private void OnHeaderPointerExited(object? sender, PointerEventArgs e)
    {
        SyncFilterIcon();
        InvalidateArrange();
    }

    private void OnHeaderPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (OwningColumn == null) return;
        var pt = e.GetCurrentPoint(this);

        if (pt.Properties.IsRightButtonPressed)
        {
            ShowClearFilterContextMenu();
            e.Handled = true;
            return;
        }

        var pos = e.GetPosition(this);
        double w = Bounds.Width;

        if (_resizeIndicator != null && pos.X > w - ResizeRegionWidth)
        {
            _isResizing = true;
            _resizeStartX = e.GetPosition(OwningColumn.DataGridOwner).X;
            _resizeStartWidth = OwningColumn.GetEffectiveWidth();
            e.Pointer.Capture(this);
            e.Handled = true;
        }
        else if (pt.Properties.IsLeftButtonPressed)
        {
            if (pos.X > w - FilterButtonWidth - IndicatorLineWidth - FilterButtonGap && OwningColumn.IsFilterable)
            {
                ShowFilterPopup();
                e.Handled = true;
                return;
            }
            OwningColumn.DataGridOwner?.OnColumnHeaderClick(OwningColumn);
            e.Handled = true;
        }
    }

    private void OnHeaderPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_isResizing && OwningColumn?.DataGridOwner != null)
        {
            double currentX = e.GetPosition(OwningColumn.DataGridOwner).X;
            double delta = currentX - _resizeStartX;
            double newWidth = Math.Max(OwningColumn.MinWidth, Math.Min(OwningColumn.MaxWidth, _resizeStartWidth + delta));
            OwningColumn.Width = new GridLength(newWidth, GridUnitType.Pixel);
            e.Handled = true;
            return;
        }

        if (OwningColumn == null) return;
        double w = Bounds.Width;
        double x = e.GetPosition(this).X;
        bool nearEdge = x > w - ResizeRegionWidth;

        Cursor = nearEdge
            ? new Cursor(StandardCursorType.SizeWestEast)
            : new Cursor(StandardCursorType.Arrow);
    }

    private void OnHeaderPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_isResizing)
        {
            _isResizing = false;
            e.Pointer.Capture(null);
            e.Handled = true;
        }
    }

    private void ShowClearFilterContextMenu()
    {
        if (OwningColumn?.DataGridOwner == null) return;
        var grid = OwningColumn.DataGridOwner;
        bool hasFilter = grid.Columns.Any(c => c.IsFiltered);

        var menu = new AtomContextMenu();

        var target = grid.HeadersPresenter as Control ?? this;
        var chooserItem = new AtomMenuItem { Header = "列选择 (Column Chooser)" };
        chooserItem.Click += (_, _) => ShowColumnChooserFlyout(grid, target);
        menu.Items.Add(chooserItem);

        var clearAllItem = new AtomMenuItem
        {
            Header = "取消所有筛选",
            IsEnabled = hasFilter,
        };
        clearAllItem.Click += (_, _) => grid.ClearAllFilters();
        menu.Items.Add(clearAllItem);

        // Anchor to the stable headers presenter (not this header) so the menu stays open
        // while BuildHeaders() rebuilds header instances when a column is toggled.
        // Pass the target explicitly: the parameterless Open() does not resolve PlacementTarget
        // for a menu created in code, and would throw ArgumentNullException.
        menu.PlacementTarget = target;
        menu.Open(target);
    }

    private void ShowColumnChooserFlyout(DataGrid grid, Control target)
    {
        var content = new StackPanel
        {
            Width = 220,
            MaxHeight = 420,
            Spacing = 6,
            Margin = new Thickness(10),
        };

        content.Children.Add(new TextBlock
        {
            Text = "列选择",
            FontSize = 15,
            FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(Color.Parse("#1A1A1A")),
            Margin = new Thickness(0, 0, 0, 4),
        });

        foreach (var column in grid.Columns)
        {
            if (column == null) continue;

            var checkBox = new AtomCheckBox
            {
                IsChecked = column.IsVisible,
                IsEnabled = column.CanUserHide,
                Content = column.Header ?? "",
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                Margin = new Thickness(0, 1),
            };
            checkBox.Bind(
                AtomCheckBox.IsCheckedProperty,
                new Binding
                {
                    Source = column,
                    Path = nameof(DataGridColumn.IsVisible),
                    Mode = BindingMode.TwoWay,
                });
            checkBox.PropertyChanged += (_, args) =>
            {
                if (args.Property == AtomCheckBox.IsCheckedProperty &&
                    args.NewValue is bool isVisible &&
                    column.IsVisible != isVisible)
                {
                    column.IsVisible = isVisible;
                }
            };
            content.Children.Add(checkBox);
        }

        var actionPanel = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            Spacing = 6,
            Margin = new Thickness(0, 6, 0, 0),
        };
        var showAll = new AtomButton { Content = "全部显示", MinWidth = 78 };
        showAll.Click += (_, _) => SetAllVisible(grid, true);
        var hideAll = new AtomButton { Content = "全部隐藏", MinWidth = 78 };
        hideAll.Click += (_, _) => SetAllVisible(grid, false);
        var close = new AtomButton { Content = "关闭", MinWidth = 58 };
        close.Click += (_, _) => _columnChooserFlyout?.Hide();
        actionPanel.Children.Add(showAll);
        actionPanel.Children.Add(hideAll);
        actionPanel.Children.Add(close);
        content.Children.Add(actionPanel);

        _columnChooserFlyout?.Hide();
        _columnChooserFlyout = new AtomFlyout
        {
            Content = content,
            RequestedPlacement = PlacementMode.BottomEdgeAlignedLeft,
            IsArrowVisible = false,
            IsLightDismissEnabled = true,
            ShouldUseOverlayPopup = true,
        };
        _columnChooserFlyout.Closed += (_, _) => _columnChooserFlyout = null;
        _columnChooserFlyout.ShowAt(target);
    }

    private static void SetAllVisible(DataGrid grid, bool visible)
    {
        if (grid == null) return;
        foreach (var column in grid.Columns)
        {
            if (column != null && column.CanUserHide)
                column.IsVisible = visible;
        }
    }

    private void ShowFilterPopup()
    {
        if (OwningColumn == null || OwningColumn.DataGridOwner == null) return;
        var grid = OwningColumn.DataGridOwner;
        grid.EndEditBeforeLayoutChange();

        var contextItems = grid.GetFilterContextItems(OwningColumn).ToList();
        var uniqueValues = contextItems
            .Select(item => OwningColumn.GetFilterValue(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
            .ToList();

        HashSet<string>? selectedValues = null;
        if (OwningColumn.IsFiltered)
        {
            selectedValues = contextItems
                .Where(OwningColumn.PassesFilter)
                .Select(item => OwningColumn.GetFilterValue(item))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        _filterContent = new DataGridFilterPopup
        {
            Column = OwningColumn,
            OwningGrid = grid,
        };

        // 在 Popup 的上下文中显式加载主题
        if (this.TryFindResource(typeof(DataGridFilterPopup), out var themeResource) == true
            && themeResource is ControlTheme filterTheme)
        {
            _filterContent.Theme = filterTheme;
        }

        _filterContent.BuildContent(uniqueValues, selectedValues);
        _filterContent.FilterApplied += (_, _) => _filterFlyout?.Hide();

        _filterFlyout = new AtomFlyout
        {
            Content = _filterContent,
            RequestedPlacement = PlacementMode.BottomEdgeAlignedRight,
            IsArrowVisible = false,
            IsLightDismissEnabled = true,
            ShouldUseOverlayPopup = true,
        };

        _filterFlyout.Closed += (_, _) =>
        {
            SyncFilterIcon();
            InvalidateArrange();
            _filterFlyout = null;
            _filterContent = null;
        };
        _filterFlyout.ShowAt(this);
    }
}
