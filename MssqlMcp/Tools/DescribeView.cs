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
        Title = "Describe View",
        ReadOnly = true,
        Idempotent = true,
        Destructive = false),
        Description("Get view definition including columns and SQL code")]
    public async Task<DbOperationResult> DescribeView(
        [Description("Name of view, supports schema.viewname format")] string name)
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

        // Query for view info
        const string ViewInfoQuery = @"SELECT 
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
        WHERE v.name = @ObjectName 
            AND (s.name = @SchemaName OR @SchemaName IS NULL)";

        // Query for columns
        const string ColumnsQuery = @"SELECT 
            c.name,
            ty.name AS type,
            c.max_length,
            c.precision,
            c.scale,
            c.is_nullable
        FROM sys.columns c
        INNER JOIN sys.types ty ON c.user_type_id = ty.user_type_id
        WHERE c.object_id = (
            SELECT v.object_id 
            FROM sys.views v
            INNER JOIN sys.schemas s ON v.schema_id = s.schema_id
            WHERE v.name = @ObjectName 
                AND (s.name = @SchemaName OR @SchemaName IS NULL)
        )
        ORDER BY c.column_id";

        // Query for code definition
        const string DefinitionQuery = @"SELECT OBJECT_DEFINITION(v.object_id) AS definition
        FROM sys.views v
        INNER JOIN sys.schemas s ON v.schema_id = s.schema_id
        WHERE v.name = @ObjectName 
            AND (s.name = @SchemaName OR @SchemaName IS NULL)";

        var conn = await _connectionFactory.GetOpenConnectionAsync();
        try
        {
            using (conn)
            {
                var result = new Dictionary<string, object?>();

                // View Info
                using (var cmd = new SqlCommand(ViewInfoQuery, conn))
                {
                    cmd.Parameters.AddWithValue("@ObjectName", name);
                    cmd.Parameters.AddWithValue("@SchemaName", schema == null ? DBNull.Value : schema);
                    using var reader = await cmd.ExecuteReaderAsync();
                    if (await reader.ReadAsync())
                    {
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
                    else
                    {
                        return new DbOperationResult(success: false, error: $"View '{name}' not found.");
                    }
                }

                // Columns
                using (var cmd = new SqlCommand(ColumnsQuery, conn))
                {
                    cmd.Parameters.AddWithValue("@ObjectName", name);
                    cmd.Parameters.AddWithValue("@SchemaName", schema == null ? DBNull.Value : schema);
                    using var reader = await cmd.ExecuteReaderAsync();
                    var columns = new List<object>();
                    while (await reader.ReadAsync())
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

                await TryAttachInsightAsync(result, "View", schema, name).ConfigureAwait(false);
                return new DbOperationResult(success: true, data: result);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DescribeView failed: {Message}", ex.Message);
            return new DbOperationResult(success: false, error: ex.Message);
        }
    }
}