using System.Text.RegularExpressions;
using AcrfAnnotationTool.Models;
using AcrfAnnotationTool.Services.Interface;
using SqlSugar;

namespace AcrfAnnotationTool.Services;

public sealed class DatabaseService(ISqlSugarClient sqlSugar) : IDatabaseService
{
    private static readonly Regex TestCodeSuggestionPattern = new(
        "^(?<prefix>[A-Za-z0-9]+)TESTCD\\s*=\\s*(?<value>.+)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public IReadOnlyList<TemplateDatasetRecord> QueryTemplateDatasets()
    {
        return sqlSugar.Ado.SqlQuery<TemplateDatasetRecord>(
            "SELECT Name, Label, Language FROM template_dataset WHERE Name IS NOT NULL");
    }

    public IReadOnlyList<TemplateVariableRecord> QueryTemplateVariables()
    {
        return sqlSugar.Ado.SqlQuery<TemplateVariableRecord>(
            "SELECT Id, DatasetName, VariableName, Label FROM template_variable " +
            "WHERE DatasetName IS NOT NULL AND VariableName IS NOT NULL ORDER BY Id");
    }

    public IReadOnlyList<Suggestion> QuerySuggestions(string? searchText)
    {
        var keyword = searchText?.Trim();
        if (string.IsNullOrWhiteSpace(keyword))
        {
            return sqlSugar.Queryable<Suggestion>()
                .OrderBy(item => item.Content)
                .Take(50)
                .ToList();
        }

        var prefixMatches = sqlSugar.Queryable<Suggestion>()
            .Where(item => item.Content.StartsWith(keyword))
            .OrderBy(item => item.Content)
            .Take(50)
            .ToList();
        if (prefixMatches.Count >= 50)
            return prefixMatches;

        var remainingQuery = sqlSugar.Queryable<Suggestion>()
            .Where(item =>
                item.Content.Contains(keyword) ||
                (item.Description != null && item.Description.Contains(keyword)));
        if (prefixMatches.Count > 0)
        {
            var prefixIds = prefixMatches.Select(item => item.Id).ToList();
            remainingQuery = remainingQuery.Where(item => !prefixIds.Contains(item.Id));
        }

        var remaining = remainingQuery
            .OrderBy(item => item.Content)
            .Take(50 - prefixMatches.Count)
            .ToList();

        prefixMatches.AddRange(remaining);
        return prefixMatches;
    }

    public void InitializeSuggestions()
    {
        sqlSugar.CodeFirst.InitTables<Suggestion>();

        var suggestions = sqlSugar.Ado.SqlQuery<SuggestionSeed>(
            "SELECT VariableName AS Content, Label AS Description " +
            "FROM template_variable " +
            "WHERE VariableName IS NOT NULL AND TRIM(VariableName) <> '' " +
            "UNION " +
            "SELECT variable_name || ' = ' || code_value AS Content, decoded_value AS Description " +
            "FROM variable_code_list " +
            "INNER JOIN code_list_term ON variable_code_list.code_list_ref = code_list_term.code_list_ref " +
            "WHERE variable_name IS NOT NULL AND TRIM(variable_name) <> '' " +
            "AND code_value IS NOT NULL AND TRIM(code_value) <> '' " +
            "ORDER BY Content, Description");

        var rows = suggestions
            .Select(item => new Suggestion
            {
                Content = item.Content?.Trim() ?? string.Empty,
                Description = string.IsNullOrWhiteSpace(item.Description)
                    ? null
                    : item.Description.Trim()
            })
            .Where(item => !string.IsNullOrWhiteSpace(item.Content))
            .ToList();

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
        rows.AddRange(CreateFindingsNotDoneSuggestions());

        rows = rows
            .GroupBy(item => new { item.Content, item.Description })
            .Select(group => group.First())
            .ToList();

        sqlSugar.Ado.BeginTran();
        try
        {
            sqlSugar.Deleteable<Suggestion>().ExecuteCommand();
            if (rows.Count > 0)
                sqlSugar.Insertable(rows).ExecuteCommand();

            sqlSugar.Ado.CommitTran();
        }
        catch
        {
            sqlSugar.Ado.RollbackTran();
            throw;
        }
    }

    private static Suggestion? CreateTestResultSuggestion(Suggestion suggestion)
    {
        var match = TestCodeSuggestionPattern.Match(suggestion.Content);
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

    private IEnumerable<Suggestion> CreateFindingsNotDoneSuggestions()
    {
        var domains = sqlSugar.Ado.SqlQuery<FindingsDomainSeed>(
            "SELECT Name FROM template_dataset " +
            "WHERE UPPER(TRIM(Class)) = 'FINDINGS' " +
            "AND Name IS NOT NULL AND TRIM(Name) <> ''");

        return domains
            .Select(item => item.Name?.Trim() ?? string.Empty)
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

    private sealed class FindingsDomainSeed
    {
        public string? Name { get; set; }
    }

    private sealed class SuggestionSeed
    {
        public string? Content { get; set; }

        public string? Description { get; set; }
    }
}

