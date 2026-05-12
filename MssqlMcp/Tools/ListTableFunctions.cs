// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.

using System.ComponentModel;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace Mssql.McpServer;

public partial class Tools
{
    private const string ListTableFunctionsQuery = @"
        SELECT 
            s.name AS [schema],
            o.name,
            o.object_id AS id,
            o.create_date,
            o.modify_date,
            o.type_desc,
            ep.value AS description
        FROM sys.objects o
        INNER JOIN sys.schemas s ON o.schema_id = s.schema_id
        LEFT JOIN sys.extended_properties ep 
            ON ep.major_id = o.object_id 
            AND ep.minor_id = 0 
            AND ep.name = 'MS_Description'
        WHERE o.type IN ('TF', 'IF', 'FT')  -- TF=Table Function, IF=Inline Table Function, FT=Assembly Table Function
        ORDER BY s.name, o.name";

    [McpServerTool(
        Title = "List Table Functions",
        ReadOnly = true,
        Idempotent = true,
        Destructive = false),
        Description("Lists all table-valued functions (inline IF, multi-statement TF, assembly FT) with schema, name, type_desc, create/modify dates, and description. Use GetFunction for parameters + T-SQL of a specific function. For scalar functions use ListScalarFunctions.")]
    public async Task<DbOperationResult> ListTableFunctions()
    {
        var conn = await _connectionFactory.GetOpenConnectionAsync();
        try
        {
            using (conn)
            {
                using var cmd = new SqlCommand(ListTableFunctionsQuery, conn);
                var functions = new List<Dictionary<string, object?>>();
                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    functions.Add(new Dictionary<string, object?>
                    {
                        ["schema"] = reader["schema"],
                        ["name"] = reader["name"],
                        ["id"] = reader["id"],
                        ["create_date"] = reader["create_date"],
                        ["modify_date"] = reader["modify_date"],
                        ["type_desc"] = reader["type_desc"],
                        ["description"] = reader["description"] is DBNull ? null : reader["description"]
                    });
                }
                return new DbOperationResult(success: true, data: functions);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ListTableFunctions failed: {Message}", ex.Message);
            return new DbOperationResult(success: false, error: ex.Message);
        }
    }
}