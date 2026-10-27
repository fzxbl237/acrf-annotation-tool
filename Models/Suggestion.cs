namespace AcrfAnnotationTool.Models;

public sealed class Suggestion
{
    public int Id { get; set; }

    public string Content { get; set; } = string.Empty;

    public string? Description { get; set; }
}
