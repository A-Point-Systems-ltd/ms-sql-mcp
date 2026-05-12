// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.

using System.ComponentModel;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace Mssql.McpServer;

public partial class Tools
{
    [McpServerTool(
        Title = "Get Insight History",
        ReadOnly = true,
        Idempotent = true,
        Destructive = false),
        Description("Returns archived AI insights from AIInsights.InsightHistory (rows soft-deleted from SchemaInsights when an object was dropped or its fingerprint changed). Use to audit what was lost/changed during DDL evolution. Newest archived rows first. Optional filters appear as 'required' in the MCP schema but accept JSON null to mean 'no filter'.")]
    public async Task<DbOperationResult> GetInsightHistory(
        [Description("Schema filter. Pass null for no filter.")] string? schemaName = null,
        [Description("Object name filter (exact match, no wildcards). Pass null for no filter.")] string? objectName = null,
        [Description("Maximum rows to return. Clamped server-side to 1..2000.")] int take = 100)
    {
        return await _insightsLayer.GetHistoryAsync(schemaName, objectName, take).ConfigureAwait(false);
    }
}
