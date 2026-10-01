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
/// The VS Code extension's private tools: the query-window runner (<c>run_script</c>) and the DDL history
/// (<c>ddl_history</c>). Deliberately not an <see cref="McpServerToolTypeAttribute"/> class: <c>WithToolsFromAssembly</c>
/// must not list them to agents; Program registers them only when MSSQL_SCRIPT_RUNNER=true.
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
            // Unpooled: each run is a new session, so SET / sp_setapprole / EXECUTE AS from an earlier run cannot leak in.
            conn = await connectionFactory.GetOpenUnpooledConnectionAsync(cancellationToken).ConfigureAwait(false);
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

    [McpServerTool(
        Name = ToolNames.DdlHistory,
        Title = "DDL History",
        ReadOnly = false,
        Idempotent = true,
        Destructive = false),
        Description("Internal tool of the MSSQL-MCP editor extension: per-database DDL history from dbo.DDL_AuditLog (filled by the DDL_Audit database trigger). action: status | install | list | get. install creates only the missing table and trigger, never changes existing ones, and is refused on read-only connections.")]
    public async Task<DbOperationResult> DdlHistory(
        [Description("status, install, list (needs name) or get (needs id).")] string action,
        [Description("list: schema of the object; entries without a schema are included too.")] string? schema = null,
        [Description("list: object name.")] string? name = null,
        [Description("get: the audit entry ID.")] int? id = null,
        [Description("list: maximum entries, newest first (default 100, clamped to 1..500).")] int top = DdlAudit.DefaultTop,
        [Description(Tools.ConnectionParamDescription)] string? connection = null,
        CancellationToken cancellationToken = default)
    {
        var verb = action?.Trim().ToLowerInvariant();
        if (verb is not ("status" or "install" or "list" or "get"))
        {
            return new DbOperationResult(success: false, error: "action must be status, install, list or get.");
        }

        if (verb == "list" && string.IsNullOrWhiteSpace(name))
        {
            return new DbOperationResult(success: false, error: "list requires name.");
        }

        if (verb == "get" && id is null)
        {
            return new DbOperationResult(success: false, error: "get requires id.");
        }

        // Unrouted calls (no profile) are treated as read-only, like run_script.
        var profile = CurrentConnection.Value;
        var readOnly = profile?.ReadOnly ?? true;
        if (verb == "install" && readOnly)
        {
            return new DbOperationResult(
                success: false,
                error: $"Connection '{profile?.Name}' is read-only; the DDL history table and trigger can only be created on a read/write connection.");
        }

        try
        {
            var conn = await connectionFactory.GetOpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using (conn.ConfigureAwait(false))
            {
                switch (verb)
                {
                    case "status":
                        return new DbOperationResult(success: true, data: await DdlAudit.GetStatusAsync(conn, readOnly, cancellationToken).ConfigureAwait(false));
                    case "install":
                        return new DbOperationResult(success: true, data: await DdlAudit.InstallAsync(conn, cancellationToken).ConfigureAwait(false));
                    case "list":
                        var entries = await DdlAudit
                            .ListAsync(conn, string.IsNullOrWhiteSpace(schema) ? null : schema, name!, top, cancellationToken)
                            .ConfigureAwait(false);
                        return entries is null
                            ? new DbOperationResult(success: false, error: DdlAudit.NotInstalledError)
                            : new DbOperationResult(success: true, data: entries);
                    default:
                        var (tableExists, command) = await DdlAudit.GetAsync(conn, id!.Value, cancellationToken).ConfigureAwait(false);
                        if (!tableExists)
                        {
                            return new DbOperationResult(success: false, error: DdlAudit.NotInstalledError);
                        }

                        return command is null
                            ? new DbOperationResult(success: false, error: $"No DDL history entry with id {id}.")
                            : new DbOperationResult(success: true, data: command);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "{Tool} {Action} failed: {Message}", ToolNames.DdlHistory, verb, ex.Message);
            return new DbOperationResult(success: false, error: ex.Message);
        }
    }
}
