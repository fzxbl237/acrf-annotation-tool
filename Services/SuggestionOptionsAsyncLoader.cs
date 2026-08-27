using System.Threading;
using AtomUI.Desktop.Controls;
using AcrfAnnotationTool.Services.Interface;

namespace AcrfAnnotationTool.Services;

public sealed class SuggestionOptionsAsyncLoader(IDatabaseService databaseService) : ICompleteOptionsAsyncLoader
{
    public async Task<CompleteOptionsLoadResult> LoadAsync(string? context, CancellationToken token)
    {
        var suggestions = await Task.Run(
            () => databaseService.QuerySuggestions(context),
            token);

        var options = suggestions
            .Select(suggestion => (IAutoCompleteOption)new SuggestionAutoCompleteOption
            {
                Content = suggestion.Content,
                Header = $"{suggestion.Content} {suggestion.Description}",
                SuggestionContent = suggestion.Content,
                SuggestionDescription = suggestion.Description
            })
            .ToList();

        return new CompleteOptionsLoadResult { Data = options };
    }
}

public record SuggestionAutoCompleteOption : AutoCompleteOption
{
    public string? SuggestionContent { get; set; }
    public string? SuggestionDescription { get; set; }
}
