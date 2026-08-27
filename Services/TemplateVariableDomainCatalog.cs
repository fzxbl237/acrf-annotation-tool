using System.Collections.Frozen;
using AcrfAnnotationTool.Services.Interface;

namespace AcrfAnnotationTool.Services;

public sealed class TemplateVariableDomainCatalog
{
    private readonly IDatabaseService _databaseService;
    private readonly Task<FrozenDictionary<string, string>> _loadTask;

    public TemplateVariableDomainCatalog(IDatabaseService databaseService)
    {
        _databaseService = databaseService;
        _loadTask = LoadAsync();
    }

    public Task<FrozenDictionary<string, string>> LoadingTask => _loadTask;

    public async Task<string?> FindDomainAsync(string? annotationText, string? variableName = null)
    {
        var domains = await _loadTask.ConfigureAwait(true);
        foreach (var candidate in GetVariableCandidates(annotationText, variableName))
        {
            if (domains.TryGetValue(candidate, out var domain))
                return domain;
        }

        return null;
    }

    public async Task SynchronizeAsync(IEnumerable<Models.Annotation> annotations)
    {
        var domains = await _loadTask.ConfigureAwait(true);
        foreach (var annotation in annotations)
        {
            annotation.AnnotationTextDomain = string.IsNullOrWhiteSpace(annotation.AnnotationText)
                ? string.Empty
                : FindDomain(domains, annotation.AnnotationText, annotation.VarName) ?? string.Empty;
        }
    }

    private static string? FindDomain(
        FrozenDictionary<string, string> domains,
        string? annotationText,
        string? variableName)
    {
        foreach (var candidate in GetVariableCandidates(annotationText, variableName))
        {
            if (domains.TryGetValue(candidate, out var domain))
                return domain;
        }

        return null;
    }

    private static IEnumerable<string> GetVariableCandidates(
        string? annotationText,
        string? variableName)
    {
        if (!string.IsNullOrWhiteSpace(annotationText))
        {
            var text = annotationText.Trim();
            var suppIndex = text.IndexOf("in SUPP", StringComparison.OrdinalIgnoreCase);
            if (suppIndex >= 0)
            {
                var suppDomain = text[(suppIndex + "in SUPP".Length)..]
                    .Split([' ', '\t', '\r', '\n', '='], StringSplitOptions.RemoveEmptyEntries)
                    .FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(suppDomain))
                    yield return suppDomain.Trim();
            }

            var variable = text
                .Split(['=', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault();
            foreach (var candidate in ExpandVariableName(variable))
                yield return candidate;
        }

        foreach (var candidate in ExpandVariableName(variableName))
            yield return candidate;
    }

    private static IEnumerable<string> ExpandVariableName(string? variableName)
    {
        if (string.IsNullOrWhiteSpace(variableName))
            yield break;

        var normalized = variableName.Trim();
        yield return normalized;

        var separatorIndex = normalized.LastIndexOf('.');
        if (separatorIndex >= 0 && separatorIndex + 1 < normalized.Length)
            yield return normalized[(separatorIndex + 1)..];
    }

    private async Task<FrozenDictionary<string, string>> LoadAsync()
    {
        try
        {
            return await Task.Run(LoadCore).ConfigureAwait(false);
        }
        catch
        {
            return CreateEmpty();
        }
    }

    private FrozenDictionary<string, string> LoadCore()
    {
        var domains = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var variable in _databaseService.QueryTemplateVariables())
        {
            var domain = variable.DatasetName?.Trim() ?? string.Empty;
            var variableName = variable.VariableName?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(domain) ||
                string.IsNullOrWhiteSpace(variableName) ||
                IsAssociatedPersonsDataset(domain))
                continue;

            domains.TryAdd(variableName, domain);
        }

        return domains.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsAssociatedPersonsDataset(string name) =>
        name.Length == 4 &&
        name.StartsWith("AP", StringComparison.OrdinalIgnoreCase);

    private static FrozenDictionary<string, string> CreateEmpty() =>
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            .ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
}
