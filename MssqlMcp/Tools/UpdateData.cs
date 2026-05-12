// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.

using System.ComponentModel;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace Mssql.McpServer;

public partial class Tools
{
    [McpServerTool(
        Title = "Update Data",
        ReadOnly = false,
        Destructive = true),
        Description("Updates rows from a single UPDATE T-SQL statement. DESTRUCTIVE - always include a WHERE clause. Returns rowsAffected. Unless the AI Insights layer is disabled (USE_INSIGHTS_LAYER=false/0/off), a background reconciliation is queued after success.")]
    public async Task<DbOperationResult> UpdateData(
        [Description("A complete UPDATE T-SQL statement. WHERE clause strongly recommended to avoid full-table updates.")] string sql)
    {
        var conn = await _connectionFactory.GetOpenConnectionAsync();
        try
        {
            using (conn)
            {
                using var cmd = new Microsoft.Data.SqlClient.SqlCommand(sql, conn);
                var rows = await cmd.ExecuteNonQueryAsync();
                QueueInsightDdlProcessing();
                return new DbOperationResult(true, null, rows);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "UpdateData failed: {Message}", ex.Message);
            return new DbOperationResult(false, ex.Message);
        }
    }
}

