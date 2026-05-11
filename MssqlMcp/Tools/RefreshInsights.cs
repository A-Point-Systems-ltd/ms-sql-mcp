// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.

using System.ComponentModel;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace Mssql.McpServer;

public partial class Tools
{
    [McpServerTool(
        Title = "Refresh Insights",
        ReadOnly = false,
        Idempotent = false,
        Destructive = false),
        Description("Processes DDL audit backlog / fingerprint drift, then returns recent insight summaries and top query patterns (computed in-process, no SQL views).")]
    public async Task<DbOperationResult> RefreshInsights()
    {
        return await _insightsLayer.RefreshInsightsAsync().ConfigureAwait(false);
    }
}
