using System.Globalization;
using AcrfAnnotationTool.Controls;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Threading;

namespace PatChes.Controls.DataGrid;

/// <summary>
/// A numeric column edited with a <see cref="ScrubInput"/>: drag the label to scrub, or type an exact value
/// (arithmetic such as <c>12*2</c> is accepted). The display cell stays a plain text block.
/// </summary>
public class DataGridScrubInputColumn : DataGridBoundColumn
{
    public static readonly StyledProperty<double?> MinimumProperty =
        AvaloniaProperty.Register<DataGridScrubInputColumn, double?>(nameof(Minimum));
    public static readonly StyledProperty<double?> MaximumProperty =
        AvaloniaProperty.Register<DataGridScrubInputColumn, double?>(nameof(Maximum));
    public static readonly StyledProperty<double> StepProperty =
        AvaloniaProperty.Register<DataGridScrubInputColumn, double>(nameof(Step), 1);
    public static readonly StyledProperty<int?> PrecisionProperty =
        AvaloniaProperty.Register<DataGridScrubInputColumn, int?>(nameof(Precision));
    public static readonly StyledProperty<string?> UnitProperty =
        AvaloniaProperty.Register<DataGridScrubInputColumn, string?>(nameof(Unit));

    public double? Minimum { get => GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }
    public double? Maximum { get => GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public double Step { get => GetValue(StepProperty); set => SetValue(StepProperty, value); }
    public int? Precision { get => GetValue(PrecisionProperty); set => SetValue(PrecisionProperty, value); }
    public string? Unit { get => GetValue(UnitProperty); set => SetValue(UnitProperty, value); }

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
            var binding = CloneBinding(Binding, BindingMode.OneWay);
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
        return new ScrubInput
        {
            Minimum = Minimum ?? double.NegativeInfinity,
            Maximum = Maximum ?? double.PositiveInfinity,
            Step = Step,
            Precision = Precision,
            Unit = Unit,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        };
    }

    public override Control? GenerateEditingElement(DataGridCell cell, object? dataItem)
    {
        if (GenerateEditingElementDirect(cell, dataItem) is not ScrubInput input)
            return null;

        if (dataItem != null && !string.IsNullOrWhiteSpace(BindingPath))
        {
            var property = DataGridBoundColumn.GetItemProperty(dataItem, BindingPath);
            if (property?.GetValue(dataItem) is { } currentValue)
                input.Value = ToDouble(currentValue);

            // Scrubbing, arrow steps and resets write straight through to the model; the cell edit
            // commit then only finalizes what is already there.
            if (property?.CanWrite == true)
            {
                var item = dataItem;
                input.ValueChanged += (_, e) =>
                {
                    if (e.ChangeSource != ScrubInputChangeSource.Programmatic)
                        WriteBack(item, property, e.NewValue);
                };
            }
        }

        Dispatcher.UIThread.Post(input.FocusEditor, DispatcherPriority.Input);
        return input;
    }

    public override object? PrepareCellForEdit(Control editingElement, RoutedEventArgs? editingEventArgs)
    {
        return editingElement is ScrubInput input ? input.Value : null;
    }

    public override void CancelCellEdit(Control editingElement, object? uneditedValue)
    {
        if (editingElement is ScrubInput input && uneditedValue != null)
            input.Value = ToDouble(uneditedValue);
    }

    public override object? CommitCellEdit(Control editingElement)
    {
        if (editingElement is not ScrubInput input)
            return null;

        input.CommitPendingEdit();
        return input.Value;
    }

    private static void WriteBack(object item, DataGridBoundColumn.ItemProperty property, double value)
    {
        var targetType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
        object? converted;
        try
        {
            converted = Convert.ChangeType(value, targetType, CultureInfo.InvariantCulture);
        }
        catch (Exception e) when (e is InvalidCastException or FormatException or OverflowException)
        {
            return;
        }
        if (Equals(property.GetValue(item), converted))
            return;
        property.SetValue(item, converted);
    }

    private static double ToDouble(object value) =>
        Convert.ToDouble(value, CultureInfo.InvariantCulture);
}
