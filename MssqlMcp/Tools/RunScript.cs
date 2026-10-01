// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.

using System.ComponentModel;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using Mssql.McpServer.Connections;
using Mssql.McpServer.Scripting;

namespace Mssql.McpServer;

/// <summary>
/// The VS Code extension's query-window runner. Deliberately not an <see cref="McpServerToolTypeAttribute"/> class:
/// <c>WithToolsFromAssembly</c> must not list it to agents; Program registers it only when MSSQL_SCRIPT_RUNNER=true.
/// </summary>
public sealed class ScriptRunnerTools(ISqlConnectionFactory connectionFactory, ILogger<ScriptRunnerTools> logger)
{
    internal const int DefaultMaxRows = 1000;
    internal const int MaxRowsCeiling = 10_000;
    internal const string EnableVariable = "MSSQL_SCRIPT_RUNNER";

    /// <summary>The single switch for registering and routing run_script: <c>MSSQL_SCRIPT_RUNNER=true</c> (case-insensitive).</summary>
    internal static bool IsEnabled(Func<string, string?> getEnvironmentVariable) =>
        string.Equals(getEnvironmentVariable(EnableVariable), "true", StringComparison.OrdinalIgnoreCase);

    [McpServerTool(
        Name = ToolNames.RunScript,
        Title = "Run Script",
        ReadOnly = false,
        Idempotent = false,
        Destructive = true),
        Description("Internal tool of the MSSQL-MCP editor extension: runs a multi-batch T-SQL script (GO separators) like SSMS and returns every result set and message. Read-only connections run only single SELECT batches.")]
    public async Task<DbOperationResult> RunScript(
        [Description("The T-SQL script; batches are separated by GO lines (GO n repeats a batch).")] string script,
        [Description("Maximum rows kept per result set (default 1000, clamped to 1..10000); further rows are only counted.")] int maxRows = DefaultMaxRows,
        [Description(Tools.ConnectionParamDescription)] string? connection = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(script))
        {
            return new DbOperationResult(success: false, error: "script is required.");
        }

        var readOnly = CurrentConnection.Value?.ReadOnly ?? true;
        SqlConnection conn;
        try
        {
            conn = await connectionFactory.GetOpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "{Tool} could not open the connection: {Message}", ToolNames.RunScript, ex.Message);
            return new DbOperationResult(success: false, error: ex.Message);
        }

        await using (conn.ConfigureAwait(false))
        {
            try
            {
                var result = await ScriptRunner
                    .RunAsync(conn, script, readOnly, Math.Clamp(maxRows, 1, MaxRowsCeiling), cancellationToken, logger)
                    .ConfigureAwait(false);
                return new DbOperationResult(success: true, data: result);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // SQL errors are messages in the result; this is only reached by an unexpected runner failure.
                logger.LogError(ex, "{Tool} failed: {Message}", ToolNames.RunScript, ex.Message);
                return new DbOperationResult(success: false, error: ex.Message);
            }
        }
    }
}
