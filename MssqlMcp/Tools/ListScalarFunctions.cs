// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.

using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace Mssql.McpServer;

public partial class Tools
{
    private const string ListScalarFunctionsQuery = @"
        SELECT 
            s.name AS [schema],
            o.name,
            o.object_id AS id,
            o.create_date,
            o.modify_date,
            ep.value AS description
        FROM sys.objects o
        INNER JOIN sys.schemas s ON o.schema_id = s.schema_id
        LEFT JOIN sys.extended_properties ep 
            ON ep.major_id = o.object_id 
            AND ep.minor_id = 0 
            AND ep.name = 'MS_Description'
        WHERE o.type = 'FN'
            AND (@NamePattern IS NULL
                OR o.name LIKE @NamePattern
                OR (s.name + '.' + o.name) LIKE @NamePattern)
        ORDER BY s.name, o.name";

    private async Task<DbOperationResult> ListScalarFunctions(string? partialName = null, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var conn = await _connectionFactory.GetOpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            {
                await using var cmd = new SqlCommand(ListScalarFunctionsQuery, conn);
                AddNamePatternParameter(cmd, partialName);
                var functions = new List<Dictionary<string, object?>>();
                await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    functions.Add(new Dictionary<string, object?>
                    {
                        ["schema"] = reader["schema"],
                        ["name"] = reader["name"],
                        ["id"] = reader["id"],
                        ["create_date"] = reader["create_date"],
                        ["modify_date"] = reader["modify_date"],
                        ["description"] = reader["description"] is DBNull ? null : reader["description"]
                    });
                }
                return new DbOperationResult(success: true, data: functions);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "{Tool} (ListScalarFunctions) failed: {Message}", ToolNames.ListObjects, ex.Message);
            return new DbOperationResult(success: false, error: ex.Message);
        }
    }
}
