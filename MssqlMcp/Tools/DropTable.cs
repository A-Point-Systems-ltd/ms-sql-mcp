// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.

using System.ComponentModel;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace Mssql.McpServer;

public partial class Tools
{
    [McpServerTool(
        Title = "Drop Table",
        ReadOnly = false,
        Destructive = true),
        Description("Drops a table. DESTRUCTIVE - irreversible. Accepts a DROP TABLE statement; prefer `IF EXISTS` guards. Unless the AI Insights layer is disabled (USE_INSIGHTS_LAYER=false/0/off), related insights are auto-archived to AIInsights.InsightHistory by the background reconciliation. Confirm with the user before calling.")]
    public async Task<DbOperationResult> DropTable(
        [Description("A complete DROP TABLE T-SQL statement (schema-qualified, optionally with `IF EXISTS`).")] string sql)
    {
        var conn = await _connectionFactory.GetOpenConnectionAsync();
        try
        {
            using (conn)
            {
                using var cmd = new Microsoft.Data.SqlClient.SqlCommand(sql, conn);
                _ = await cmd.ExecuteNonQueryAsync();
                QueueInsightDdlProcessing();
                return new DbOperationResult(success: true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DropTable failed: {Message}", ex.Message);
            return new DbOperationResult(success: false, error: ex.Message);
        }
    }
}
