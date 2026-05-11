using Mssql.McpServer.InsightsLayer.Models;

namespace Mssql.McpServer.InsightsLayer;

/// <summary>
/// Optional AI insights cache + DDL audit integration for the MCP server.
/// </summary>
public interface IInsightsLayerService
{
    /// <summary>
    /// True when <c>USE_INSIGHTS_LAYER</c> is set to <c>true</c> or <c>1</c>.
    /// </summary>
    bool IsEnabled { get; }

    Task<LayerStatus> GetStatusAsync(CancellationToken cancellationToken = default);

    Task<DbOperationResult> InstallLayerAsync(CancellationToken cancellationToken = default);

    Task<(SchemaInsight? insight, InsightFreshness freshness)> GetInsightForObjectAsync(
        string objectType,
        string? schemaName,
        string objectName,
        CancellationToken cancellationToken = default);

    Task<DbOperationResult> UpsertInsightAsync(SchemaInsight input, CancellationToken cancellationToken = default);

    /// <summary>
    /// Processes new <c>dbo.DDL_AuditLog</c> rows (when present) and archives affected insights; otherwise scans fingerprints.
    /// </summary>
    Task ProcessDdlChangesAsync(CancellationToken cancellationToken = default);

    Task<DbOperationResult> ListInsightsAsync(string? schemaName, string? objectType, int take, CancellationToken cancellationToken = default);

    Task<DbOperationResult> GetHistoryAsync(string? schemaName, string? objectName, int take, CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs <see cref="ProcessDdlChangesAsync"/> and returns recent insight summaries plus top query patterns (in-code equivalents of former views).
    /// </summary>
    Task<DbOperationResult> RefreshInsightsAsync(CancellationToken cancellationToken = default);
}
