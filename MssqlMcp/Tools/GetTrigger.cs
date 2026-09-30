// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.

using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace Mssql.McpServer;

public partial class Tools
{
    private const string GetTriggerInfoQuery = @"SELECT
            s.name AS [schema],
            o.name AS table_name,
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
        WHERE tr.object_id = @ObjectId
            AND tr.parent_class = 1";

    private async Task<DbOperationResult> GetTrigger(string name, CancellationToken cancellationToken)
    {
        // Triggers read three parts as schema.table.trigger (not database.schema.name).
        if (!ObjectNameParser.TryParseTrigger(name, out var parts, out var parseError))
        {
            return new DbOperationResult(success: false, error: parseError);
        }

        try
        {
            await using var conn = await _connectionFactory.GetOpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            var resolved = await ResolveObjectAsync(conn, parts, TriggerObjectTypes, cancellationToken).ConfigureAwait(false);
            if (resolved is not { } trigger)
            {
                return new DbOperationResult(success: false, error: ObjectNotFoundMessage("Trigger", name, parts));
            }

            var result = new Dictionary<string, object?>();

            await using (var cmd = new SqlCommand(GetTriggerInfoQuery, conn))
            {
                AddObjectIdParameter(cmd, trigger.ObjectId);
                await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    return new DbOperationResult(success: false, error: ObjectNotFoundMessage("Trigger", name, parts));
                }

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

            result["definition"] = await ReadObjectDefinitionAsync(conn, trigger.ObjectId, cancellationToken).ConfigureAwait(false);

            await TryAttachInsightAsync(result, "Trigger", trigger.Schema, trigger.Name, cancellationToken).ConfigureAwait(false);
            return new DbOperationResult(success: true, data: result);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "{Tool} (Trigger) failed: {Message}", ToolNames.GetObject, ex.Message);
            return new DbOperationResult(success: false, error: ex.Message);
        }
    }
}
