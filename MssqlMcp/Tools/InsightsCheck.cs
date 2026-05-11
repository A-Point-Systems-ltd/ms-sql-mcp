// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.

using System.ComponentModel;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace Mssql.McpServer;

public partial class Tools
{
    [McpServerTool(
        Title = "Insights Check",
        ReadOnly = true,
        Idempotent = true,
        Destructive = false),
        Description("Returns whether the AI Insights layer and DDL audit objects are present in the database (requires USE_INSIGHTS_LAYER).")]
    public async Task<DbOperationResult> InsightsCheck()
    {
        try
        {
            var status = await _insightsLayer.GetStatusAsync().ConfigureAwait(false);
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
        catch (Exception ex)
        {
            _logger.LogError(ex, "InsightsCheck failed: {Message}", ex.Message);
            return new DbOperationResult(success: false, error: ex.Message);
        }
    }
}
