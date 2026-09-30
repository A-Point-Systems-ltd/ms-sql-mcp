// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.

using System.ComponentModel;
using ModelContextProtocol.Server;

namespace Mssql.McpServer;

public partial class Tools
{
    [McpServerTool(
        Name = ToolNames.UpdateData,
        Title = "Update Data",
        ReadOnly = false,
        Destructive = true),
        Description("Updates rows from a single UPDATE T-SQL statement; any other statement type is rejected. DESTRUCTIVE - always include a WHERE clause. Returns rowsAffected. Unless the AI Insights layer is disabled (USE_INSIGHTS_LAYER=false/0/off), a background DDL/fingerprint reconciliation is queued after success.")]
    public Task<DbOperationResult> UpdateData(
        [Description("A complete UPDATE T-SQL statement. WHERE clause strongly recommended to avoid full-table updates.")] string sql,
        CancellationToken cancellationToken = default) =>
        ExecuteWriteAsync(sql, SqlStatementKind.Update, ToolNames.UpdateData, includeRowsAffected: true, cancellationToken);
}
