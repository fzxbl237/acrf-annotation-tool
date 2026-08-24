using System;
using System.Threading;
using System.Threading.Tasks;
using AbstractAutoComplete = AtomUI.Desktop.Controls.AbstractAutoComplete;
using AtomComboBox = AtomUI.Desktop.Controls.ComboBox;
using AtomLineEdit = AtomUI.Desktop.Controls.LineEdit;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Interactivity;
using Avalonia.Layout;

namespace PatChes.Controls.DataGrid;

public class DataGridDynamicColumn : DataGridBoundColumn
{
    public static readonly StyledProperty<IDataGridDynamicEditorProvider?> EditorProviderProperty =
        AvaloniaProperty.Register<DataGridDynamicColumn, IDataGridDynamicEditorProvider?>(nameof(EditorProvider));

    public IDataGridDynamicEditorProvider? EditorProvider
    {
        get => GetValue(EditorProviderProperty);
        set => SetValue(EditorProviderProperty, value);
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
            if (binding is Binding dataBinding)
                dataBinding.Mode = BindingMode.OneWay;
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
        return null;
    }

    public override Control? GenerateEditingElement(DataGridCell cell, object? dataItem)
    {
        if (dataItem == null)
            return null;

        var currentValue = GetCurrentValue(dataItem);
        var loadingEditor = CreateLineEdit(currentValue, false);
        var host = new DynamicEditorHost(currentValue, dataItem)
        {
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
            Content = loadingEditor,
        };

        var provider = EditorProvider ?? DataGridOwner?.DataContext as IDataGridDynamicEditorProvider;
        if (provider == null)
        {
            loadingEditor.IsEnabled = true;
            ConfigureEditor(loadingEditor);
            host.Editor = loadingEditor;
            return host;
        }

        _ = LoadEditorAsync(host, provider, dataItem);
        return host;
    }

    public override object? PrepareCellForEdit(Control editingElement, RoutedEventArgs? editingEventArgs)
    {
        return editingElement is DynamicEditorHost host ? host.OriginalValue : null;
    }

    public override void CancelCellEdit(Control editingElement, object? uneditedValue)
    {
        if (editingElement is not DynamicEditorHost host)
            return;

        host.CancelLoading();
        if (host.Editor != null)
            SetEditorValue(host.Editor, uneditedValue);
    }

    public override object? CommitCellEdit(Control editingElement)
    {
        if (editingElement is not DynamicEditorHost host)
            return null;

        host.CancelLoading();
        if (host.Editor == null)
            return host.OriginalValue;

        var editorValue = GetEditorValue(host.Editor);
        return editorValue ?? GetCurrentValue(host.DataItem);
    }

    private async Task LoadEditorAsync(
        DynamicEditorHost host,
        IDataGridDynamicEditorProvider provider,
        object dataItem)
    {
        try
        {
            var context = new DataGridDynamicEditorContext(this, dataItem, host.OriginalValue);
            var editor = await provider.CreateEditorAsync(context, host.CancellationToken);
            if (host.CancellationToken.IsCancellationRequested)
                return;

            editor ??= CreateLineEdit(host.OriginalValue, true);
            ConfigureEditor(editor);
            SetEditorValue(editor, host.OriginalValue);
            host.Editor = editor;
            host.Content = editor;

            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (host.CancellationToken.IsCancellationRequested)
                    return;

                editor.Focus();
                switch (editor)
                {
                    case AtomComboBox comboBox:
                        comboBox.IsDropDownOpen = true;
                        break;
                    case AbstractAutoComplete autoComplete:
                        autoComplete.IsDropDownOpen = true;
                        break;
                }
            }, Avalonia.Threading.DispatcherPriority.Input);
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            if (host.CancellationToken.IsCancellationRequested)
                return;

            var fallback = CreateLineEdit(host.OriginalValue, true);
            ConfigureEditor(fallback);
            host.Editor = fallback;
            host.Content = fallback;
            Avalonia.Threading.Dispatcher.UIThread.Post(() => fallback.Focus(),
                Avalonia.Threading.DispatcherPriority.Input);
        }
    }

    private object? GetCurrentValue(object dataItem)
    {
        if (string.IsNullOrWhiteSpace(BindingPath))
            return null;

        return dataItem.GetType().GetProperty(BindingPath)?.GetValue(dataItem);
    }

    private void ConfigureEditor(Control editor)
    {
        editor.HorizontalAlignment = HorizontalAlignment.Stretch;
        editor.VerticalAlignment = VerticalAlignment.Stretch;
        editor.Margin = new Thickness(0);

        switch (editor)
        {
            case AtomComboBox comboBox:
                comboBox.DropDownClosed += (_, _) => DataGridOwner?.OnComboBoxDropDownClosed();
                break;
            case AtomLineEdit lineEdit:
                lineEdit.GotFocus += (_, _) =>
                {
                    Avalonia.Threading.Dispatcher.UIThread.Post(
                        () => lineEdit.CaretIndex = lineEdit.Text?.Length ?? 0,
                        Avalonia.Threading.DispatcherPriority.Input);
                };
                break;
        }
    }

    private static AtomLineEdit CreateLineEdit(object? value, bool isEnabled)
    {
        return new AtomLineEdit
        {
            Text = value?.ToString() ?? string.Empty,
            IsEnabled = isEnabled,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Margin = new Thickness(0),
        };
    }

    private static object? GetEditorValue(Control editor)
    {
        return editor switch
        {
            AtomComboBox comboBox => comboBox.SelectedItem,
            AtomLineEdit lineEdit => lineEdit.Text,
            AbstractAutoComplete autoComplete => autoComplete.Value,
            _ => null,
        };
    }

    private static void SetEditorValue(Control editor, object? value)
    {
        switch (editor)
        {
            case AtomComboBox comboBox:
                comboBox.SelectedItem = value;
                break;
            case AtomLineEdit lineEdit:
                lineEdit.Text = value?.ToString() ?? string.Empty;
                break;
            case AbstractAutoComplete autoComplete:
                autoComplete.Value = value?.ToString() ?? string.Empty;
                break;
        }
    }

    private sealed class DynamicEditorHost : ContentControl
    {
        private readonly CancellationTokenSource _cancellationTokenSource = new();

        public DynamicEditorHost(object? originalValue, object dataItem)
        {
            OriginalValue = originalValue;
            DataItem = dataItem;
        }

        public object? OriginalValue { get; }
        public object DataItem { get; }
        public Control? Editor { get; set; }
        public CancellationToken CancellationToken => _cancellationTokenSource.Token;

        public void CancelLoading()
        {
            if (!_cancellationTokenSource.IsCancellationRequested)
                _cancellationTokenSource.Cancel();
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            CancelLoading();
            base.OnDetachedFromVisualTree(e);
        }
    }
}
