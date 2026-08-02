// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.

using System.ComponentModel;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace Mssql.McpServer;

public partial class Tools
{
    [McpServerTool(
        Title = "Execute SQL",
        ReadOnly = false,
        Idempotent = false,
        Destructive = true),
        Description("Executes DDL/DML only (INSERT, UPDATE, DELETE, MERGE, CREATE, ALTER, DROP, TRUNCATE, EXEC, etc.). SELECT and other read-only queries are rejected — use ReadData for every SELECT, including sys.* and INFORMATION_SCHEMA. Marked DESTRUCTIVE: confirm intent before running. Unless the AI Insights layer is disabled (USE_INSIGHTS_LAYER=false/0/off), successful execution queues background DDL/fingerprint reconciliation. Prefer CreateTable/DropTable/InsertData/UpdateData for typed operations when possible.")]
    public async Task<DbOperationResult> ExecuteSQL(
        [Description("A single non-SELECT T-SQL statement (DDL or DML). SELECT/WITH-read queries are rejected — use ReadData instead. Multi-batch scripts separated by 'GO' are not supported.")] string sql)
    {
        if (!SqlStatementClassifier.TryValidateExecutable(sql, out var validationError))
        {
            return new DbOperationResult(success: false, error: validationError);
        }

        var conn = await _connectionFactory.GetOpenConnectionAsync();
        try
        {
            using (conn)
            {
                using var cmd = new SqlCommand(sql, conn);
                int rowsAffected = await cmd.ExecuteNonQueryAsync();
                QueueInsightDdlProcessing();
                return new DbOperationResult(success: true, rowsAffected: rowsAffected);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ExecuteSQL failed: {Message}", ex.Message);
            return new DbOperationResult(success: false, error: ex.Message);
        }
    }
}
