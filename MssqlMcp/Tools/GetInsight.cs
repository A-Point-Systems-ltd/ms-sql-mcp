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
        Description("Returns the cached AI insight for a single database object plus a freshness state. Response shape: { insight: {...} | null, insightFreshness: 'LayerDisabled' | 'Absent' | 'Fresh' | 'StaleArchived' | 'AccessDenied' | 'DefinitionUnavailable' }. If the live schema fingerprint has changed (object dropped or modified), the stored row is auto-archived to AIInsights.InsightHistory and freshness becomes 'StaleArchived'. After 'StaleArchived' or 'Absent', re-investigate the object and call UpsertInsight.")]
    public async Task<DbOperationResult> GetInsight(
        [Description("Object name without schema (e.g. 'TableProblems'). Case follows SQL Server collation.")] string objectName,
        [Description("Schema name. Pass 'dbo' explicitly when the object lives in dbo; pass null only when schema is unknown.")] string? schemaName = null,
        [Description("Object type label as stored in AIInsights: 'Table' | 'View' | 'Procedure' | 'Function' | 'Trigger'. Defaults to 'Table'.")] string objectType = "Table")
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
