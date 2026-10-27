using CommunityToolkit.Mvvm.ComponentModel;
using AcrfAnnotationTool.Services;
using AcrfAnnotationTool.Services.Interface;
using AcrfAnnotationTool.ViewModels.Documents;

namespace AcrfAnnotationTool.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    public TemplateVariableDomainCatalog VariableDomainCatalog { get; }
    public AcrfDocumentViewModel Editor { get; }
    private string _status = "就绪";

    public Task<SpecExportOutcome> ExportSpecForShutdownAsync() => Editor.ExportSpecAsync();

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
        var factory = new DockFactory(
            this,
            cdiscDatasetCatalog,
            suggestionOptionsAsyncLoader,
            messageService);
        Editor = factory.CreateEditor();
    }
}
