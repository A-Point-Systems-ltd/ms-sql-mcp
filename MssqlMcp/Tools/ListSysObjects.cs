// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.

using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace Mssql.McpServer;

public partial class Tools
{
    private const string ListSysObjectsQuery = @"
        SELECT 
            o.object_id,
            s.name AS [schema],
            o.name,
            o.type,
            o.type_desc,
            o.create_date,
            o.modify_date,
            o.is_ms_shipped,
            ep.value AS description
        FROM sys.objects o
        INNER JOIN sys.schemas s ON o.schema_id = s.schema_id
        LEFT JOIN sys.extended_properties ep 
            ON ep.major_id = o.object_id 
            AND ep.minor_id = 0 
            AND ep.name = 'MS_Description'
        WHERE (@Type IS NULL OR o.type = @Type)
            AND o.is_ms_shipped = 0
            AND (@NamePattern IS NULL
                OR o.name LIKE @NamePattern
                OR (s.name + '.' + o.name) LIKE @NamePattern)
        ORDER BY s.name, o.name";

    private async Task<DbOperationResult> ListSysObjects(string? type = null, string? partialName = null, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var conn = await _connectionFactory.GetOpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            {
                await using var cmd = new SqlCommand(ListSysObjectsQuery, conn);
                cmd.Parameters.AddWithValue("@Type", type == null ? DBNull.Value : type);
                AddNamePatternParameter(cmd, partialName);
                
                var objects = new List<Dictionary<string, object?>>();
                await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    objects.Add(new Dictionary<string, object?>
                    {
                        ["object_id"] = reader["object_id"],
                        ["schema"] = reader["schema"],
                        ["name"] = reader["name"],
                        ["type"] = reader["type"],
                        ["type_desc"] = reader["type_desc"],
                        ["create_date"] = reader["create_date"],
                        ["modify_date"] = reader["modify_date"],
                        ["is_ms_shipped"] = reader["is_ms_shipped"],
                        ["description"] = reader["description"] is DBNull ? null : reader["description"]
                    });
                }
                return new DbOperationResult(success: true, data: objects);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "{Tool} (ListSysObjects) failed: {Message}", ToolNames.ListObjects, ex.Message);
            return new DbOperationResult(success: false, error: ex.Message);
        }
    }
}
