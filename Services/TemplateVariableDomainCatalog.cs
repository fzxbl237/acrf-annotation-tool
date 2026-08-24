using System.Collections.Frozen;
using Microsoft.Data.Sqlite;

namespace AcrfAnnotationTool.Services;

public sealed class TemplateVariableDomainCatalog
{
    private readonly Task<FrozenDictionary<string, string>> _loadTask;

    private TemplateVariableDomainCatalog(string databasePath)
    {
        _loadTask = LoadAsync(databasePath);
    }

    public static TemplateVariableDomainCatalog StartFromApplicationDatabase()
    {
        var databasePath = Path.Combine(AppContext.BaseDirectory, "cdisc_setting.db");
        return new TemplateVariableDomainCatalog(databasePath);
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

    public static async Task<FrozenDictionary<string, string>> LoadAsync(string databasePath)
    {
        try
        {
            return await Task.Run(() => LoadCore(databasePath)).ConfigureAwait(false);
        }
        catch
        {
            return CreateEmpty();
        }
    }

    private static FrozenDictionary<string, string> LoadCore(string databasePath)
    {
        if (!File.Exists(databasePath))
            return CreateEmpty();

        try
        {
            using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT DatasetName, VariableName FROM template_variable WHERE DatasetName IS NOT NULL AND VariableName IS NOT NULL ORDER BY Id";

            var domains = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var domain = reader.IsDBNull(0) ? string.Empty : reader.GetString(0).Trim();
                var variableName = reader.IsDBNull(1) ? string.Empty : reader.GetString(1).Trim();
                if (string.IsNullOrWhiteSpace(domain) || string.IsNullOrWhiteSpace(variableName))
                    continue;

                domains.TryAdd(variableName, domain);
            }

            return domains.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return CreateEmpty();
        }
    }

    private static FrozenDictionary<string, string> CreateEmpty() =>
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            .ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
}
