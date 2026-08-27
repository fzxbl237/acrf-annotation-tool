using AcrfAnnotationTool.Services;
using AcrfAnnotationTool.Services.Interface;
using AcrfAnnotationTool.ViewModels.Documents;
using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.Core.Events;
using Dock.Model.Mvvm;

namespace AcrfAnnotationTool.ViewModels;

public sealed class DockFactory : Factory
{
    private readonly MainWindowViewModel _owner;
    private readonly CdiscDatasetCatalog _cdiscDatasetCatalog;
    private readonly SuggestionOptionsAsyncLoader _suggestionOptionsAsyncLoader;
    private readonly IMessageService _messageService;
    private bool _applicationClosing;

    public DockFactory(
        MainWindowViewModel owner,
        CdiscDatasetCatalog cdiscDatasetCatalog,
        SuggestionOptionsAsyncLoader suggestionOptionsAsyncLoader,
        IMessageService messageService)
    {
        _owner = owner;
        _cdiscDatasetCatalog = cdiscDatasetCatalog;
        _suggestionOptionsAsyncLoader = suggestionOptionsAsyncLoader;
        _messageService = messageService;
        WindowClosing += OnWindowClosing;
    }

    public void BeginApplicationShutdown() => _applicationClosing = true;

    private void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (!_applicationClosing)
            e.Cancel = true;
    }

    public override IRootDock CreateLayout()
    {
        var editor = CreateEditor("AcrfEditor-1", "注释编辑", false);

        var root = CreateRootDock();
        root.Id = "Root";
        root.Title = "ACRF 注释工具";
        root.IsCollapsable = false;
        root.ActiveDockable = editor;
        root.DefaultDockable = editor;
        root.VisibleDockables = CreateList<IDockable>(editor);
        return root;
    }

    private AcrfDocumentViewModel CreateEditor(string id, string title, bool canClose)
    {
        var editor = new AcrfDocumentViewModel(
            _cdiscDatasetCatalog,
            _owner.VariableDomainCatalog,
            _suggestionOptionsAsyncLoader,
            _messageService)
        {
            Id = id,
            Title = title,
            CanClose = canClose,
            AppOwner = _owner
        };
        return editor;
    }

    public override void InitLayout(IDockable layout)
    {
        ContextLocator = new Dictionary<string, Func<object?>>
        {
            ["Root"] = () => _owner,
        };
        base.InitLayout(layout);
    }
}
