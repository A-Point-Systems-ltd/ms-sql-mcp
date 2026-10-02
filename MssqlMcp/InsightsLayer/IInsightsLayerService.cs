using Mssql.McpServer.InsightsLayer.Models;

namespace Mssql.McpServer.InsightsLayer;

/// <summary>
/// Optional AI insights cache + DDL audit integration for the MCP server.
/// </summary>
public interface IInsightsLayerService
{
    /// <summary>
    /// True unless <c>USE_INSIGHTS_LAYER</c> is explicitly set to a falsey value
    /// (<c>false</c>, <c>0</c>, <c>no</c>, <c>off</c>, <c>disabled</c>). The env var is opt-OUT only;
    /// missing or empty means enabled.
    /// </summary>
    bool IsEnabled { get; }

    Task<LayerStatus> GetStatusAsync(CancellationToken cancellationToken = default);

    Task<DbOperationResult> InstallLayerAsync(CancellationToken cancellationToken = default);

    Task<(SchemaInsight? insight, InsightFreshness freshness)> GetInsightForObjectAsync(
        string objectType,
        string? schemaName,
        string objectName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Ensures a baseline insight row exists for the requested object.
    /// If a row already exists it is returned unchanged.
    /// If missing, a mechanical low-confidence baseline is created and then returned.
    /// </summary>
    Task<(SchemaInsight? insight, InsightFreshness freshness)> EnsureBaselineForObjectAsync(
        string objectType,
        string? schemaName,
        string objectName,
        CancellationToken cancellationToken = default);

    Task<DbOperationResult> UpsertInsightAsync(SchemaInsight input, CancellationToken cancellationToken = default);

    /// <summary>
    /// Decides whether the agent should update <paramref name="insight"/>, and returns what it needs to do so:
    /// the previous authored insight, recent DDL events, or the current row count. Returns null when the cached
    /// insight is current. Authored insights that cannot trigger the row-count rule cost no database round trip.
    /// </summary>
    Task<InsightEnrichmentContext?> GetEnrichmentContextAsync(
        SchemaInsight insight,
        InsightFreshness freshness,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Processes new <c>dbo.DDL_AuditLog</c> rows (when present) and archives affected insights; when no audit
    /// rows were processed, runs a rotating fingerprint scan instead. Runs are serialized per database with
    /// <c>sp_getapplock</c>; a run that finds the lock held returns without doing anything.
    /// </summary>
    /// <returns>False when another process held the processing lock and this run was skipped; true otherwise.</returns>
    Task<bool> ProcessDdlChangesAsync(CancellationToken cancellationToken = default);

    Task<DbOperationResult> ListInsightsAsync(string? schemaName, string? objectType, int take, CancellationToken cancellationToken = default);

    Task<DbOperationResult> GetHistoryAsync(string? schemaName, string? objectName, int take, CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs <see cref="ProcessDdlChangesAsync"/> and returns recent insight summaries.
    /// <c>topQueryPatterns</c> is kept in the response shape for compatibility and currently returns an empty list.
    /// <c>ddlProcessing</c> reports the reconciliation outcome: <c>completed</c>, <c>skipped_busy</c> (another run
    /// held the lock), <c>not_installed</c>, or <c>failed</c>.
    /// </summary>
    Task<DbOperationResult> RefreshInsightsAsync(CancellationToken cancellationToken = default);
}
