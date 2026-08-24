using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Metadata;

namespace PatChes.Controls.DataGrid;

public abstract class DataGridBoundColumn : DataGridColumn
{
    private BindingBase? _binding;

    [AssignBinding]
    [InheritDataTypeFromItems(nameof(DataGrid.ItemsSource), AncestorType = typeof(DataGrid))]
    public virtual BindingBase? Binding
    {
        get => _binding;
        set
        {
            if (_binding != value)
            {
                _binding = value;
                BindingPath = ExtractPath(value);
                if (_binding is Binding b && b.Mode == BindingMode.Default && string.IsNullOrEmpty(b.StringFormat))
                    b.Mode = BindingMode.TwoWay;
                DataGridOwner?.OnColumnBindingChanged(this);
            }
        }
    }

    internal string? BindingPath { get; private set; }

    private static string? ExtractPath(BindingBase? binding)
    {
        if (binding == null) return null;
        if (binding is Binding b) return b.Path;
        var prop = binding.GetType().GetProperty("Path");
        return prop?.GetValue(binding)?.ToString();
    }

    protected AvaloniaProperty? BindingTarget { get; set; }

    public override Control? GenerateEditingElement(DataGridCell cell, object? dataItem)
    {
        var element = GenerateEditingElementDirect(cell, dataItem);
        if (element != null && Binding != null && BindingTarget != null && dataItem != null)
        {
            var binding = CloneBinding(Binding);
            element.Bind(BindingTarget, binding);
        }
        return element;
    }

    protected abstract Control? GenerateEditingElementDirect(DataGridCell cell, object? dataItem);

    protected static BindingBase CloneBinding(BindingBase source)
    {
        if (source is Binding b)
        {
            return new Binding
            {
                Path = b.Path, Source = b.Source, Converter = b.Converter,
                ConverterParameter = b.ConverterParameter, ConverterCulture = b.ConverterCulture,
                FallbackValue = b.FallbackValue, TargetNullValue = b.TargetNullValue,
                StringFormat = b.StringFormat,
                Mode = b.Mode == BindingMode.Default ? BindingMode.TwoWay : b.Mode,
            };
        }
        return source;
    }
}
