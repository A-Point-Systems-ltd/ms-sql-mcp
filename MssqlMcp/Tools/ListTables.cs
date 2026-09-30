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

    private async Task<DbOperationResult> ListTables(string? partialName = null, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var conn = await _connectionFactory.GetOpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            {
                await using var cmd = new SqlCommand(ListTablesQuery, conn);
                AddNamePatternParameter(cmd, partialName);
                var tables = new List<string>();
                await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    tables.Add($"{reader.GetString(0)}.{reader.GetString(1)}");
                }
                return new DbOperationResult(success: true, data: tables);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "{Tool} (ListTables) failed: {Message}", ToolNames.ListObjects, ex.Message);
            return new DbOperationResult(success: false, error: ex.Message);
        }
    }
}
