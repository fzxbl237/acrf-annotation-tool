using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Interactivity;
using AtomCheckBox = AtomUI.Desktop.Controls.CheckBox;

namespace PatChes.Controls.DataGrid;

public class DataGridCheckBoxColumn : DataGridBoundColumn
{
    public static readonly StyledProperty<bool> ShowCheckBoxProperty =
        AvaloniaProperty.Register<DataGridCheckBoxColumn, bool>(nameof(ShowCheckBox), true);
    public bool ShowCheckBox { get => GetValue(ShowCheckBoxProperty); set => SetValue(ShowCheckBoxProperty, value); }

    public DataGridCheckBoxColumn() { BindingTarget = AtomCheckBox.IsCheckedProperty; }

    /// <summary>
    /// Display element: interactive CheckBox with TwoWay binding.
    /// Single click toggles the value directly, no edit mode needed.
    /// </summary>
    public override Control GenerateElement(DataGridCell cell, object? dataItem)
    {
        var cb = new AtomCheckBox
        {
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
        };
        if (Binding != null && dataItem != null)
        {
            var b = CloneBinding(Binding);
            if (b is Binding binding) binding.Mode = BindingMode.TwoWay;
            cb.Bind(AtomCheckBox.IsCheckedProperty, b);
        }

        return cb;
    }

    protected override Control? GenerateEditingElementDirect(DataGridCell cell, object? dataItem)
    {
        return new AtomCheckBox
        {
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
        };
    }

    public override object? PrepareCellForEdit(Control editingElement, RoutedEventArgs? editingEventArgs)
    {
        if (editingElement is AtomCheckBox cb) return cb.IsChecked;
        return null;
    }

    public override void CancelCellEdit(Control editingElement, object? uneditedValue)
    {
        if (editingElement is AtomCheckBox cb && uneditedValue is bool prev) cb.IsChecked = prev;
    }

    public override object? CommitCellEdit(Control editingElement)
    {
        if (editingElement is AtomCheckBox cb) return cb.IsChecked;
        return null;
    }
}
