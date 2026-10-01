// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.

using System.ComponentModel;
using ModelContextProtocol.Server;

namespace Mssql.McpServer;

public partial class Tools
{
    [McpServerTool(
        Name = ToolNames.ExecuteSql,
        Title = "Execute SQL",
        ReadOnly = false,
        Idempotent = false,
        Destructive = true),
        Description("Executes a single DDL/DML statement (INSERT, UPDATE, DELETE, MERGE, CREATE, ALTER, DROP, TRUNCATE, EXEC, GRANT/REVOKE/DENY, BACKUP/RESTORE, SELECT ... INTO). SELECT and other read-only queries are rejected - use " + ToolNames.ReadData + " for every SELECT, including sys.* and INFORMATION_SCHEMA. Marked DESTRUCTIVE: confirm intent before running. Unless the AI Insights layer is disabled (USE_INSIGHTS_LAYER=false/0/off), a background DDL/fingerprint reconciliation is queued after success. Prefer " + ToolNames.CreateTable + "/" + ToolNames.DropTable + "/" + ToolNames.InsertData + "/" + ToolNames.UpdateData + " for typed operations when possible." + MultiConnectionNote)]
    public Task<DbOperationResult> ExecuteSQL(
        [Description("A single non-SELECT T-SQL statement (DDL or DML). A CREATE PROCEDURE/FUNCTION/TRIGGER body counts as one statement. SELECT/WITH-read queries are rejected - use " + ToolNames.ReadData + " instead. Multi-statement scripts and 'GO' batches are not supported.")] string sql,
        [Description(ConnectionParamDescription)] string? connection = null,
        CancellationToken cancellationToken = default) =>
        ExecuteWriteAsync(sql, SqlStatementKind.Any, ToolNames.ExecuteSql, includeRowsAffected: true, cancellationToken);
}
