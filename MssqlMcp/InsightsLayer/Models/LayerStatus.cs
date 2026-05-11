namespace Mssql.McpServer.InsightsLayer.Models;

/// <summary>
/// Snapshot of whether optional AI insight objects exist in the connected database.
/// </summary>
public sealed class LayerStatus
{
    public bool LayerEnabledViaEnvironment { get; init; }

    public bool AiInsightsSchemaExists { get; init; }

    public bool DdlAuditTableExists { get; init; }

    public bool DdlAuditTriggerEnabled { get; init; }

    public int SchemaInsightsCount { get; init; }

    public int LastProcessedAuditId { get; init; }

    public DateTime? LastProcessedAt { get; init; }
}
