// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.

using System.ComponentModel;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace Mssql.McpServer;

public partial class Tools
{
    [McpServerTool(
        Name = ToolNames.ListInsights,
        Title = "List Insights",
        ReadOnly = true,
        Idempotent = true,
        Destructive = false),
        Description("Lists rows from AIInsights.SchemaInsights, newest first by LastAnalyzed. Returns summary fields only (InsightID, ObjectType, SchemaName, ObjectName, ColumnName, Description, BusinessPurpose, Confidence, LastAnalyzed, Version). Use " + ToolNames.GetInsight + " for the full record of a specific object. Optional string filters appear as 'required' in the MCP schema but accept JSON null to mean 'no filter'." + MultiConnectionNote)]
    public async Task<DbOperationResult> ListInsights(
        [Description("Schema filter. Pass 'dbo' to limit to dbo; pass null for no filter.")] string? schemaName = null,
        [Description("Object type filter ('Table' | 'View' | 'Procedure' | 'Function' | 'Trigger'). Pass null for no filter.")] string? objectType = null,
        [Description("Maximum rows to return. Clamped server-side to 1..2000. Use small values (e.g. 50) for triage.")] int take = 100,
        [Description(ToonParamDescription)] bool? toon = null,
        [Description(ConnectionParamDescription)] string? connection = null,
        CancellationToken cancellationToken = default)
    {
        return await _insightsLayer.ListInsightsAsync(schemaName, objectType, take, cancellationToken).ConfigureAwait(false);
    }
}
