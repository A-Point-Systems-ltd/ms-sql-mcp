// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.

using System.ComponentModel;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace Mssql.McpServer;

public partial class Tools
{
    private const string ListTableTriggersQuery = @"SELECT
        s.name AS [schema],
        OBJECT_NAME(tr.parent_id) AS table_name,
        tr.name,
        tr.object_id AS id,
        tr.create_date,
        tr.modify_date,
        tr.is_disabled,
        tr.is_instead_of_trigger,
        ep.value AS description,
        STUFF((
            SELECT ', ' + te2.type_desc
            FROM sys.trigger_events te2
            WHERE tr.object_id = te2.object_id
            FOR XML PATH(''), TYPE
        ).value('.', 'NVARCHAR(MAX)'), 1, 2, '') AS trigger_events
    FROM sys.triggers tr
    INNER JOIN sys.objects o ON tr.parent_id = o.object_id
    INNER JOIN sys.schemas s ON o.schema_id = s.schema_id
    LEFT JOIN sys.extended_properties ep
        ON ep.major_id = tr.object_id
        AND ep.minor_id = 0
        AND ep.name = 'MS_Description'
    WHERE tr.parent_class = 1
    ORDER BY s.name, OBJECT_NAME(tr.parent_id), tr.name";

    [McpServerTool(
        Title = "List Table Triggers",
        ReadOnly = true,
        Idempotent = true,
        Destructive = false),
        Description("Lists all table triggers in the database with schema, parent table, name, create/modify dates, is_disabled, is_instead_of_trigger, the comma-joined event types (INSERT/UPDATE/DELETE), and description. Sorted by schema, table, trigger. Use GetTrigger for the full T-SQL of a specific trigger. Database-level / server-level triggers are NOT included.")]
    public async Task<DbOperationResult> ListTableTriggers()
    {
        var conn = await _connectionFactory.GetOpenConnectionAsync();
        try
        {
            using (conn)
            {
                using var cmd = new SqlCommand(ListTableTriggersQuery, conn);
                var triggers = new List<Dictionary<string, object?>>();
                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    triggers.Add(new Dictionary<string, object?>
                    {
                        ["schema"] = reader["schema"],
                        ["table_name"] = reader["table_name"],
                        ["name"] = reader["name"],
                        ["id"] = reader["id"],
                        ["create_date"] = reader["create_date"],
                        ["modify_date"] = reader["modify_date"],
                        ["is_disabled"] = reader["is_disabled"],
                        ["is_instead_of_trigger"] = reader["is_instead_of_trigger"],
                        ["description"] = reader["description"] is DBNull ? null : reader["description"],
                        ["trigger_events"] = reader["trigger_events"] is DBNull ? null : reader["trigger_events"]
                    });
                }
                return new DbOperationResult(success: true, data: triggers);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ListTableTriggers failed: {Message}", ex.Message);
            return new DbOperationResult(success: false, error: ex.Message);
        }
    }
}