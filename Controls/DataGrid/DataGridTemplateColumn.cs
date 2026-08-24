using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Interactivity;

namespace PatChes.Controls.DataGrid;

/// <summary>
/// Represents a DataGrid column that hosts templated controls in its cells.
/// Uses CellTemplate for display mode and EditingTemplate for edit mode.
/// Falls back to CellTemplate for editing if EditingTemplate is not set.
/// </summary>
public class DataGridTemplateColumn : DataGridColumn
{
    private Control? _generatedElement;

    /// <summary>
    /// Generates the display element using CellTemplate.
    /// </summary>
    public override Control GenerateElement(DataGridCell cell, object? dataItem)
    {
        if (CellTemplate != null && dataItem != null)
        {
            var content = CellTemplate.Build(dataItem);
            if (content != null) return content;
        }
        return new TextBlock
        {
            Text = dataItem?.ToString() ?? "",
            Margin = new Thickness(4, 0),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Foreground = DataGridColumn.DefaultCellTextForeground,
        };
    }

    /// <summary>
    /// Generates the editing element using EditingTemplate, falling back to CellTemplate.
    /// </summary>
    public override Control? GenerateEditingElement(DataGridCell cell, object? dataItem)
    {
        var template = EditingTemplate ?? CellTemplate;
        if (template != null && dataItem != null)
        {
            var content = template.Build(dataItem);
            if (content != null) return content;
        }
        return GenerateElement(cell, dataItem);
    }

    /// <summary>
    /// For template columns, we capture the generated element as the unedited value reference.
    /// </summary>
    public override object? PrepareCellForEdit(Control editingElement, RoutedEventArgs? editingEventArgs)
    {
        _generatedElement = editingElement;
        return editingElement;
    }

    public override void CancelCellEdit(Control editingElement, object? uneditedValue)
    {
    }

    public override object? CommitCellEdit(Control editingElement)
    {
        return editingElement;
    }
}
