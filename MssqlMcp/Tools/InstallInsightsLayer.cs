// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.

using System.ComponentModel;
using ModelContextProtocol.Server;

namespace Mssql.McpServer;

public partial class Tools
{
    [McpServerTool(
        Name = ToolNames.InstallInsightsLayer,
        Title = "Install Insights Layer",
        ReadOnly = false,
        Idempotent = true,
        Destructive = true),
        Description("Idempotently installs the active AIInsights layer: AIInsights schema, SchemaInsights, InsightHistory, DdlChangeWatermark, dbo.DDL_AuditLog table, and a DATABASE-WIDE DDL trigger (DDL_Audit) that logs every DDL statement by every application in this database. DESTRUCTIVE: confirm with the user before running on a shared or production database. The trigger runs as the caller, so a login that can run DDL but lacks INSERT on dbo.DDL_AuditLog will have its DDL rolled back - grant INSERT on dbo.DDL_AuditLog to such logins. An existing DDL_Audit trigger is never replaced. Legacy AIInsights tables from older schema versions are dropped only when empty. Safe to re-run. The layer is enabled by default; only disabled when USE_INSIGHTS_LAYER is set to false/0/off/disabled. Requires DDL trigger permission (ALTER ANY DATABASE DDL TRIGGER, or ddl_admin / sysadmin). Run once per database before using other insight tools. Returns success plus an installed=true marker.")]
    public async Task<DbOperationResult> InstallInsightsLayer(CancellationToken cancellationToken = default)
    {
        return await _insightsLayer.InstallLayerAsync(cancellationToken).ConfigureAwait(false);
    }
}
