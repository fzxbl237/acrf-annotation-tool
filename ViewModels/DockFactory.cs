using AcrfAnnotationTool.Services;
using AcrfAnnotationTool.Services.Interface;
using AcrfAnnotationTool.ViewModels.Documents;

namespace AcrfAnnotationTool.ViewModels;

public sealed class DockFactory
{
    private readonly MainWindowViewModel _owner;
    private readonly CdiscDatasetCatalog _cdiscDatasetCatalog;
    private readonly SuggestionOptionsAsyncLoader _suggestionOptionsAsyncLoader;
    private readonly IMessageService _messageService;

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
    }

    public AcrfDocumentViewModel CreateEditor() => new(
        _cdiscDatasetCatalog,
        _owner.VariableDomainCatalog,
        _suggestionOptionsAsyncLoader,
        _messageService)
    {
        AppOwner = _owner
    };
}
