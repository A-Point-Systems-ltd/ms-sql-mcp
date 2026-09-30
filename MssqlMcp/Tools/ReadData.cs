// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.

using System.ComponentModel;
using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace Mssql.McpServer;
public partial class Tools
{
    internal const int ReadDataDefaultMaxRows = 500;
    internal const int ReadDataMaxRowsCeiling = 10_000;

    [McpServerTool(
        Name = ToolNames.ReadData,
        Title = "Read Data",
        ReadOnly = true,
        Idempotent = true,
        Destructive = false),
        Description("Executes a single read-only SELECT query and returns rows as column-name objects. Use this tool for ALL queries that return result sets - user tables, sys.*, INFORMATION_SCHEMA.*, DMVs, and WITH ... SELECT. " + ToolNames.ExecuteSql + " rejects SELECT; do not use it for reads. At most maxRows rows are returned; when more exist the response has truncated=true - narrow the query (TOP/WHERE) instead of raising the cap. SQL parameters are not supported - build literals yourself and never interpolate untrusted input. For built-in schema detail prefer " + ToolNames.DescribeTable + "/" + ToolNames.DescribeView + "/" + ToolNames.GetObject + " when applicable.")]
    public async Task<DbOperationResult> ReadData(
        [Description("A single read-only T-SQL SELECT (or WITH ... SELECT). Includes queries against sys.* and INFORMATION_SCHEMA. DDL/DML, SELECT ... INTO, OPENQUERY/OPENROWSET and linked-server names are not allowed.")] string sql,
        [Description("Maximum rows to return (default 500, clamped to 1..10000).")] int maxRows = ReadDataDefaultMaxRows,
        CancellationToken cancellationToken = default)
    {
        if (!SqlStatementClassifier.TryValidateReadOnly(sql, out var validationError))
        {
            return new DbOperationResult(success: false, error: validationError);
        }

        var cap = Math.Clamp(maxRows, 1, ReadDataMaxRowsCeiling);
        try
        {
            await using var conn = await _connectionFactory.GetOpenConnectionAsync(cancellationToken).ConfigureAwait(false);

            // Backstop behind the parser gate: whatever the statement does, nothing is committed.
            await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);
            var results = new List<Dictionary<string, object?>>();
            var truncated = false;
            await using (var cmd = new SqlCommand(sql, conn, tx))
            await using (var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    if (results.Count == cap)
                    {
                        truncated = true;
                        cmd.Cancel();
                        break;
                    }

                    var row = new Dictionary<string, object?>(reader.FieldCount);
                    for (var i = 0; i < reader.FieldCount; i++)
                    {
                        row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                    }
                    results.Add(row);
                }
            }

            // Quiet: after cmd.Cancel() the server may already have ended the transaction.
            await InsightsLayer.InsightsLayerService.RollbackQuietlyAsync(tx, _logger).ConfigureAwait(false);
            return truncated
                ? new DbOperationResult(success: true, data: results) { Truncated = true, MaxRows = cap }
                : new DbOperationResult(success: true, data: results);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "{Tool} failed: {Message}", ToolNames.ReadData, ex.Message);
            return new DbOperationResult(success: false, error: ex.Message);
        }
    }
}
