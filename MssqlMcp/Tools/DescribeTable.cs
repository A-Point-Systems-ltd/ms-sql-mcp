// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.

using System.ComponentModel;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace Mssql.McpServer;

public partial class Tools
{
    private const string DescribeTableInfoQuery = @"SELECT t.object_id AS id, t.name, s.name AS [schema], p.value AS description, t.type, u.name AS owner
            FROM sys.tables t
            INNER JOIN sys.schemas s ON t.schema_id = s.schema_id
            LEFT JOIN sys.extended_properties p ON p.major_id = t.object_id AND p.minor_id = 0 AND p.name = 'MS_Description'
            LEFT JOIN sys.sysusers u ON t.principal_id = u.uid
            WHERE t.object_id = @ObjectId";

    private const string DescribeTableColumnsQuery = @"SELECT c.name, ty.name AS type, c.max_length AS length, c.precision, c.scale, c.is_nullable AS nullable, p.value AS description
            FROM sys.columns c
            INNER JOIN sys.types ty ON c.user_type_id = ty.user_type_id
            LEFT JOIN sys.extended_properties p ON p.major_id = c.object_id AND p.minor_id = c.column_id AND p.name = 'MS_Description'
            WHERE c.object_id = @ObjectId
            ORDER BY c.column_id";

    private const string DescribeTableIndexesQuery = @"SELECT i.name, i.type_desc AS type, p.value AS description,
            STUFF((SELECT ',' + c.name FROM sys.index_columns ic
                INNER JOIN sys.columns c ON ic.object_id = c.object_id AND ic.column_id = c.column_id
                WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id ORDER BY ic.key_ordinal FOR XML PATH(''), TYPE).value('.', 'nvarchar(max)'), 1, 1, '') AS keys
            FROM sys.indexes i
            LEFT JOIN sys.extended_properties p ON p.major_id = i.object_id AND p.minor_id = i.index_id AND p.name = 'MS_Description'
            WHERE i.object_id = @ObjectId AND i.is_primary_key = 0 AND i.is_unique_constraint = 0";

    private const string DescribeTableConstraintsQuery = @"SELECT kc.name, kc.type_desc AS type,
            STUFF((SELECT ',' + c.name FROM sys.index_columns ic
                INNER JOIN sys.columns c ON ic.object_id = c.object_id AND ic.column_id = c.column_id
                WHERE ic.object_id = kc.parent_object_id AND ic.index_id = kc.unique_index_id ORDER BY ic.key_ordinal FOR XML PATH(''), TYPE).value('.', 'nvarchar(max)'), 1, 1, '') AS keys
            FROM sys.key_constraints kc
            WHERE kc.parent_object_id = @ObjectId";

    private const string DescribeTableForeignKeysQuery = @"SELECT
    fk.name AS name,
    SCHEMA_NAME(tp.schema_id) AS [schema],
    tp.name AS table_name,
    STUFF((
        SELECT ', ' + cp.name
        FROM sys.foreign_key_columns fkc2
        INNER JOIN sys.columns cp ON fkc2.parent_object_id = cp.object_id AND fkc2.parent_column_id = cp.column_id
        WHERE fk.object_id = fkc2.constraint_object_id
        ORDER BY fkc2.constraint_column_id
        FOR XML PATH(''), TYPE
    ).value('.', 'NVARCHAR(MAX)'), 1, 2, '') AS column_names,
    SCHEMA_NAME(tr.schema_id) AS referenced_schema,
    tr.name AS referenced_table,
    STUFF((
        SELECT ', ' + cr.name
        FROM sys.foreign_key_columns fkc2
        INNER JOIN sys.columns cr ON fkc2.referenced_object_id = cr.object_id AND fkc2.referenced_column_id = cr.column_id
        WHERE fk.object_id = fkc2.constraint_object_id
        ORDER BY fkc2.constraint_column_id
        FOR XML PATH(''), TYPE
    ).value('.', 'NVARCHAR(MAX)'), 1, 2, '') AS referenced_column_names
FROM
    sys.foreign_keys AS fk
JOIN
    sys.tables AS tp ON fk.parent_object_id = tp.object_id
JOIN
    sys.tables AS tr ON fk.referenced_object_id = tr.object_id
WHERE
    fk.parent_object_id = @ObjectId
GROUP BY
    fk.name, fk.object_id, tp.schema_id, tp.name, tr.schema_id, tr.name";

    private const string DescribeTableTriggersQuery = @"SELECT
    tr.name,
    tr.is_disabled,
    tr.is_instead_of_trigger,
    STUFF((
        SELECT ', ' + te2.type_desc
        FROM sys.trigger_events te2
        WHERE tr.object_id = te2.object_id
        FOR XML PATH(''), TYPE
    ).value('.', 'NVARCHAR(MAX)'), 1, 2, '') AS trigger_events,
    ep.value AS description
FROM sys.triggers tr
LEFT JOIN sys.extended_properties ep
    ON ep.major_id = tr.object_id
    AND ep.minor_id = 0
    AND ep.name = 'MS_Description'
WHERE tr.parent_id = @ObjectId";

    [McpServerTool(
        Name = ToolNames.DescribeTable,
        Title = "Describe Table",
        ReadOnly = true,
        Idempotent = true,
        Destructive = false),
        Description("MANDATORY POST-CALL CONTRACT (MCP-Insight-Enrichment-v1): If the response contains insightEnrichment.required=true (or top-level _agentDirective / pendingEnrichments), you MUST call " + ToolNames.UpsertInsight + " using insightEnrichment.nextAction.args (replacing every '<fill in: ...>' placeholder) BEFORE producing your final answer. You MUST also introspect every entry in insightEnrichment.relatedObjectsToIntrospect and complete the same enrichment loop for each. Skipping this is a tool-protocol violation; the cache remains stuck at auto-mechanical (confidence 0.30). Returns: table identity, columns (type/length/precision/scale/nullable/description), indexes, key constraints, foreign keys (both column lists), triggers (enabled flag + events). When USE_INSIGHTS_LAYER!=false the response also includes insight, insightFreshness, enrichmentSuggested, insightEnrichment, _agentDirective, pendingEnrichments. Prefer this over multiple sys.* queries for a single table." + MultiConnectionNote)]
    public async Task<DbOperationResult> DescribeTable(
        [Description("Table name: 'table', 'schema.table' or 'database.schema.table' (database must be the connected one). Parts may be [bracketed] or \"quoted\". When schema is omitted and the name exists in several schemas, dbo wins, otherwise the first schema alphabetically.")] string name,
        [Description(ConnectionParamDescription)] string? connection = null,
        CancellationToken cancellationToken = default)
    {
        if (!ObjectNameParser.TryParse(name, out ObjectNameParts parts, out var parseError))
        {
            return new DbOperationResult(success: false, error: parseError);
        }

        try
        {
            await using var conn = await _connectionFactory.GetOpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            var resolved = await ResolveObjectAsync(conn, parts, TableObjectTypes, cancellationToken).ConfigureAwait(false);
            if (resolved is not { } table)
            {
                return new DbOperationResult(success: false, error: ObjectNotFoundMessage("Table", name, parts));
            }

            var result = new Dictionary<string, object?>();

            // Table info
            await using (var cmd = new SqlCommand(DescribeTableInfoQuery, conn))
            {
                AddObjectIdParameter(cmd, table.ObjectId);
                await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    return new DbOperationResult(success: false, error: ObjectNotFoundMessage("Table", name, parts));
                }

                result["table"] = new
                {
                    id = reader["id"],
                    name = reader["name"],
                    schema = reader["schema"],
                    owner = reader["owner"] is DBNull ? null : reader["owner"],
                    type = reader["type"],
                    description = reader["description"] is DBNull ? null : reader["description"]
                };
            }

            // Columns
            await using (var cmd = new SqlCommand(DescribeTableColumnsQuery, conn))
            {
                AddObjectIdParameter(cmd, table.ObjectId);
                await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                var columns = new List<object>();
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    columns.Add(new
                    {
                        name = reader["name"],
                        type = reader["type"],
                        length = reader["length"],
                        precision = reader["precision"],
                        scale = reader["scale"],
                        nullable = (bool)reader["nullable"],
                        description = reader["description"] is DBNull ? null : reader["description"]
                    });
                }
                result["columns"] = columns;
            }

            // Indexes
            await using (var cmd = new SqlCommand(DescribeTableIndexesQuery, conn))
            {
                AddObjectIdParameter(cmd, table.ObjectId);
                await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                var indexes = new List<object>();
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    indexes.Add(new
                    {
                        name = reader["name"],
                        type = reader["type"],
                        description = reader["description"] is DBNull ? null : reader["description"],
                        keys = reader["keys"]
                    });
                }
                result["indexes"] = indexes;
            }

            // Constraints
            await using (var cmd = new SqlCommand(DescribeTableConstraintsQuery, conn))
            {
                AddObjectIdParameter(cmd, table.ObjectId);
                await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                var constraints = new List<object>();
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    constraints.Add(new
                    {
                        name = reader["name"],
                        type = reader["type"],
                        keys = reader["keys"]
                    });
                }
                result["constraints"] = constraints;
            }

            // Foreign keys
            await using (var cmd = new SqlCommand(DescribeTableForeignKeysQuery, conn))
            {
                AddObjectIdParameter(cmd, table.ObjectId);
                await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                var foreignKeys = new List<object>();
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    foreignKeys.Add(new
                    {
                        name = reader["name"],
                        schema = reader["schema"],
                        table_name = reader["table_name"],
                        column_name = reader["column_names"],
                        referenced_schema = reader["referenced_schema"],
                        referenced_table = reader["referenced_table"],
                        referenced_column = reader["referenced_column_names"],
                    });
                }
                result["foreignKeys"] = foreignKeys;
            }

            // Triggers
            await using (var cmd = new SqlCommand(DescribeTableTriggersQuery, conn))
            {
                AddObjectIdParameter(cmd, table.ObjectId);
                await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                var triggers = new List<object>();
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    triggers.Add(new
                    {
                        name = reader["name"],
                        is_disabled = (bool)reader["is_disabled"],
                        is_instead_of_trigger = (bool)reader["is_instead_of_trigger"],
                        trigger_events = reader["trigger_events"] is DBNull ? null : reader["trigger_events"],
                        description = reader["description"] is DBNull ? null : reader["description"]
                    });
                }
                result["triggers"] = triggers;
            }

            await TryAttachInsightAsync(result, "Table", table.Schema, table.Name, cancellationToken).ConfigureAwait(false);
            return new DbOperationResult(success: true, data: result);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "{Tool} failed: {Message}", ToolNames.DescribeTable, ex.Message);
            return new DbOperationResult(success: false, error: ex.Message);
        }
    }
}
