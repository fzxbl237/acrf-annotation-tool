using System.Collections;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using AtomUI.Icons.AntDesign;
using AtomIconButton = AtomUI.Desktop.Controls.IconButton;
using ComboBox = AtomUI.Desktop.Controls.ComboBox;

namespace PatChes.Controls.DataGrid;

public class DataGridComboBoxColumn : DataGridBoundColumn
{
    public static readonly StyledProperty<IEnumerable?> ItemsSourceProperty =
        AvaloniaProperty.Register<DataGridComboBoxColumn, IEnumerable?>(nameof(ItemsSource));
    public static readonly StyledProperty<double> FontSizeProperty =
        AvaloniaProperty.Register<DataGridComboBoxColumn, double>(nameof(FontSize), 13);
    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        AvaloniaProperty.Register<DataGridComboBoxColumn, IBrush?>(nameof(Foreground));
    public static readonly StyledProperty<IDataTemplate?> ItemTemplateProperty =
        AvaloniaProperty.Register<DataGridComboBoxColumn, IDataTemplate?>(nameof(ItemTemplate));
    public static readonly StyledProperty<double> MaxDropDownHeightProperty =
        AvaloniaProperty.Register<DataGridComboBoxColumn, double>(nameof(MaxDropDownHeight), 300);

    public IEnumerable? ItemsSource { get => GetValue(ItemsSourceProperty); set => SetValue(ItemsSourceProperty, value); }
    public double FontSize { get => GetValue(FontSizeProperty); set => SetValue(FontSizeProperty, value); }
    public IBrush? Foreground { get => GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    public IDataTemplate? ItemTemplate { get => GetValue(ItemTemplateProperty); set => SetValue(ItemTemplateProperty, value); }
    public double MaxDropDownHeight { get => GetValue(MaxDropDownHeightProperty); set => SetValue(MaxDropDownHeightProperty, value); }

    public DataGridComboBoxColumn() { BindingTarget = ComboBox.SelectedItemProperty; }

    public override Control GenerateElement(DataGridCell cell, object? dataItem)
    {
        var panel = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            IsHitTestVisible = false,
        };

        var textBlock = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 4, 0),
            FontSize = FontSize,
            Foreground = Foreground ?? DataGridColumn.DefaultCellTextForeground,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        if (Foreground != null)
            textBlock.Foreground = Foreground;

        var arrow = new AtomIconButton
        {
            Icon = new DownOutlined(),
            Width = 18,
            Height = 18,
            MinWidth = 0,
            FontSize = 12,
            Padding = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 0, 4, 0),
        };

        Grid.SetColumn(textBlock, 0);
        Grid.SetColumn(arrow, 1);
        panel.Children.Add(textBlock);
        panel.Children.Add(arrow);

        if (Binding != null && dataItem != null)
        {
            var binding = CloneBinding(Binding, BindingMode.OneWay);
            textBlock.Bind(TextBlock.TextProperty, binding);
        }
        else if (dataItem != null && !string.IsNullOrWhiteSpace(BindingPath))
        {
            var property = DataGridBoundColumn.GetItemProperty(dataItem, BindingPath);
            textBlock.Text = property?.GetValue(dataItem)?.ToString();
        }
        else if (dataItem != null)
        {
            textBlock.Text = dataItem.ToString();
        }

        return panel;
    }

    public override Control? GenerateEditingElement(DataGridCell cell, object? dataItem)
    {
        var editingElement = GenerateEditingElementDirect(cell, dataItem);
        if (editingElement is not ComboBox comboBox || dataItem == null)
            return editingElement;

        if (!string.IsNullOrWhiteSpace(BindingPath))
        {
            var property = DataGridBoundColumn.GetItemProperty(dataItem, BindingPath);
            comboBox.SelectedItem = property?.GetValue(dataItem);
        }

        if (Binding != null)
        {
            var binding = CloneBinding(Binding, BindingMode.OneWay);
            comboBox.Bind(ComboBox.SelectedItemProperty, binding);
        }

        return comboBox;
    }

    protected override Control? GenerateEditingElementDirect(DataGridCell cell, object? dataItem)
    {
        var cb = new ComboBox
        {
            ItemsSource = ItemsSource,
            MaxDropDownHeight = MaxDropDownHeight,
            FontSize = FontSize,
            MinHeight = 22,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            FocusAdorner = null,
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Colors.Transparent),
        };

        cb.DropDownClosed += (_, _) => DataGridOwner?.OnComboBoxDropDownClosed();

        if (ItemTemplate != null) cb.ItemTemplate = ItemTemplate;

        return cb;
    }

    public override object? PrepareCellForEdit(Control editingElement, RoutedEventArgs? editingEventArgs)
    {
        if (editingElement is ComboBox cb)
        {
            var originalValue = cb.SelectedItem;
            Avalonia.Threading.Dispatcher.UIThread.Post(() => cb.IsDropDownOpen = true,
                Avalonia.Threading.DispatcherPriority.Loaded);
            return originalValue;
        }
        return null;
    }

    public override void CancelCellEdit(Control editingElement, object? uneditedValue)
    {
        if (editingElement is ComboBox cb) cb.SelectedItem = uneditedValue;
    }

    public override object? CommitCellEdit(Control editingElement)
    {
        if (editingElement is ComboBox cb) return cb.SelectedItem;
        return null;
    }

}