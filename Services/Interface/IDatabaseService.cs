using AcrfAnnotationTool.Models;

namespace AcrfAnnotationTool.Services.Interface;

public interface IDatabaseService
{
    IReadOnlyList<TemplateDatasetRecord> QueryTemplateDatasets();

    IReadOnlyList<TemplateVariableRecord> QueryTemplateVariables();

    void InitializeSuggestions();

    IReadOnlyList<Suggestion> QuerySuggestions(string? searchText);
}

public sealed class TemplateDatasetRecord
{
    public string? Name { get; set; }

    public string? Label { get; set; }
}

public sealed class TemplateVariableRecord
{
    public long? Id { get; set; }

    public string? DatasetName { get; set; }

    public string? VariableName { get; set; }

    public string? Label { get; set; }
}
