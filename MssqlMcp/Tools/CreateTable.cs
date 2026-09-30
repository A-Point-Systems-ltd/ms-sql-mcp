// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.

using System.ComponentModel;
using ModelContextProtocol.Server;

namespace Mssql.McpServer;

public partial class Tools
{
    // Additive only: the statement gate below rejects anything that is not a single CREATE TABLE.
    [McpServerTool(
        Name = ToolNames.CreateTable,
        Title = "Create Table",
        ReadOnly = false,
        Destructive = false),
        Description("Creates a new table from a single CREATE TABLE T-SQL statement; any other statement type is rejected. Unless the AI Insights layer is disabled (USE_INSIGHTS_LAYER=false/0/off), a background DDL/fingerprint reconciliation is queued after success. Use " + ToolNames.DescribeTable + " to verify the result." + MultiConnectionNote)]
    public Task<DbOperationResult> CreateTable(
        [Description("A complete CREATE TABLE T-SQL statement (schema-qualified name recommended).")] string sql,
        [Description(ConnectionParamDescription)] string? connection = null,
        CancellationToken cancellationToken = default) =>
        ExecuteWriteAsync(sql, SqlStatementKind.CreateTable, ToolNames.CreateTable, includeRowsAffected: false, cancellationToken);
}
