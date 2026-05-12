// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.

using System.ComponentModel;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace Mssql.McpServer;

public partial class Tools
{
    [McpServerTool(
        Title = "Insert Data",
        ReadOnly = false,
        Destructive = false),
        Description("Inserts rows from a single INSERT T-SQL statement. Returns rowsAffected. Use parameter-less, fully literal SQL; multi-batch scripts are not supported. Unless the AI Insights layer is disabled (USE_INSIGHTS_LAYER=false/0/off), a background DDL/fingerprint reconciliation is queued after success.")]
    public async Task<DbOperationResult> InsertData(
        [Description("A complete INSERT T-SQL statement (INSERT ... VALUES / INSERT ... SELECT).")] string sql)
    {
        var conn = await _connectionFactory.GetOpenConnectionAsync();
        try
        {
            using (conn)
            {
                using var cmd = new Microsoft.Data.SqlClient.SqlCommand(sql, conn);
                var rows = await cmd.ExecuteNonQueryAsync();
                QueueInsightDdlProcessing();
                return new DbOperationResult(success: true, rowsAffected: rows);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "InsertData failed: {Message}", ex.Message);
            return new DbOperationResult(success: false, error: ex.Message);
        }
    }
}
