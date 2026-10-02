// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.

using System.ComponentModel;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace Mssql.McpServer;

public partial class Tools
{
    [McpServerTool(
        Name = ToolNames.GetInsight,
        Title = "Get Insight",
        ReadOnly = true,
        Idempotent = true,
        Destructive = false),
        Description("Returns the cached AI insight for a single database object plus a freshness state. Response shape: { insight: {...} | null, insightFreshness: 'LayerDisabled' | 'Absent' | 'Fresh' | 'StaleArchived' | 'AccessDenied' | 'DefinitionUnavailable' }. If the object was dropped or its structure (columns or definition) changed, the stored row is archived to AIInsights.InsightHistory and freshness becomes 'StaleArchived' (on a read-only connection nothing is written: the row is reported 'StaleArchived' but left in place). Prefer " + ToolNames.DescribeTable + "/" + ToolNames.DescribeView + "/" + ToolNames.GetObject + ", which restore or rebuild the insight and say whether an update is needed." + MultiConnectionNote)]
    public async Task<DbOperationResult> GetInsight(
        [Description("Object name without schema (e.g. 'TableProblems'). Case follows SQL Server collation.")] string objectName,
        [Description("Schema name. Pass 'dbo' explicitly when the object lives in dbo; pass null only when schema is unknown.")] string? schemaName = null,
        [Description("Object type label as stored in AIInsights: 'Table' | 'View' | 'Procedure' | 'Function' | 'Trigger'. Defaults to 'Table'.")] string objectType = "Table",
        [Description(ConnectionParamDescription)] string? connection = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var (insight, freshness) = await _insightsLayer
                .GetInsightForObjectAsync(objectType, schemaName, objectName, cancellationToken)
                .ConfigureAwait(false);
            return new DbOperationResult(
                success: true,
                data: new
                {
                    insight = ProjectInsightForResponse(insight),
                    insightFreshness = freshness.ToString()
                });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "{Tool} failed: {Message}", ToolNames.GetInsight, ex.Message);
            return new DbOperationResult(success: false, error: ex.Message);
        }
    }
}
