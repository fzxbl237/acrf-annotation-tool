using Avalonia.Controls;
using Avalonia.Controls.Templates;
using AcrfAnnotationTool.ViewModels;
using AcrfAnnotationTool.ViewModels.Documents;
using AcrfAnnotationTool.Views;

namespace AcrfAnnotationTool;

public sealed class ViewLocator : IDataTemplate
{
    public Control? Build(object? data)
    {
        return data switch
        {
            MainWindowViewModel vm => new MainView { DataContext = vm },
            AcrfDocumentViewModel vm => new AcrfEditorView { DataContext = vm },
            _ => new TextBlock { Text = $"未找到视图: {data?.GetType().Name}" }
        };
    }

    public bool Match(object? data) => data is MainWindowViewModel
        or AcrfDocumentViewModel;
}
