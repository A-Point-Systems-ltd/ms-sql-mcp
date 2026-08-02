// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.

using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace Mssql.McpServer;

public partial class Tools
{
    private async Task<DbOperationResult> GetTrigger(string name)
    {
        var parsed = TriggerQualifiedName.Parse(name);
        if (string.IsNullOrWhiteSpace(parsed.Name))
        {
            return new DbOperationResult(success: false, error: "Trigger name is required.");
        }

        const string TriggerInfoQuery = @"SELECT
            s.name AS [schema],
            OBJECT_NAME(tr.parent_id) AS table_name,
            tr.name,
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
        WHERE tr.name = @ObjectName
            AND tr.parent_class = 1
            AND (@SchemaName IS NULL OR s.name = @SchemaName)
            AND (@TableName IS NULL OR OBJECT_NAME(tr.parent_id) = @TableName)";

        const string DefinitionQuery = @"SELECT OBJECT_DEFINITION(tr.object_id) AS definition
        FROM sys.triggers tr
        INNER JOIN sys.objects o ON tr.parent_id = o.object_id
        INNER JOIN sys.schemas s ON o.schema_id = s.schema_id
        WHERE tr.name = @ObjectName
            AND tr.parent_class = 1
            AND (@SchemaName IS NULL OR s.name = @SchemaName)
            AND (@TableName IS NULL OR OBJECT_NAME(tr.parent_id) = @TableName)";

        var conn = await _connectionFactory.GetOpenConnectionAsync();
        try
        {
            using (conn)
            {
                var result = new Dictionary<string, object?>();

                using (var cmd = new SqlCommand(TriggerInfoQuery, conn))
                {
                    AddTriggerLookupParameters(cmd, parsed);
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
                        return new DbOperationResult(
                            success: false,
                            error: $"Trigger '{parsed.DisplayName}' not found.");
                    }
                }

                using (var cmd = new SqlCommand(DefinitionQuery, conn))
                {
                    AddTriggerLookupParameters(cmd, parsed);
                    using var reader = await cmd.ExecuteReaderAsync();
                    if (await reader.ReadAsync())
                    {
                        result["definition"] = reader["definition"] is DBNull ? null : reader["definition"];
                    }
                }

                await TryAttachInsightAsync(result, "Trigger", parsed.Schema, parsed.Name).ConfigureAwait(false);
                return new DbOperationResult(success: true, data: result);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetTrigger failed: {Message}", ex.Message);
            return new DbOperationResult(success: false, error: ex.Message);
        }
    }

    private static void AddTriggerLookupParameters(SqlCommand cmd, TriggerQualifiedName parsed)
    {
        cmd.Parameters.AddWithValue("@ObjectName", parsed.Name);
        cmd.Parameters.AddWithValue("@SchemaName", parsed.Schema is null ? DBNull.Value : parsed.Schema);
        cmd.Parameters.AddWithValue("@TableName", parsed.TableName is null ? DBNull.Value : parsed.TableName);
    }
}
