// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.

using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace Mssql.McpServer;

public partial class Tools
{
    private const string GetProcedureInfoQuery = @"SELECT
            s.name AS [schema],
            p.name,
            p.create_date,
            p.modify_date,
            ep.value AS description
        FROM sys.procedures p
        INNER JOIN sys.schemas s ON p.schema_id = s.schema_id
        LEFT JOIN sys.extended_properties ep
            ON ep.major_id = p.object_id
            AND ep.minor_id = 0
            AND ep.name = 'MS_Description'
        WHERE p.object_id = @ObjectId";

    private const string GetProcedureParametersQuery = @"SELECT
            pm.name,
            t.name AS type,
            pm.max_length,
            pm.precision,
            pm.scale,
            pm.is_output,
            pm.has_default_value,
            pm.default_value
        FROM sys.parameters pm
        INNER JOIN sys.types t ON pm.user_type_id = t.user_type_id
        WHERE pm.object_id = @ObjectId
        ORDER BY pm.parameter_id";

    private async Task<DbOperationResult> GetStoredProc(string name, CancellationToken cancellationToken)
    {
        if (!ObjectNameParser.TryParse(name, out ObjectNameParts parts, out var parseError))
        {
            return new DbOperationResult(success: false, error: parseError);
        }

        try
        {
            await using var conn = await _connectionFactory.GetOpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            var resolved = await ResolveObjectAsync(conn, parts, ProcedureObjectTypes, cancellationToken).ConfigureAwait(false);
            if (resolved is not { } procedure)
            {
                return new DbOperationResult(success: false, error: ObjectNotFoundMessage("Stored procedure", name, parts));
            }

            var result = new Dictionary<string, object?>();

            // Procedure info
            await using (var cmd = new SqlCommand(GetProcedureInfoQuery, conn))
            {
                AddObjectIdParameter(cmd, procedure.ObjectId);
                await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    return new DbOperationResult(success: false, error: ObjectNotFoundMessage("Stored procedure", name, parts));
                }

                result["procedure"] = new
                {
                    schema = reader["schema"],
                    name = reader["name"],
                    create_date = reader["create_date"],
                    modify_date = reader["modify_date"],
                    description = reader["description"] is DBNull ? null : reader["description"]
                };
            }

            // Parameters
            await using (var cmd = new SqlCommand(GetProcedureParametersQuery, conn))
            {
                AddObjectIdParameter(cmd, procedure.ObjectId);
                await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                var parameters = new List<object>();
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    parameters.Add(new
                    {
                        name = reader["name"],
                        type = reader["type"],
                        max_length = reader["max_length"],
                        precision = reader["precision"],
                        scale = reader["scale"],
                        is_output = (bool)reader["is_output"],
                        has_default_value = (bool)reader["has_default_value"],
                        default_value = reader["default_value"] is DBNull ? null : reader["default_value"]
                    });
                }
                result["parameters"] = parameters;
            }

            result["definition"] = await ReadObjectDefinitionAsync(conn, procedure.ObjectId, cancellationToken).ConfigureAwait(false);

            await TryAttachInsightAsync(result, "Procedure", procedure.Schema, procedure.Name, cancellationToken).ConfigureAwait(false);
            return new DbOperationResult(success: true, data: result);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "{Tool} (StoredProcedure) failed: {Message}", ToolNames.GetObject, ex.Message);
            return new DbOperationResult(success: false, error: ex.Message);
        }
    }
}
