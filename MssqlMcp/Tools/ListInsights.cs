// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.

using System.ComponentModel;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace Mssql.McpServer;

public partial class Tools
{
    [McpServerTool(
        Title = "List Insights",
        ReadOnly = true,
        Idempotent = true,
        Destructive = false),
        Description("Lists recent rows from AIInsights.SchemaInsights with optional filters.")]
    public async Task<DbOperationResult> ListInsights(
        [Description("Optional schema filter")] string? schemaName = null,
        [Description("Optional object type filter (e.g. Table)")] string? objectType = null,
        [Description("Max rows (1..2000)")] int take = 100)
    {
        return await _insightsLayer.ListInsightsAsync(schemaName, objectType, take).ConfigureAwait(false);
    }
}
