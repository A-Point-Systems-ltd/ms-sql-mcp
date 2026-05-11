// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.

using System.ComponentModel;
using ModelContextProtocol.Server;

namespace Mssql.McpServer;

public partial class Tools
{
    [McpServerTool(
        Title = "Install Insights Layer",
        ReadOnly = false,
        Idempotent = true,
        Destructive = false),
        Description("Installs AIInsights schema, core tables, DDL_AuditLog table, and DDL_Audit database trigger (idempotent). Requires USE_INSIGHTS_LAYER and sufficient database permissions.")]
    public async Task<DbOperationResult> InstallInsightsLayer()
    {
        return await _insightsLayer.InstallLayerAsync().ConfigureAwait(false);
    }
}
