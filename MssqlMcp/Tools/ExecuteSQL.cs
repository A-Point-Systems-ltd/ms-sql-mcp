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
        Description("Executes an arbitrary T-SQL command. Marked DESTRUCTIVE: may modify schema or data. If the statement starts with SELECT, returns rows like ReadData; otherwise executes as a non-query and returns rowsAffected. Unless the AI Insights layer is disabled (USE_INSIGHTS_LAYER=false/0/off), non-SELECT statements queue a background DDL/fingerprint reconciliation. Prefer ReadData for SELECTs and CreateTable/DropTable/InsertData/UpdateData for typed operations; reserve ExecuteSQL for ALTER, MERGE, multi-statement batches, etc. Confirm intent with the user before running destructive commands.")]
    public async Task<DbOperationResult> ExecuteSQL(
        [Description("A single T-SQL statement (DDL, DML, or SELECT). Multi-batch scripts separated by 'GO' are not supported.")] string sql)
    {
        var conn = await _connectionFactory.GetOpenConnectionAsync();
        try
        {
            using (conn)
            {
                using var cmd = new SqlCommand(sql, conn);
                
                // Detect if it's a SELECT query
                bool isSelect = sql.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase);

                if (isSelect)
                {
                    // Use ExecuteReaderAsync - similar to ReadData
                    using var reader = await cmd.ExecuteReaderAsync();
                    var results = new List<Dictionary<string, object?>>();
                    while (await reader.ReadAsync())
                    {
                        var row = new Dictionary<string, object?>();
                        for (var i = 0; i < reader.FieldCount; i++)
                        {
                            row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                        }
                        results.Add(row);
                    }
                    return new DbOperationResult(success: true, data: results);
                }
                else
                {
                    // Use ExecuteNonQueryAsync for INSERT, UPDATE, DELETE, DDL
                    int rowsAffected = await cmd.ExecuteNonQueryAsync();
                    QueueInsightDdlProcessing();
                    return new DbOperationResult(success: true, rowsAffected: rowsAffected);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ExecuteSQL failed: {Message}", ex.Message);
            return new DbOperationResult(success: false, error: ex.Message);
        }
    }
}