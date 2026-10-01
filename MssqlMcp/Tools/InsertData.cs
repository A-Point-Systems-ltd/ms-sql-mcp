// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.

using System.ComponentModel;
using ModelContextProtocol.Server;

namespace Mssql.McpServer;

public partial class Tools
{
    // Additive only: the statement gate below rejects anything that is not a single INSERT.
    [McpServerTool(
        Name = ToolNames.InsertData,
        Title = "Insert Data",
        ReadOnly = false,
        Destructive = false),
        Description("Inserts rows from a single INSERT T-SQL statement; any other statement type is rejected. Returns rowsAffected. Use parameter-less, fully literal SQL; multi-statement scripts are not supported. Unless the AI Insights layer is disabled (USE_INSIGHTS_LAYER=false/0/off), a background DDL/fingerprint reconciliation is queued after success." + MultiConnectionNote)]
    public Task<DbOperationResult> InsertData(
        [Description("A complete INSERT T-SQL statement (INSERT ... VALUES / INSERT ... SELECT).")] string sql,
        [Description(ConnectionParamDescription)] string? connection = null,
        CancellationToken cancellationToken = default) =>
        ExecuteWriteAsync(sql, SqlStatementKind.Insert, ToolNames.InsertData, includeRowsAffected: true, cancellationToken);
}
