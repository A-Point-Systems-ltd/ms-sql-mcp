// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.

using System.ComponentModel;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

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
            AND o.is_ms_shipped = 0  -- Exclude system objects
        ORDER BY s.name, o.name";

    [McpServerTool(
        Title = "List System Objects",
        ReadOnly = true,
        Idempotent = true,
        Destructive = false),
        Description("Returns user-defined entries from sys.objects (is_ms_shipped = 0) with object_id, schema, name, type, type_desc, create/modify dates, is_ms_shipped, description. Use for cross-cutting object discovery when the typed list tools (ListTables/Views/StoredProcedures/Functions/Triggers) are too narrow. Optional 'type' filter accepts JSON null for no filter.")]
    public async Task<DbOperationResult> ListSysObjects(
        [Description("Object type code: 'U'=user table, 'V'=view, 'P'=stored proc, 'FN'=scalar fn, 'IF'=inline TVF, 'TF'=multi-statement TVF, 'TR'=trigger, 'SO'=sequence, etc. Pass null for no filter.")] string? type = null)
    {
        var conn = await _connectionFactory.GetOpenConnectionAsync();
        try
        {
            using (conn)
            {
                using var cmd = new SqlCommand(ListSysObjectsQuery, conn);
                cmd.Parameters.AddWithValue("@Type", type == null ? DBNull.Value : type);
                
                var objects = new List<Dictionary<string, object?>>();
                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
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
        catch (Exception ex)
        {
            _logger.LogError(ex, "ListSysObjects failed: {Message}", ex.Message);
            return new DbOperationResult(success: false, error: ex.Message);
        }
    }
}