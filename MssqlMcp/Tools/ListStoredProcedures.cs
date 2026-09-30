// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.

using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace Mssql.McpServer;

public partial class Tools
{
    private const string ListStoredProceduresQuery = @"
        SELECT 
            s.name AS [schema],
            p.name AS name,
            p.object_id AS id,
            p.create_date,
            p.modify_date,
            ep.value AS description
        FROM sys.procedures p
        INNER JOIN sys.schemas s ON p.schema_id = s.schema_id
        LEFT JOIN sys.extended_properties ep 
            ON ep.major_id = p.object_id 
            AND ep.minor_id = 0 
            AND ep.name = 'MS_Description'
        WHERE (@NamePattern IS NULL
            OR p.name LIKE @NamePattern
            OR (s.name + '.' + p.name) LIKE @NamePattern)
        ORDER BY s.name, p.name";

    private async Task<DbOperationResult> ListStoredProcedures(string? partialName = null, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var conn = await _connectionFactory.GetOpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            {
                await using var cmd = new SqlCommand(ListStoredProceduresQuery, conn);
                AddNamePatternParameter(cmd, partialName);
                var procedures = new List<Dictionary<string, object?>>();
                await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    procedures.Add(new Dictionary<string, object?>
                    {
                        ["schema"] = reader["schema"],
                        ["name"] = reader["name"],
                        ["id"] = reader["id"],
                        ["create_date"] = reader["create_date"],
                        ["modify_date"] = reader["modify_date"],
                        ["description"] = reader["description"] is DBNull ? null : reader["description"]
                    });
                }
                return new DbOperationResult(success: true, data: procedures);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "{Tool} (ListStoredProcedures) failed: {Message}", ToolNames.ListObjects, ex.Message);
            return new DbOperationResult(success: false, error: ex.Message);
        }
    }
}
