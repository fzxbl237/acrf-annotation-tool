using SqlSugar;

namespace AcrfAnnotationTool.Models;

[SugarTable("suggestion")]
public sealed class Suggestion
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
    public int Id { get; set; }

    [SugarColumn(IsNullable = false)]
    public string Content { get; set; } = string.Empty;

    [SugarColumn(IsNullable = true)]
    public string? Description { get; set; }
}
