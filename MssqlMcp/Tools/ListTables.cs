// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.

using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace Mssql.McpServer;

public partial class Tools
{
    private const string ListTablesQuery = @"
        SELECT TABLE_SCHEMA, TABLE_NAME
        FROM INFORMATION_SCHEMA.TABLES
        WHERE TABLE_TYPE = 'BASE TABLE'
            AND (@NamePattern IS NULL
                OR TABLE_NAME LIKE @NamePattern
                OR (TABLE_SCHEMA + '.' + TABLE_NAME) LIKE @NamePattern)
        ORDER BY TABLE_SCHEMA, TABLE_NAME";

    private async Task<DbOperationResult> ListTables(string? partialName = null)
    {
        var conn = await _connectionFactory.GetOpenConnectionAsync();
        try
        {
            using (conn)
            {
                using var cmd = new SqlCommand(ListTablesQuery, conn);
                AddNamePatternParameter(cmd, partialName);
                var tables = new List<string>();
                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    tables.Add($"{reader.GetString(0)}.{reader.GetString(1)}");
                }
                return new DbOperationResult(success: true, data: tables);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ListTables failed: {Message}", ex.Message);
            return new DbOperationResult(success: false, error: ex.Message);
        }
    }
}
