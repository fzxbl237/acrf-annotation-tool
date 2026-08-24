using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;

namespace PatChes.Controls.DataGrid;

public interface IDataGridDynamicEditorProvider
{
    Task<Control?> CreateEditorAsync(DataGridDynamicEditorContext context, CancellationToken cancellationToken);
}

public sealed record DataGridDynamicEditorContext(
    DataGridDynamicColumn Column,
    object DataItem,
    object? CurrentValue);
