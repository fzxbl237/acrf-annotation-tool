using AtomNumericUpDown = AtomUI.Desktop.Controls.NumericUpDown;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Threading;

namespace PatChes.Controls.DataGrid;

public class DataGridNumericUpDownColumn : DataGridBoundColumn
{
    public static readonly StyledProperty<decimal?> MinimumProperty =
        AvaloniaProperty.Register<DataGridNumericUpDownColumn, decimal?>(nameof(Minimum));
    public static readonly StyledProperty<decimal?> MaximumProperty =
        AvaloniaProperty.Register<DataGridNumericUpDownColumn, decimal?>(nameof(Maximum));
    public static readonly StyledProperty<decimal?> IncrementProperty =
        AvaloniaProperty.Register<DataGridNumericUpDownColumn, decimal?>(nameof(Increment));

    public decimal? Minimum { get => GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }
    public decimal? Maximum { get => GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public decimal? Increment { get => GetValue(IncrementProperty); set => SetValue(IncrementProperty, value); }

    public DataGridNumericUpDownColumn()
    {
        BindingTarget = NumericUpDown.ValueProperty;
    }

    public override Control GenerateElement(DataGridCell cell, object? dataItem)
    {
        var textBlock = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0),
            Foreground = DataGridColumn.DefaultCellTextForeground,
        };

        if (Binding != null && dataItem != null)
        {
            var binding = CloneBinding(Binding);
            if (binding is Binding dataBinding) dataBinding.Mode = BindingMode.OneWay;
            textBlock.Bind(TextBlock.TextProperty, binding);
        }
        else if (dataItem != null)
        {
            textBlock.Text = dataItem.ToString();
        }

        return textBlock;
    }

    protected override Control? GenerateEditingElementDirect(DataGridCell cell, object? dataItem)
    {
        return new DataGridNumericUpDown
        {
            Minimum = Minimum ?? decimal.MinValue,
            Maximum = Maximum ?? decimal.MaxValue,
            Increment = Increment ?? 1m,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Margin = new Thickness(0),
        };
    }

    public override Control? GenerateEditingElement(DataGridCell cell, object? dataItem)
    {
        var element = GenerateEditingElementDirect(cell, dataItem);
        if (element is not AtomNumericUpDown numericUpDown)
            return element;

        if (dataItem != null && !string.IsNullOrWhiteSpace(BindingPath))
        {
            var property = dataItem.GetType().GetProperty(BindingPath);
            var currentValue = property?.GetValue(dataItem);
            if (currentValue != null)
                numericUpDown.Value = System.Convert.ToDecimal(currentValue);
        }

        numericUpDown.AddHandler(InputElement.KeyDownEvent, OnNumericKeyDown, RoutingStrategies.Tunnel);
        return numericUpDown;
    }

    private static void OnNumericKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not AtomNumericUpDown numericUpDown || e.Key != Key.Back)
            return;

        numericUpDown.Value = null;
        e.Handled = true;
        Dispatcher.UIThread.Post(() => numericUpDown.Focus(), DispatcherPriority.Input);
    }

    public override object? PrepareCellForEdit(Control editingElement, RoutedEventArgs? editingEventArgs)
    {
        return editingElement is AtomNumericUpDown numericUpDown ? numericUpDown.Value : null;
    }

    public override void CancelCellEdit(Control editingElement, object? uneditedValue)
    {
        if (editingElement is AtomNumericUpDown numericUpDown)
            numericUpDown.Value = uneditedValue as decimal?;
    }

    public override object? CommitCellEdit(Control editingElement)
    {
        return editingElement is AtomNumericUpDown numericUpDown ? numericUpDown.Value : null;
    }

    private sealed class DataGridNumericUpDown : AtomNumericUpDown
    {
        protected override System.Type StyleKeyOverride => typeof(AtomNumericUpDown);

        protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
        {
            base.OnApplyTemplate(e);
            var textBox = e.NameScope.Find<TextBox>("PART_TextBox");
            if (textBox == null)
                return;

            Dispatcher.UIThread.Post(() =>
            {
                textBox.Focus();
                textBox.SelectAll();
            }, DispatcherPriority.Input);
        }
    }
}
