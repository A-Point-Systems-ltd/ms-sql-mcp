using Mssql.McpServer.InsightsLayer.Models;

namespace Mssql.McpServer.InsightsLayer;

/// <summary>
/// Used when <c>USE_INSIGHTS_LAYER</c> is not enabled; keeps DI simple without null checks in tools.
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
            error: "AI Insights layer is disabled. Set environment variable USE_INSIGHTS_LAYER=true (or 1) on the MCP server, then restart."));
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
