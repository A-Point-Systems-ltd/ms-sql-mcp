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
        Description("Execute custom SQL commands (DDL, DML, queries)")]
    public async Task<DbOperationResult> ExecuteSQL(
        [Description("SQL command to execute")] string sql)
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