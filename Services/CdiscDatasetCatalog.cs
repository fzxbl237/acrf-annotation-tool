using Microsoft.Data.Sqlite;

namespace AcrfAnnotationTool.Services;

public sealed class CdiscDatasetCatalog
{
    private readonly Dictionary<string, string> _labels;

    private CdiscDatasetCatalog(Dictionary<string, string> labels)
    {
        _labels = labels;
    }

    public static CdiscDatasetCatalog LoadFromApplicationDatabase()
    {
        var databasePath = Path.Combine(AppContext.BaseDirectory, "cdisc_setting.db");
        if (!File.Exists(databasePath))
            return new CdiscDatasetCatalog(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

        try
        {
            using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT Name, Label FROM template_dataset WHERE Name IS NOT NULL";

            var labels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var name = reader.IsDBNull(0) ? string.Empty : reader.GetString(0).Trim();
                if (string.IsNullOrWhiteSpace(name))
                    continue;

                var label = reader.IsDBNull(1) ? string.Empty : reader.GetString(1).Trim();
                labels[name] = label;
            }

            return new CdiscDatasetCatalog(labels);
        }
        catch
        {
            return new CdiscDatasetCatalog(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
        }
    }

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
