using AcrfAnnotationTool.Services.Interface;

namespace AcrfAnnotationTool.Services;

public sealed class CdiscDatasetCatalog
{
    private readonly Dictionary<string, Dictionary<string, string>> _labelsByLanguage =
        new(StringComparer.OrdinalIgnoreCase);
    private string _language = "en";

    public CdiscDatasetCatalog(IDatabaseService databaseService)
    {
        try
        {
            foreach (var dataset in databaseService.QueryTemplateDatasets())
            {
                var name = dataset.Name?.Trim() ?? string.Empty;
                var language = dataset.Language?.Trim() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(language) ||
                    IsAssociatedPersonsDataset(name))
                    continue;

                if (!_labelsByLanguage.TryGetValue(language, out var labels))
                {
                    labels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    _labelsByLanguage[language] = labels;
                }

                labels[name] = dataset.Label?.Trim() ?? string.Empty;
            }
        }
        catch
        {
            // Keep application startup resilient when the optional settings database is unavailable.
        }
    }

    public void SetLanguage(bool useChinese) => _language = useChinese ? "zh" : "en";

    private static bool IsAssociatedPersonsDataset(string name) =>
        name.Length == 4 &&
        name.StartsWith("AP", StringComparison.OrdinalIgnoreCase);

    public string? FormatDomain(string? domain)
    {
        if (string.IsNullOrWhiteSpace(domain))
            return null;

        var name = domain.Trim();
        if (!_labelsByLanguage.TryGetValue(_language, out var labels))
            labels = _labelsByLanguage.GetValueOrDefault("en") ?? new Dictionary<string, string>();

        var match = labels.FirstOrDefault(pair =>
            string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase));
        if (match.Key is null || string.IsNullOrWhiteSpace(match.Value))
            return name;

        return $"{match.Key} ({match.Value})";
    }
}
