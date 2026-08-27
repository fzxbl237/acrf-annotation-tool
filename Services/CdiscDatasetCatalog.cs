using AcrfAnnotationTool.Services.Interface;

namespace AcrfAnnotationTool.Services;

public sealed class CdiscDatasetCatalog
{
    private readonly Dictionary<string, string> _labels;

    public CdiscDatasetCatalog(IDatabaseService databaseService)
    {
        var labels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var dataset in databaseService.QueryTemplateDatasets())
            {
                var name = dataset.Name?.Trim() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(name) || IsAssociatedPersonsDataset(name))
                    continue;

                labels[name] = dataset.Label?.Trim() ?? string.Empty;
            }
        }
        catch
        {
            // Keep application startup resilient when the optional settings database is unavailable.
        }

        _labels = labels;
    }

    private static bool IsAssociatedPersonsDataset(string name) =>
        name.Length == 4 &&
        name.StartsWith("AP", StringComparison.OrdinalIgnoreCase);

    public string? FormatDomain(string? domain)
    {
        if (string.IsNullOrWhiteSpace(domain))
            return null;

        var name = domain.Trim();
        var match = _labels.FirstOrDefault(pair =>
            string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase));
        if (match.Key is null || string.IsNullOrWhiteSpace(match.Value))
            return name;

        return $"{match.Key} ({match.Value})";
    }
}
