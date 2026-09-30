// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.

using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace Mssql.McpServer;

public partial class Tools
{
    private const string GetFunctionInfoQuery = @"SELECT
            s.name AS [schema],
            o.name,
            o.type_desc,
            o.create_date,
            o.modify_date,
            ep.value AS description
        FROM sys.objects o
        INNER JOIN sys.schemas s ON o.schema_id = s.schema_id
        LEFT JOIN sys.extended_properties ep
            ON ep.major_id = o.object_id
            AND ep.minor_id = 0
            AND ep.name = 'MS_Description'
        WHERE o.object_id = @ObjectId";

    private const string GetFunctionParametersQuery = @"SELECT
            pm.name,
            t.name AS type,
            pm.max_length,
            pm.precision,
            pm.scale,
            pm.is_output
        FROM sys.parameters pm
        INNER JOIN sys.types t ON pm.user_type_id = t.user_type_id
        WHERE pm.object_id = @ObjectId
        ORDER BY pm.parameter_id";

    private async Task<DbOperationResult> GetFunction(string name, CancellationToken cancellationToken)
    {
        if (!ObjectNameParser.TryParse(name, out ObjectNameParts parts, out var parseError))
        {
            return new DbOperationResult(success: false, error: parseError);
        }

        try
        {
            await using var conn = await _connectionFactory.GetOpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            var resolved = await ResolveObjectAsync(conn, parts, FunctionObjectTypes, cancellationToken).ConfigureAwait(false);
            if (resolved is not { } function)
            {
                return new DbOperationResult(success: false, error: ObjectNotFoundMessage("Function", name, parts));
            }

            var result = new Dictionary<string, object?>();

            // Function info
            await using (var cmd = new SqlCommand(GetFunctionInfoQuery, conn))
            {
                AddObjectIdParameter(cmd, function.ObjectId);
                await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    return new DbOperationResult(success: false, error: ObjectNotFoundMessage("Function", name, parts));
                }

                result["function"] = new
                {
                    schema = reader["schema"],
                    name = reader["name"],
                    type_desc = reader["type_desc"],
                    create_date = reader["create_date"],
                    modify_date = reader["modify_date"],
                    description = reader["description"] is DBNull ? null : reader["description"]
                };
            }

            // Parameters
            await using (var cmd = new SqlCommand(GetFunctionParametersQuery, conn))
            {
                AddObjectIdParameter(cmd, function.ObjectId);
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
                        is_output = (bool)reader["is_output"]
                    });
                }
                result["parameters"] = parameters;
            }

            result["definition"] = await ReadObjectDefinitionAsync(conn, function.ObjectId, cancellationToken).ConfigureAwait(false);

            await TryAttachInsightAsync(result, "Function", function.Schema, function.Name, cancellationToken).ConfigureAwait(false);
            return new DbOperationResult(success: true, data: result);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "{Tool} (Function) failed: {Message}", ToolNames.GetObject, ex.Message);
            return new DbOperationResult(success: false, error: ex.Message);
        }
    }
}
