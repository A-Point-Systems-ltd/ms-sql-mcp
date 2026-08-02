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
        Title = "Read Data",
        ReadOnly = true,
        Idempotent = true,
        Destructive = false),
        Description("Executes read-only SELECT queries and returns rows as column-name objects. Use this tool for ALL queries that return result sets — user tables, sys.*, INFORMATION_SCHEMA.*, DMVs, and WITH ... SELECT. ExecuteSQL rejects SELECT; do not use ExecuteSQL for reads. SQL parameters are not supported — build literals yourself and never interpolate untrusted input. For built-in schema detail prefer DescribeTable/DescribeView/GetObject when applicable.")]
    public async Task<DbOperationResult> ReadData(
        [Description("A single read-only T-SQL SELECT (or WITH ... SELECT). Includes queries against sys.* and INFORMATION_SCHEMA. DDL/DML and SELECT ... INTO are not allowed — use ExecuteSQL for those.")] string sql)
    {
        if (!SqlStatementClassifier.TryValidateReadOnly(sql, out var validationError))
        {
            return new DbOperationResult(success: false, error: validationError);
        }

        var conn = await _connectionFactory.GetOpenConnectionAsync();
        try
        {
            using (conn)
            {
                using var cmd = new SqlCommand(sql, conn);
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
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ReadData failed: {Message}", ex.Message);
            return new DbOperationResult(success: false, error: ex.Message);
        }
    }
}
