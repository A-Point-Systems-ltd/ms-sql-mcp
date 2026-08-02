// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.

using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace Mssql.McpServer;

public partial class Tools
{
    private async Task<DbOperationResult> GetFunction(string name)
    {
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

        // Query for function info
        const string FunctionInfoQuery = @"SELECT 
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
        WHERE o.name = @ObjectName 
            AND (s.name = @SchemaName OR @SchemaName IS NULL)
            AND o.type IN ('FN', 'TF', 'IF', 'FT')";

        // Query for parameters
        const string ParametersQuery = @"SELECT 
            pm.name,
            t.name AS type,
            pm.max_length,
            pm.precision,
            pm.scale,
            pm.is_output
        FROM sys.parameters pm
        INNER JOIN sys.types t ON pm.user_type_id = t.user_type_id
        WHERE pm.object_id = (
            SELECT o.object_id 
            FROM sys.objects o
            INNER JOIN sys.schemas s ON o.schema_id = s.schema_id
            WHERE o.name = @ObjectName 
                AND (s.name = @SchemaName OR @SchemaName IS NULL)
                AND o.type IN ('FN', 'TF', 'IF', 'FT')
        )
        ORDER BY pm.parameter_id";

        // Query for code definition
        const string DefinitionQuery = @"SELECT OBJECT_DEFINITION(o.object_id) AS definition
        FROM sys.objects o
        INNER JOIN sys.schemas s ON o.schema_id = s.schema_id
        WHERE o.name = @ObjectName 
            AND (s.name = @SchemaName OR @SchemaName IS NULL)
            AND o.type IN ('FN', 'TF', 'IF', 'FT')";

        var conn = await _connectionFactory.GetOpenConnectionAsync();
        try
        {
            using (conn)
            {
                var result = new Dictionary<string, object?>();

                // Function Info
                using (var cmd = new SqlCommand(FunctionInfoQuery, conn))
                {
                    cmd.Parameters.AddWithValue("@ObjectName", name);
                    cmd.Parameters.AddWithValue("@SchemaName", schema == null ? DBNull.Value : schema);
                    using var reader = await cmd.ExecuteReaderAsync();
                    if (await reader.ReadAsync())
                    {
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
                    else
                    {
                        return new DbOperationResult(success: false, error: $"Function '{name}' not found.");
                    }
                }

                // Parameters
                using (var cmd = new SqlCommand(ParametersQuery, conn))
                {
                    cmd.Parameters.AddWithValue("@ObjectName", name);
                    cmd.Parameters.AddWithValue("@SchemaName", schema == null ? DBNull.Value : schema);
                    using var reader = await cmd.ExecuteReaderAsync();
                    var parameters = new List<object>();
                    while (await reader.ReadAsync())
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

                await TryAttachInsightAsync(result, "Function", schema, name).ConfigureAwait(false);
                return new DbOperationResult(success: true, data: result);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetFunction failed: {Message}", ex.Message);
            return new DbOperationResult(success: false, error: ex.Message);
        }
    }
}