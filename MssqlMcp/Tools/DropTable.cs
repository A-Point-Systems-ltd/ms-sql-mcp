// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.

using System.ComponentModel;
using ModelContextProtocol.Server;

namespace Mssql.McpServer;

public partial class Tools
{
    [McpServerTool(
        Name = ToolNames.DropTable,
        Title = "Drop Table",
        ReadOnly = false,
        Destructive = true),
        Description("Drops a table. DESTRUCTIVE - irreversible. Accepts a single DROP TABLE statement only; prefer `IF EXISTS` guards. Unless the AI Insights layer is disabled (USE_INSIGHTS_LAYER=false/0/off), related insights are auto-archived to AIInsights.InsightHistory by the background reconciliation. Confirm with the user before calling." + MultiConnectionNote)]
    public Task<DbOperationResult> DropTable(
        [Description("A complete DROP TABLE T-SQL statement (schema-qualified, optionally with `IF EXISTS`).")] string sql,
        [Description(ConnectionParamDescription)] string? connection = null,
        CancellationToken cancellationToken = default) =>
        ExecuteWriteAsync(sql, SqlStatementKind.DropTable, ToolNames.DropTable, includeRowsAffected: false, cancellationToken);
}
