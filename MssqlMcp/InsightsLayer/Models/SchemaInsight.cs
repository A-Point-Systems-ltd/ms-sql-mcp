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

    /// <summary>
    /// Database server local time (written with <c>GETDATE()</c>), unspecified kind. Compare it only
    /// with server-side values; the MCP host may be in a different time zone.
    /// </summary>
    public DateTime LastAnalyzed { get; init; }

    public string? AnalyzedBy { get; init; }

    public int Version { get; init; }

    public DateTime? ModifyDateAtAnalysis { get; init; }

    public int? ObjectIdAtAnalysis { get; init; }

    public string? SchemaFingerprint { get; init; }

    /// <summary>
    /// True when <see cref="LastAnalyzed"/> falls before today's date on the database server clock
    /// (computed in SQL). Internal, so it is not serialized into tool responses.
    /// </summary>
    internal bool AnalyzedBeforeServerToday { get; init; }
}
