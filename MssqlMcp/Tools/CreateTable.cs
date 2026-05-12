// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.

using System.ComponentModel;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace Mssql.McpServer;

public partial class Tools
{
    [McpServerTool(
        Title = "Create Table",
        ReadOnly = false,
        Destructive = false),
        Description("Creates a new table from a CREATE TABLE T-SQL statement. Unless the AI Insights layer is disabled (USE_INSIGHTS_LAYER=false/0/off), a background DDL/fingerprint reconciliation is queued after success. Use DescribeTable to verify the result.")]
    public async Task<DbOperationResult> CreateTable(
        [Description("A complete CREATE TABLE T-SQL statement (schema-qualified name recommended).")] string sql)
    {
        var conn = await _connectionFactory.GetOpenConnectionAsync();
        try
        {
            using (conn)
            {
                using var cmd = new Microsoft.Data.SqlClient.SqlCommand(sql, conn);
                _ = await cmd.ExecuteNonQueryAsync();
                QueueInsightDdlProcessing();
                return new DbOperationResult(success: true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CreateTable failed: {Message}", ex.Message);
            return new DbOperationResult(success: false, error: ex.Message);
        }
    }
}
