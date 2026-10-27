using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Metadata;
using AcrfAnnotationTool.Models;
using System.Reflection;

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
                if (value is not null and not CompiledBinding)
                    throw new NotSupportedException("DataGrid columns require compiled bindings for Native AOT. Enable x:CompileBindings and specify x:DataType.");
                _binding = value;
                BindingPath = ExtractPath(value);
                DataGridOwner?.OnColumnBindingChanged(this);
            }
        }
    }

    internal string? BindingPath { get; private set; }

    private static string? ExtractPath(BindingBase? binding)
    {
        return binding switch
        {
            Binding b => b.Path,
            CompiledBinding b => b.Path?.ToString(),
            _ => null,
        };
    }

    internal sealed class ItemProperty
    {
        private readonly Func<object, object?> _getter;
        private readonly Action<object, object?> _setter;

        public Type PropertyType { get; }
        public bool CanRead { get; }
        public bool CanWrite { get; }

        internal ItemProperty(Type propertyType, bool canRead, bool canWrite,
            Func<object, object?> getter, Action<object, object?> setter)
        {
            PropertyType = propertyType;
            CanRead = canRead;
            CanWrite = canWrite;
            _getter = getter;
            _setter = setter;
        }

        public object? GetValue(object item) => _getter(item);
        public void SetValue(object item, object? value) => _setter(item, value);
    }

    private static ItemProperty CreateAnnotationProperty<T>(Func<Annotation, T> getter, Action<Annotation, T> setter)
    {
        return new ItemProperty(typeof(T), true, true,
            item => getter((Annotation)item),
            // Match PropertyInfo.SetValue: null clears value types to default(T).
            (item, value) => setter((Annotation)item, value == null ? default! : (T)value));
    }

    private static readonly IReadOnlyDictionary<string, ItemProperty> AnnotationProperties =
        new Dictionary<string, ItemProperty>(StringComparer.OrdinalIgnoreCase)
        {
            [nameof(Annotation.FormName)] = CreateAnnotationProperty(item => item.FormName, (item, value) => item.FormName = value),
            [nameof(Annotation.PageNum)] = CreateAnnotationProperty(item => item.PageNum, (item, value) => item.PageNum = value),
            [nameof(Annotation.VarName)] = CreateAnnotationProperty(item => item.VarName, (item, value) => item.VarName = value),
            [nameof(Annotation.Text)] = CreateAnnotationProperty(item => item.Text, (item, value) => item.Text = value),
            [nameof(Annotation.Type)] = CreateAnnotationProperty(item => item.Type, (item, value) => item.Type = value),
            [nameof(Annotation.AnnotationText)] = CreateAnnotationProperty(item => item.AnnotationText, (item, value) => item.AnnotationText = value),
            [nameof(Annotation.AnnotationTextDomain)] = CreateAnnotationProperty(item => item.AnnotationTextDomain, (item, value) => item.AnnotationTextDomain = value),
            [nameof(Annotation.AnnotationDotted)] = CreateAnnotationProperty(item => item.AnnotationDotted, (item, value) => item.AnnotationDotted = value),
            [nameof(Annotation.X)] = CreateAnnotationProperty(item => item.X, (item, value) => item.X = value),
            [nameof(Annotation.Y)] = CreateAnnotationProperty(item => item.Y, (item, value) => item.Y = value),
            [nameof(Annotation.Width)] = CreateAnnotationProperty(item => item.Width, (item, value) => item.Width = value),
            [nameof(Annotation.Line)] = CreateAnnotationProperty(item => item.Line, (item, value) => item.Line = value),
            [nameof(Annotation.FormOutline)] = CreateAnnotationProperty(item => item.FormOutline, (item, value) => item.FormOutline = value),
            [nameof(Annotation.AnnotationUniqueId)] = CreateAnnotationProperty(item => item.AnnotationUniqueId, (item, value) => item.AnnotationUniqueId = value),
            [nameof(Annotation.IsSelected)] = CreateAnnotationProperty(item => item.IsSelected, (item, value) => item.IsSelected = value),
        };

    internal static bool TryGetAnnotationValue(object item, string path, bool ignoreCase, out object? value)
    {
        if (item is Annotation && AnnotationProperties.TryGetValue(path, out var property) &&
            (ignoreCase || AnnotationProperties.Keys.Contains(path, StringComparer.Ordinal)))
        {
            value = property.GetValue(item);
            return true;
        }

        value = null;
        return false;
    }

    // Explicit accessors keep editing, filtering and clipboard operations safe under trimming.
    internal static ItemProperty? GetItemProperty(object item, string path,
        BindingFlags flags = BindingFlags.Public | BindingFlags.Instance)
    {
        if (item is Annotation)
        {
            if (!flags.HasFlag(BindingFlags.Public) || !flags.HasFlag(BindingFlags.Instance))
                return null;
            return AnnotationProperties.TryGetValue(path, out var property) &&
                (flags.HasFlag(BindingFlags.IgnoreCase) || AnnotationProperties.Keys.Contains(path, StringComparer.Ordinal))
                ? property : null;
        }

        return null;
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

    protected static BindingBase CloneBinding(BindingBase source, BindingMode? mode = null)
    {
        if (source is CompiledBinding compiled)
        {
            return new CompiledBinding
            {
                Path = compiled.Path,
                Source = compiled.Source,
                Converter = compiled.Converter,
                ConverterParameter = compiled.ConverterParameter,
                ConverterCulture = compiled.ConverterCulture,
                FallbackValue = compiled.FallbackValue,
                TargetNullValue = compiled.TargetNullValue,
                StringFormat = compiled.StringFormat,
                Mode = mode ?? (compiled.Mode == BindingMode.Default ? BindingMode.TwoWay : compiled.Mode),
                Priority = compiled.Priority,
                Delay = compiled.Delay,
                UpdateSourceTrigger = compiled.UpdateSourceTrigger,
            };
        }
        throw new NotSupportedException("DataGrid columns require compiled bindings for Native AOT.");
    }
}
