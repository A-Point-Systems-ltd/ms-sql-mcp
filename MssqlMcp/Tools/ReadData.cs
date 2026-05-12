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
        Description("Executes a SELECT query against the configured database and returns rows as an array of objects (column name -> value, with NULL as JSON null). Intended for read-only queries; use ExecuteSQL for DDL/DML. SQL parameters are not supported here - build the literal yourself and never interpolate untrusted input. For schema introspection prefer DescribeTable/DescribeView/GetStoredProc/GetFunction/GetTrigger over hand-written sys.* queries.")]
    public async Task<DbOperationResult> ReadData(
        [Description("A single T-SQL SELECT statement. Multi-batch scripts and DDL/DML are not allowed here; use ExecuteSQL for those.")] string sql)
    {
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
