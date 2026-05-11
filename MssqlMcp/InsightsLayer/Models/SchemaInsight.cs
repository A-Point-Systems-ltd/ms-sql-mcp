namespace Mssql.McpServer.InsightsLayer.Models;

/// <summary>
/// Row from <c>AIInsights.SchemaInsights</c> (table-level when <see cref="ColumnName"/> is null).
/// </summary>
public sealed class SchemaInsight
{
    public int InsightId { get; init; }

    public string ObjectType { get; init; } = string.Empty;

    public string? SchemaName { get; init; }

    public string ObjectName { get; init; } = string.Empty;

    public string? ColumnName { get; init; }

    public string? Description { get; init; }

    public string? BusinessPurpose { get; init; }

    public string? DataPatterns { get; init; }

    public string? UsageGuidelines { get; init; }

    public string? RelatedObjects { get; init; }

    public string? LlmModel { get; init; }

    public decimal? Confidence { get; init; }

    public DateTime LastAnalyzed { get; init; }

    public string? AnalyzedBy { get; init; }

    public int Version { get; init; }

    public DateTime? ModifyDateAtAnalysis { get; init; }

    public int? ObjectIdAtAnalysis { get; init; }

    public string? SchemaFingerprint { get; init; }
}
