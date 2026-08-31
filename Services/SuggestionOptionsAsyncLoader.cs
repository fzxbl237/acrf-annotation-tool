using System.Diagnostics;
using System.Threading;
using AtomUI.Desktop.Controls;
using AcrfAnnotationTool.Services.Interface;
using PatChes.Controls.DataGrid;

namespace AcrfAnnotationTool.Services;

public sealed class SuggestionOptionsAsyncLoader(IDatabaseService databaseService) : ICompleteOptionsAsyncLoader
{
    public async Task<CompleteOptionsLoadResult> LoadAsync(string? context, CancellationToken token)
    {
        var stopwatch = Stopwatch.StartNew();
        var contextLength = context?.Length ?? 0;
        var contextHash = context?.GetHashCode() ?? 0;
        AutoCompleteDiagnostics.Log($"Loader.LoadAsync.start length={contextLength} hash={contextHash:X8} canceled={token.IsCancellationRequested}");

        try
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

            AutoCompleteDiagnostics.Log($"Loader.LoadAsync.success length={contextLength} hash={contextHash:X8} suggestions={suggestions.Count} options={options.Count} elapsedMs={stopwatch.ElapsedMilliseconds}");
            return new CompleteOptionsLoadResult { Data = options };
        }
        catch (OperationCanceledException)
        {
            AutoCompleteDiagnostics.Log($"Loader.LoadAsync.canceled length={contextLength} hash={contextHash:X8} elapsedMs={stopwatch.ElapsedMilliseconds}");
            throw;
        }
        catch (Exception exception)
        {
            AutoCompleteDiagnostics.Log($"Loader.LoadAsync.error length={contextLength} hash={contextHash:X8} type={exception.GetType().Name} message={exception.Message} elapsedMs={stopwatch.ElapsedMilliseconds}");
            throw;
        }
    }
}

public record SuggestionAutoCompleteOption : AutoCompleteOption
{
    public string? SuggestionContent { get; set; }
    public string? SuggestionDescription { get; set; }
}
