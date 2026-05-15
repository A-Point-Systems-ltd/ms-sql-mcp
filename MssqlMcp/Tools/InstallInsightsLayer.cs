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
        Description("Idempotently installs the active AIInsights layer: AIInsights schema, SchemaInsights, InsightHistory, DdlChangeWatermark, dbo.DDL_AuditLog table, and the database-level DDL_Audit trigger. Safe to re-run. Also removes legacy/non-active AIInsights tables from older schema versions. The layer is enabled by default; only disabled when USE_INSIGHTS_LAYER is set to false/0/off/disabled. Requires DDL trigger permission (ALTER ANY DATABASE DDL TRIGGER, or ddl_admin / sysadmin). Run once per database before using other insight tools. Returns success plus an installed=true marker.")]
    public async Task<DbOperationResult> InstallInsightsLayer()
    {
        return await _insightsLayer.InstallLayerAsync().ConfigureAwait(false);
    }
}
