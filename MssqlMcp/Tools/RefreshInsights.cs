// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.

using System.ComponentModel;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace Mssql.McpServer;

public partial class Tools
{
    [McpServerTool(
        Name = ToolNames.RefreshInsights,
        Title = "Refresh Insights",
        ReadOnly = false,
        Idempotent = false,
        Destructive = false),
        Description("Reconciles the AI Insights layer with the live schema. Reads new dbo.DDL_AuditLog rows since the stored watermark, archives affected insights, and falls back to a fingerprint scan when no audit rows are pending. Returns recent insight summaries. For compatibility, response still includes topQueryPatterns as an empty list. Call once per investigation session, or after known DDL bursts. Not idempotent - advances the watermark and may archive rows." + MultiConnectionNote)]
    public async Task<DbOperationResult> RefreshInsights(
        [Description(ConnectionParamDescription)] string? connection = null,
        CancellationToken cancellationToken = default)
    {
        return await _insightsLayer.RefreshInsightsAsync(cancellationToken).ConfigureAwait(false);
    }
}
