// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.

using System.ComponentModel;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace Mssql.McpServer;

public partial class Tools
{
    [McpServerTool(
        Name = ToolNames.InsightsCheck,
        Title = "Insights Check",
        ReadOnly = true,
        Idempotent = true,
        Destructive = false),
        Description("Reports the AI Insights layer state for the current database. Returns: layerEnabledViaEnvironment, aiInsightsSchemaExists, ddlAuditTableExists, ddlAuditTriggerEnabled, schemaInsightsCount, lastProcessedAuditId, lastProcessedAt. Call BEFORE other AIInsights tools to decide whether to run " + ToolNames.InstallInsightsLayer + ". The layer is enabled by default; it is only off when USE_INSIGHTS_LAYER is set to false/0/off/disabled.")]
    public async Task<DbOperationResult> InsightsCheck(CancellationToken cancellationToken = default)
    {
        try
        {
            var status = await _insightsLayer.GetStatusAsync(cancellationToken).ConfigureAwait(false);
            var data = new Dictionary<string, object?>
            {
                ["layerEnabledViaEnvironment"] = status.LayerEnabledViaEnvironment,
                ["aiInsightsSchemaExists"] = status.AiInsightsSchemaExists,
                ["ddlAuditTableExists"] = status.DdlAuditTableExists,
                ["ddlAuditTriggerEnabled"] = status.DdlAuditTriggerEnabled,
                ["schemaInsightsCount"] = status.SchemaInsightsCount,
                ["lastProcessedAuditId"] = status.LastProcessedAuditId,
                ["lastProcessedAt"] = status.LastProcessedAt
            };
            return new DbOperationResult(success: true, data: data);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "InsightsCheck failed: {Message}", ex.Message);
            return new DbOperationResult(success: false, error: ex.Message);
        }
    }
}
