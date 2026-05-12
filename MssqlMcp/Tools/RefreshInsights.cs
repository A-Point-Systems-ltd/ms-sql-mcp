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
        Description("Reconciles the AI Insights layer with the live schema. Reads new dbo.DDL_AuditLog rows since the stored watermark, archives affected insights, and falls back to a fingerprint scan when no audit rows are pending. Returns recent insight summaries and top query patterns (both computed in-process; no SQL views are used). Call once per investigation session, or after known DDL bursts. Not idempotent — advances the watermark and may archive rows.")]
    public async Task<DbOperationResult> RefreshInsights()
    {
        return await _insightsLayer.RefreshInsightsAsync().ConfigureAwait(false);
    }
}
