// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.

using System.ComponentModel;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace Mssql.McpServer;

public partial class Tools
{
    private const string DescribeViewInfoQuery = @"SELECT
            s.name AS [schema],
            v.name,
            v.object_id AS id,
            v.create_date,
            v.modify_date,
            ep.value AS description
        FROM sys.views v
        INNER JOIN sys.schemas s ON v.schema_id = s.schema_id
        LEFT JOIN sys.extended_properties ep
            ON ep.major_id = v.object_id
            AND ep.minor_id = 0
            AND ep.name = 'MS_Description'
        WHERE v.object_id = @ObjectId";

    private const string DescribeViewColumnsQuery = @"SELECT
            c.name,
            ty.name AS type,
            c.max_length,
            c.precision,
            c.scale,
            c.is_nullable
        FROM sys.columns c
        INNER JOIN sys.types ty ON c.user_type_id = ty.user_type_id
        WHERE c.object_id = @ObjectId
        ORDER BY c.column_id";

    [McpServerTool(
        Name = ToolNames.DescribeView,
        Title = "Describe View",
        ReadOnly = true,
        Idempotent = true,
        Destructive = false),
        Description("MANDATORY POST-CALL CONTRACT (MCP-Insight-Enrichment-v1): If the response contains insightEnrichment.required=true (or top-level _agentDirective / pendingEnrichments), you MUST call " + ToolNames.UpsertInsight + " using insightEnrichment.nextAction.args (replacing every '<fill in: ...>' placeholder) BEFORE producing your final answer. You MUST also introspect every entry in insightEnrichment.relatedObjectsToIntrospect and complete the same enrichment loop for each. Skipping this is a tool-protocol violation. Returns: view metadata (schema, name, id, create/modify dates, description), column list, and full T-SQL definition. When USE_INSIGHTS_LAYER!=false the response also includes insight, insightFreshness, enrichmentSuggested, insightEnrichment, _agentDirective, pendingEnrichments." + MultiConnectionNote)]
    public async Task<DbOperationResult> DescribeView(
        [Description("View name: 'view', 'schema.view' or 'database.schema.view' (database must be the connected one). Parts may be [bracketed] or \"quoted\". When schema is omitted and the name exists in several schemas, dbo wins, otherwise the first schema alphabetically.")] string name,
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
            var resolved = await ResolveObjectAsync(conn, parts, ViewObjectTypes, cancellationToken).ConfigureAwait(false);
            if (resolved is not { } view)
            {
                return new DbOperationResult(success: false, error: ObjectNotFoundMessage("View", name, parts));
            }

            var result = new Dictionary<string, object?>();

            // View info
            await using (var cmd = new SqlCommand(DescribeViewInfoQuery, conn))
            {
                AddObjectIdParameter(cmd, view.ObjectId);
                await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    return new DbOperationResult(success: false, error: ObjectNotFoundMessage("View", name, parts));
                }

                result["view"] = new
                {
                    schema = reader["schema"],
                    name = reader["name"],
                    id = reader["id"],
                    create_date = reader["create_date"],
                    modify_date = reader["modify_date"],
                    description = reader["description"] is DBNull ? null : reader["description"]
                };
            }

            // Columns
            await using (var cmd = new SqlCommand(DescribeViewColumnsQuery, conn))
            {
                AddObjectIdParameter(cmd, view.ObjectId);
                await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                var columns = new List<object>();
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    columns.Add(new
                    {
                        name = reader["name"],
                        type = reader["type"],
                        max_length = reader["max_length"],
                        precision = reader["precision"],
                        scale = reader["scale"],
                        is_nullable = (bool)reader["is_nullable"]
                    });
                }
                result["columns"] = columns;
            }

            result["definition"] = await ReadObjectDefinitionAsync(conn, view.ObjectId, cancellationToken).ConfigureAwait(false);

            await TryAttachInsightAsync(result, "View", view.Schema, view.Name, cancellationToken).ConfigureAwait(false);
            return new DbOperationResult(success: true, data: result);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "{Tool} failed: {Message}", ToolNames.DescribeView, ex.Message);
            return new DbOperationResult(success: false, error: ex.Message);
        }
    }
}
