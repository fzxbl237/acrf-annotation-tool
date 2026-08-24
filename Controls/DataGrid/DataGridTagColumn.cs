using AtomTag = AtomUI.Desktop.Controls.Tag;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Layout;

namespace PatChes.Controls.DataGrid;

public class DataGridTagColumn : DataGridBoundColumn
{
    public static readonly StyledProperty<IValueConverter?> ColorConverterProperty =
        AvaloniaProperty.Register<DataGridTagColumn, IValueConverter?>(nameof(ColorConverter));

    public static readonly StyledProperty<object?> ColorConverterParameterProperty =
        AvaloniaProperty.Register<DataGridTagColumn, object?>(nameof(ColorConverterParameter));

    public IValueConverter? ColorConverter
    {
        get => GetValue(ColorConverterProperty);
        set => SetValue(ColorConverterProperty, value);
    }

    public object? ColorConverterParameter
    {
        get => GetValue(ColorConverterParameterProperty);
        set => SetValue(ColorConverterParameterProperty, value);
    }

    public DataGridTagColumn()
    {
        IsReadOnly = true;
    }

    public override Control GenerateElement(DataGridCell cell, object? dataItem)
    {
        var tag = new AtomTag
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0),
        };

        if (Binding == null || dataItem == null)
        {
            return tag;
        }

        var textBinding = CloneBinding(Binding);
        if (textBinding is Binding textDataBinding)
        {
            textDataBinding.Mode = BindingMode.OneWay;
        }
        tag.Bind(AtomTag.TextProperty, textBinding);

        if (ColorConverter == null)
        {
            return tag;
        }

        var colorBinding = CloneBinding(Binding);
        if (colorBinding is Binding colorDataBinding)
        {
            colorDataBinding.Mode = BindingMode.OneWay;
            colorDataBinding.Converter = ColorConverter;
            colorDataBinding.ConverterParameter = ColorConverterParameter;
        }
        tag.Bind(AtomTag.TagColorProperty, colorBinding);

        return tag;
    }

    public override Control? GenerateEditingElement(DataGridCell cell, object? dataItem) => null;

    protected override Control? GenerateEditingElementDirect(DataGridCell cell, object? dataItem) => null;
}
