using CommunityToolkit.Mvvm.ComponentModel;
using Dock.Model.Controls;
using Dock.Model.Core;
using AcrfAnnotationTool.Services;
using AcrfAnnotationTool.Services.Interface;
using AcrfAnnotationTool.ViewModels.Documents;

namespace AcrfAnnotationTool.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    private readonly DockFactory _factory;
    private IRootDock? _layout;

    public TemplateVariableDomainCatalog VariableDomainCatalog { get; }
    private string _status = "就绪";

    public IRootDock? Layout
    {
        get => _layout;
        private set => SetProperty(ref _layout, value);
    }

    public DockFactory Factory => _factory;

    public void BeginApplicationShutdown() => _factory.BeginApplicationShutdown();

    public async Task<SpecExportOutcome> ExportSpecForShutdownAsync()
    {
        var document = Layout?.ActiveDockable as AcrfDocumentViewModel ??
            Layout?.VisibleDockables?.OfType<AcrfDocumentViewModel>().FirstOrDefault();
        return document is null
            ? SpecExportOutcome.Failed
            : await document.ExportSpecAsync();
    }

    public string Status
    {
        get => _status;
        set => SetProperty(ref _status, value);
    }

    public MainWindowViewModel(
        CdiscDatasetCatalog cdiscDatasetCatalog,
        TemplateVariableDomainCatalog templateVariableDomainCatalog,
        SuggestionOptionsAsyncLoader suggestionOptionsAsyncLoader,
        IMessageService messageService)
    {
        VariableDomainCatalog = templateVariableDomainCatalog;
        _factory = new DockFactory(
            this,
            cdiscDatasetCatalog,
            suggestionOptionsAsyncLoader,
            messageService);
        ResetLayout();
    }

    private void ResetLayout()
    {
        Layout = _factory.CreateLayout();
    }
}
