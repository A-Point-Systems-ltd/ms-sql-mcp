// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.

using System.ComponentModel;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace Mssql.McpServer;

public partial class Tools
{
    [McpServerTool(
        Title = "Get Insight",
        ReadOnly = true,
        Idempotent = true,
        Destructive = false),
        Description("Returns cached AI insight for a database object (if any). Stale insights are archived automatically and return absent.")]
    public async Task<DbOperationResult> GetInsight(
        [Description("Object name (e.g. table or procedure name)")] string objectName,
        [Description("Schema name; defaults to dbo when omitted")] string? schemaName = null,
        [Description("Object type label stored in AIInsights, e.g. Table, View, Procedure, Function, Trigger")] string objectType = "Table")
    {
        try
        {
            var (insight, freshness) = await _insightsLayer
                .GetInsightForObjectAsync(objectType, schemaName, objectName)
                .ConfigureAwait(false);
            return new DbOperationResult(
                success: true,
                data: new
                {
                    insight = ProjectInsightForResponse(insight),
                    insightFreshness = freshness.ToString()
                });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetInsight failed: {Message}", ex.Message);
            return new DbOperationResult(success: false, error: ex.Message);
        }
    }
}
