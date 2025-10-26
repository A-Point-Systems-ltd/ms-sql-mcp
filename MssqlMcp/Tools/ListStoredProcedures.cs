// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.

using System.ComponentModel;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace Mssql.McpServer;

public partial class Tools
{
    private const string ListStoredProceduresQuery = @"
        SELECT 
            s.name AS [schema],
            p.name AS name,
            p.object_id AS id,
            p.create_date,
            p.modify_date,
            ep.value AS description
        FROM sys.procedures p
        INNER JOIN sys.schemas s ON p.schema_id = s.schema_id
        LEFT JOIN sys.extended_properties ep 
            ON ep.major_id = p.object_id 
            AND ep.minor_id = 0 
            AND ep.name = 'MS_Description'
        ORDER BY s.name, p.name";

    [McpServerTool(
        Title = "List Stored Procedures",
        ReadOnly = true,
        Idempotent = true,
        Destructive = false),
        Description("List all stored procedures with descriptions")]
    public async Task<DbOperationResult> ListStoredProcedures()
    {
        var conn = await _connectionFactory.GetOpenConnectionAsync();
        try
        {
            using (conn)
            {
                using var cmd = new SqlCommand(ListStoredProceduresQuery, conn);
                var procedures = new List<Dictionary<string, object?>>();
                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    procedures.Add(new Dictionary<string, object?>
                    {
                        ["schema"] = reader["schema"],
                        ["name"] = reader["name"],
                        ["id"] = reader["id"],
                        ["create_date"] = reader["create_date"],
                        ["modify_date"] = reader["modify_date"],
                        ["description"] = reader["description"] is DBNull ? null : reader["description"]
                    });
                }
                return new DbOperationResult(success: true, data: procedures);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ListStoredProcedures failed: {Message}", ex.Message);
            return new DbOperationResult(success: false, error: ex.Message);
        }
    }
}