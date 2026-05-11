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
        Description("Returns archived AI insights from AIInsights.InsightHistory.")]
    public async Task<DbOperationResult> GetInsightHistory(
        [Description("Optional schema filter")] string? schemaName = null,
        [Description("Optional object name filter")] string? objectName = null,
        [Description("Max rows (1..2000)")] int take = 100)
    {
        return await _insightsLayer.GetHistoryAsync(schemaName, objectName, take).ConfigureAwait(false);
    }
}
