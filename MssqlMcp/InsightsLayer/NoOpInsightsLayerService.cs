using Mssql.McpServer.InsightsLayer.Models;

namespace Mssql.McpServer.InsightsLayer;

/// <summary>
/// Used when the AI Insights layer has been explicitly disabled via <c>USE_INSIGHTS_LAYER</c>
/// (e.g. <c>false</c>, <c>0</c>, <c>off</c>); keeps DI simple without null checks in tools.
/// </summary>
public sealed class NoOpInsightsLayerService : IInsightsLayerService
{
    public static NoOpInsightsLayerService Instance { get; } = new();

    private NoOpInsightsLayerService()
    {
    }

    public bool IsEnabled => false;

    public Task<LayerStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new LayerStatus
        {
            LayerEnabledViaEnvironment = false,
            AiInsightsSchemaExists = false,
            DdlAuditTableExists = false,
            DdlAuditTriggerEnabled = false,
            SchemaInsightsCount = 0,
            LastProcessedAuditId = 0,
            LastProcessedAt = null
        });
    }

    public Task<DbOperationResult> InstallLayerAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new DbOperationResult(
            success: false,
            error: "AI Insights layer is disabled. Remove USE_INSIGHTS_LAYER from the MCP environment (or set it to true) and restart the server. The layer is enabled by default; only false/0/off/disabled turns it off."));
    }

    public Task<(SchemaInsight? insight, InsightFreshness freshness)> GetInsightForObjectAsync(
        string objectType,
        string? schemaName,
        string objectName,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult<(SchemaInsight?, InsightFreshness)>((null, InsightFreshness.LayerDisabled));
    }

    public Task<DbOperationResult> UpsertInsightAsync(SchemaInsight input, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new DbOperationResult(success: false, error: "AI Insights layer is disabled."));
    }

    public Task ProcessDdlChangesAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<DbOperationResult> ListInsightsAsync(string? schemaName, string? objectType, int take, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new DbOperationResult(success: false, error: "AI Insights layer is disabled."));
    }

    public Task<DbOperationResult> GetHistoryAsync(string? schemaName, string? objectName, int take, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new DbOperationResult(success: false, error: "AI Insights layer is disabled."));
    }

    public Task<DbOperationResult> RefreshInsightsAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new DbOperationResult(success: false, error: "AI Insights layer is disabled."));
    }
}
