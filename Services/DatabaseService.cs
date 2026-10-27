using System.Text.RegularExpressions;
using AcrfAnnotationTool.Models;
using AcrfAnnotationTool.Services.Interface;
using Microsoft.Data.Sqlite;

namespace AcrfAnnotationTool.Services;

public sealed partial class DatabaseService(Func<SqliteConnection> createConnection) : IDatabaseService
{
    [GeneratedRegex("^(?<prefix>[A-Za-z0-9]+)TESTCD\\s*=\\s*(?<value>.+)$", RegexOptions.CultureInvariant)]
    private static partial Regex TestCodeSuggestionRegex();

    public IReadOnlyList<TemplateDatasetRecord> QueryTemplateDatasets()
    {
        using var connection = createConnection();
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT Name, Label, Language FROM template_dataset WHERE Name IS NOT NULL";

        using var reader = command.ExecuteReader();
        var nameOrdinal = reader.GetOrdinal("Name");
        var labelOrdinal = reader.GetOrdinal("Label");
        var languageOrdinal = reader.GetOrdinal("Language");
        var records = new List<TemplateDatasetRecord>();

        while (reader.Read())
        {
            records.Add(new TemplateDatasetRecord
            {
                Name = ReadNullableString(reader, nameOrdinal),
                Label = ReadNullableString(reader, labelOrdinal),
                Language = ReadNullableString(reader, languageOrdinal)
            });
        }

        return records;
    }

    public IReadOnlyList<TemplateVariableRecord> QueryTemplateVariables()
    {
        using var connection = createConnection();
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT Id, DatasetName, VariableName, Label FROM template_variable " +
            "WHERE DatasetName IS NOT NULL AND VariableName IS NOT NULL ORDER BY Id";

        using var reader = command.ExecuteReader();
        var idOrdinal = reader.GetOrdinal("Id");
        var datasetNameOrdinal = reader.GetOrdinal("DatasetName");
        var variableNameOrdinal = reader.GetOrdinal("VariableName");
        var labelOrdinal = reader.GetOrdinal("Label");
        var records = new List<TemplateVariableRecord>();

        while (reader.Read())
        {
            records.Add(new TemplateVariableRecord
            {
                Id = reader.IsDBNull(idOrdinal) ? null : reader.GetInt64(idOrdinal),
                DatasetName = ReadNullableString(reader, datasetNameOrdinal),
                VariableName = ReadNullableString(reader, variableNameOrdinal),
                Label = ReadNullableString(reader, labelOrdinal)
            });
        }

        return records;
    }

    public IReadOnlyList<Suggestion> QuerySuggestions(string? searchText)
    {
        using var connection = createConnection();
        connection.Open();

        var keyword = searchText?.Trim();
        using var command = connection.CreateCommand();
        if (string.IsNullOrWhiteSpace(keyword))
        {
            command.CommandText =
                "SELECT Id, Content, Description FROM suggestion ORDER BY Content LIMIT 50";
        }
        else
        {
            var escapedKeyword = EscapeLikePattern(keyword);
            command.CommandText =
                "SELECT Id, Content, Description FROM suggestion " +
                "WHERE Content LIKE $prefix ESCAPE $escape ORDER BY Content LIMIT 50";
            command.Parameters.AddWithValue("$prefix", $"{escapedKeyword}%");
            command.Parameters.AddWithValue("$escape", "\\");
        }

        var suggestions = new List<Suggestion>(50);
        ReadSuggestions(command, suggestions);
        if (!string.IsNullOrWhiteSpace(keyword) && suggestions.Count < 50)
        {
            command.CommandText =
                "SELECT Id, Content, Description FROM suggestion " +
                "WHERE Content NOT LIKE $prefix ESCAPE $escape AND " +
                "(Content LIKE $contains ESCAPE $escape " +
                "OR (Description IS NOT NULL AND Description LIKE $contains ESCAPE $escape)) " +
                "ORDER BY Content LIMIT $remaining";
            command.Parameters.AddWithValue("$contains", $"%{EscapeLikePattern(keyword)}%");
            command.Parameters.AddWithValue("$remaining", 50 - suggestions.Count);
            ReadSuggestions(command, suggestions);
        }

        return suggestions;
    }

    private static void ReadSuggestions(SqliteCommand command, List<Suggestion> suggestions)
    {
        using var reader = command.ExecuteReader();
        var idOrdinal = reader.GetOrdinal("Id");
        var contentOrdinal = reader.GetOrdinal("Content");
        var descriptionOrdinal = reader.GetOrdinal("Description");
        while (reader.Read())
        {
            suggestions.Add(new Suggestion
            {
                Id = reader.GetInt32(idOrdinal),
                Content = reader.GetString(contentOrdinal),
                Description = ReadNullableString(reader, descriptionOrdinal)
            });
        }
    }

    public void InitializeSuggestions()
    {
        using var connection = createConnection();
        connection.Open();

        using (var createTableCommand = connection.CreateCommand())
        {
            createTableCommand.CommandText =
                "CREATE TABLE IF NOT EXISTS suggestion (" +
                "Id INTEGER PRIMARY KEY AUTOINCREMENT, " +
                "Content TEXT NOT NULL, " +
                "Description TEXT NULL)";
            createTableCommand.ExecuteNonQuery();
        }

        var rows = ReadSuggestionSeeds(connection);
        var derivedRows = rows
            .Select(CreateTestResultSuggestion)
            .Where(item => item is not null)
            .Select(item => item!)
            .ToList();
        rows.AddRange(derivedRows);
        rows.Add(new Suggestion
        {
            Content = "[NOT SUBMITTED]",
            Description = "Not Submitted"
        });
        rows.AddRange(CreateFindingsNotDoneSuggestions(connection));

        rows = rows
            .GroupBy(item => new { item.Content, item.Description })
            .Select(group => group.First())
            .ToList();

        using var transaction = connection.BeginTransaction();
        try
        {
            using (var deleteCommand = connection.CreateCommand())
            {
                deleteCommand.Transaction = transaction;
                deleteCommand.CommandText = "DELETE FROM suggestion";
                deleteCommand.ExecuteNonQuery();
            }

            if (rows.Count > 0)
            {
                using var insertCommand = connection.CreateCommand();
                insertCommand.Transaction = transaction;
                insertCommand.CommandText =
                    "INSERT INTO suggestion (Content, Description) VALUES ($content, $description)";
                var contentParameter = insertCommand.Parameters.Add("$content", SqliteType.Text);
                var descriptionParameter = insertCommand.Parameters.Add("$description", SqliteType.Text);

                foreach (var row in rows)
                {
                    contentParameter.Value = row.Content;
                    descriptionParameter.Value = row.Description is null
                        ? DBNull.Value
                        : row.Description;
                    insertCommand.ExecuteNonQuery();
                }
            }

            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    private static List<Suggestion> ReadSuggestionSeeds(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT VariableName AS Content, Label AS Description " +
            "FROM template_variable " +
            "WHERE VariableName IS NOT NULL AND TRIM(VariableName) <> '' " +
            "UNION " +
            "SELECT variable_name || ' = ' || code_value AS Content, decoded_value AS Description " +
            "FROM variable_code_list " +
            "INNER JOIN code_list_term ON variable_code_list.code_list_ref = code_list_term.code_list_ref " +
            "WHERE variable_name IS NOT NULL AND TRIM(variable_name) <> '' " +
            "AND code_value IS NOT NULL AND TRIM(code_value) <> '' " +
            "ORDER BY Content, Description";

        using var reader = command.ExecuteReader();
        var contentOrdinal = reader.GetOrdinal("Content");
        var descriptionOrdinal = reader.GetOrdinal("Description");
        var rows = new List<Suggestion>();

        while (reader.Read())
        {
            var content = ReadNullableString(reader, contentOrdinal)?.Trim() ?? string.Empty;
            var description = ReadNullableString(reader, descriptionOrdinal);
            var suggestion = new Suggestion
            {
                Content = content,
                Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim()
            };

            if (!string.IsNullOrWhiteSpace(suggestion.Content))
                rows.Add(suggestion);
        }

        return rows;
    }

    private static Suggestion? CreateTestResultSuggestion(Suggestion suggestion)
    {
        var match = TestCodeSuggestionRegex().Match(suggestion.Content);
        if (!match.Success)
            return null;

        var prefix = match.Groups["prefix"].Value;
        var value = match.Groups["value"].Value.Trim();
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return new Suggestion
        {
            Content = $"{prefix}ORRES when {prefix}TESTCD = {value}",
            Description = suggestion.Description
        };
    }

    private static List<Suggestion> CreateFindingsNotDoneSuggestions(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT Name FROM template_dataset " +
            "WHERE UPPER(TRIM(Class)) = 'FINDINGS' " +
            "AND Name IS NOT NULL AND TRIM(Name) <> ''";

        using var reader = command.ExecuteReader();
        var nameOrdinal = reader.GetOrdinal("Name");
        var domains = new List<string>();
        while (reader.Read())
        {
            domains.Add(ReadNullableString(reader, nameOrdinal)?.Trim() ?? string.Empty);
        }

        return domains
            .Where(name =>
                name.All(char.IsLetterOrDigit) &&
                !string.Equals(name, "FINDINGS", StringComparison.OrdinalIgnoreCase))
            .Select(name => new Suggestion
            {
                Content = $"{name}STAT = NOT DONE when {name}TESTCD = {name}ALL",
                Description = $"Not done for all {name} test"
            })
            .ToList();
    }

    private static string? ReadNullableString(SqliteDataReader reader, int ordinal)
    {
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private static string EscapeLikePattern(string value)
    {
        return value
            .Replace("\\", "\\\\")
            .Replace("%", "\\%")
            .Replace("_", "\\_");
    }
}
