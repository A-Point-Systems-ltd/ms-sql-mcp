// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.

using System.ComponentModel;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace Mssql.McpServer;

public partial class Tools
{
    private const string ListViewsQuery = @"
        SELECT 
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
        ORDER BY s.name, v.name";

    [McpServerTool(
        Title = "List Views",
        ReadOnly = true,
        Idempotent = true,
        Destructive = false),
        Description("List all views")]
    public async Task<DbOperationResult> ListViews()
    {
        var conn = await _connectionFactory.GetOpenConnectionAsync();
        try
        {
            using (conn)
            {
                using var cmd = new SqlCommand(ListViewsQuery, conn);
                var views = new List<Dictionary<string, object?>>();
                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    views.Add(new Dictionary<string, object?>
                    {
                        ["schema"] = reader["schema"],
                        ["name"] = reader["name"],
                        ["id"] = reader["id"],
                        ["create_date"] = reader["create_date"],
                        ["modify_date"] = reader["modify_date"],
                        ["description"] = reader["description"] is DBNull ? null : reader["description"]
                    });
                }
                return new DbOperationResult(success: true, data: views);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ListViews failed: {Message}", ex.Message);
            return new DbOperationResult(success: false, error: ex.Message);
        }
    }
}