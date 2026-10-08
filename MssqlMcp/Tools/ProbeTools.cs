using System.ComponentModel;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using Mssql.McpServer.Connections;

namespace Mssql.McpServer;

/// <summary>
/// The VS Code extension's connection-form probe tools: list databases with their state, read one database's state,
/// and bring an OFFLINE database online after the user confirmed it in the form. Deliberately not an
/// <see cref="McpServerToolTypeAttribute"/> class: <c>WithToolsFromAssembly</c> must not list them to agents; Program
/// registers them only when MSSQL_PROBE_TOOLS=true, which only the extension's short-lived probe process sets.
/// Every tool reaches the server through master, so the bound connection's own database may be offline.
/// </summary>
public sealed class ProbeTools(ILogger<ProbeTools> logger)
{
    internal const string EnableVariable = "MSSQL_PROBE_TOOLS";

    internal static bool IsEnabled(Func<string, string?> getEnvironmentVariable) =>
        string.Equals(getEnvironmentVariable(EnableVariable), "true", StringComparison.OrdinalIgnoreCase);

    [McpServerTool(Name = ToolNames.ProbeListDatabases, Title = "Probe: list databases", ReadOnly = true, Idempotent = true, Destructive = false),
        Description("Internal tool of the MSSQL-MCP connection form: every database of the connection's server with its state (ONLINE, OFFLINE, ...).")]
    public async Task<DbOperationResult> ListDatabases(
        [Description(Tools.ConnectionParamDescription)] string? connection = null,
        CancellationToken cancellationToken = default) =>
        await RunAsync(async cs => new DbOperationResult(true, data: await DatabaseStateOps.ListAsync(cs, cancellationToken).ConfigureAwait(false)))
            .ConfigureAwait(false);

    [McpServerTool(Name = ToolNames.ProbeDatabaseState, Title = "Probe: database state", ReadOnly = true, Idempotent = true, Destructive = false),
        Description("Internal tool of the MSSQL-MCP connection form: the state of one database (data = {name, state}; state is null when not found).")]
    public async Task<DbOperationResult> DatabaseState(
        [Description("Database name; defaults to the connection's database.")] string? database = null,
        [Description(Tools.ConnectionParamDescription)] string? connection = null,
        CancellationToken cancellationToken = default) =>
        await RunAsync(async cs =>
        {
            var name = Target(cs, database);
            var state = await DatabaseStateOps.GetStateAsync(cs, name, cancellationToken).ConfigureAwait(false);
            return new DbOperationResult(true, data: new { name, state });
        }).ConfigureAwait(false);

    [McpServerTool(Name = ToolNames.ProbeBringOnline, Title = "Probe: bring database online", ReadOnly = false, Idempotent = true, Destructive = false),
        Description("Internal tool of the MSSQL-MCP connection form: ALTER DATABASE ... SET ONLINE for an OFFLINE database, after the user confirmed it.")]
    public async Task<DbOperationResult> BringOnline(
        [Description("Database name; defaults to the connection's database.")] string? database = null,
        [Description(Tools.ConnectionParamDescription)] string? connection = null,
        CancellationToken cancellationToken = default) =>
        await RunAsync(async cs =>
        {
            var name = Target(cs, database);
            var r = await DatabaseStateOps.BringOnlineAsync(cs, name, logger, cancellationToken).ConfigureAwait(false);
            return new DbOperationResult(r.Success, r.Success ? null : r.Message, data: new { name, state = r.State, message = r.Message });
        }).ConfigureAwait(false);

    private static string Target(string connectionString, string? database) =>
        string.IsNullOrWhiteSpace(database) ? new SqlConnectionStringBuilder(connectionString).InitialCatalog : database.Trim();

    private async Task<DbOperationResult> RunAsync(Func<string, Task<DbOperationResult>> action)
    {
        if (CurrentConnection.Value is not { } profile)
        {
            return new DbOperationResult(false, "No connection is bound to this call.");
        }

        try
        {
            return await action(profile.ConnectionString).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException or ArgumentException)
        {
            logger.LogWarning("Probe tool failed: {Error}", ex.Message);
            return new DbOperationResult(false, ex.Message);
        }
    }
}
