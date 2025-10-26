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
        Title = "Get Trigger",
        ReadOnly = true,
        Idempotent = true,
        Destructive = false),
        Description("Get trigger details and SQL code")]
    public async Task<DbOperationResult> GetTrigger(
        [Description("Name of trigger")] string name)
    {
        // Note: Triggers are typically identified by name directly, but we still parse for consistency
        string? schema = null;
        if (name.Contains('.'))
        {
            var parts = name.Split('.');
            if (parts.Length > 1)
            {
                name = parts[1];
                schema = parts[0];
            }
        }

        // Query for trigger info
        const string TriggerInfoQuery = @"SELECT 
            s.name AS [schema],
            OBJECT_NAME(tr.parent_id) AS table_name,
            tr.name,
            tr.create_date,
            tr.modify_date,
            tr.is_disabled,
            tr.is_instead_of_trigger,
            ep.value AS description,
            STRING_AGG(te.type_desc, ', ') AS trigger_events
        FROM sys.triggers tr
        INNER JOIN sys.objects o ON tr.parent_id = o.object_id
        INNER JOIN sys.schemas s ON o.schema_id = s.schema_id
        LEFT JOIN sys.extended_properties ep 
            ON ep.major_id = tr.object_id 
            AND ep.minor_id = 0 
            AND ep.name = 'MS_Description'
        LEFT JOIN sys.trigger_events te ON tr.object_id = te.object_id
        WHERE tr.name = @ObjectName 
            AND tr.parent_class = 1
            AND (s.name = @SchemaName OR @SchemaName IS NULL)
        GROUP BY s.name, tr.parent_id, tr.name, tr.create_date, 
                 tr.modify_date, tr.is_disabled, tr.is_instead_of_trigger, ep.value";

        // Query for code definition
        const string DefinitionQuery = @"SELECT OBJECT_DEFINITION(tr.object_id) AS definition
        FROM sys.triggers tr
        INNER JOIN sys.objects o ON tr.parent_id = o.object_id
        INNER JOIN sys.schemas s ON o.schema_id = s.schema_id
        WHERE tr.name = @ObjectName
            AND (s.name = @SchemaName OR @SchemaName IS NULL)";

        var conn = await _connectionFactory.GetOpenConnectionAsync();
        try
        {
            using (conn)
            {
                var result = new Dictionary<string, object>();

                // Trigger Info
                using (var cmd = new SqlCommand(TriggerInfoQuery, conn))
                {
                    cmd.Parameters.AddWithValue("@ObjectName", name);
                    cmd.Parameters.AddWithValue("@SchemaName", schema == null ? DBNull.Value : schema);
                    using var reader = await cmd.ExecuteReaderAsync();
                    if (await reader.ReadAsync())
                    {
                        result["trigger"] = new
                        {
                            schema = reader["schema"],
                            table_name = reader["table_name"],
                            name = reader["name"],
                            create_date = reader["create_date"],
                            modify_date = reader["modify_date"],
                            is_disabled = (bool)reader["is_disabled"],
                            is_instead_of_trigger = (bool)reader["is_instead_of_trigger"],
                            description = reader["description"] is DBNull ? null : reader["description"],
                            trigger_events = reader["trigger_events"] is DBNull ? null : reader["trigger_events"]
                        };
                    }
                    else
                    {
                        return new DbOperationResult(success: false, error: $"Trigger '{name}' not found.");
                    }
                }

                // Code Definition
                using (var cmd = new SqlCommand(DefinitionQuery, conn))
                {
                    cmd.Parameters.AddWithValue("@ObjectName", name);
                    cmd.Parameters.AddWithValue("@SchemaName", schema == null ? DBNull.Value : schema);
                    using var reader = await cmd.ExecuteReaderAsync();
                    if (await reader.ReadAsync())
                    {
                        result["definition"] = reader["definition"] is DBNull ? null : reader["definition"];
                    }
                }

                return new DbOperationResult(success: true, data: result);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetTrigger failed: {Message}", ex.Message);
            return new DbOperationResult(success: false, error: ex.Message);
        }
    }
}