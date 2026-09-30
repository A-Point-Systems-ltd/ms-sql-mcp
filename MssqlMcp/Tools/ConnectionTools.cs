using System.ComponentModel;
using Microsoft.Data.SqlClient;
using ModelContextProtocol.Server;
using Mssql.McpServer.Connections;

namespace Mssql.McpServer;

public partial class Tools
{
    [McpServerTool(Name = ToolNames.ListConnections, Title = "List Connections", ReadOnly = true, Idempotent = true, Destructive = false),
        Description("Lists every database connection this server knows: name, open/closed, read-only, server and database (never credentials). " +
                    "connectionRequired=true means there is more than one connection and NO default: every other tool call MUST pass one of these names as its 'connection' argument. " +
                    "Call this first in a session, and again after " + ToolNames.OpenConnection + " / " + ToolNames.CloseConnection + ".")]
    public DbOperationResult ListConnections()
    {
        var connections = _connections.List();
        return new(success: true, data: new
        {
            connectionRequired = connections.Count > 1,
            count = connections.Count,
            connections,
        });
    }

    [McpServerTool(Name = ToolNames.OpenConnection, Title = "Open Connection", ReadOnly = false, Idempotent = true, Destructive = false, OpenWorld = true),
        Description("Opens a connection so tools can use it. Without connectionString: reopens a configured connection (see " + ToolNames.ListConnections + "). " +
                    "With connectionString: registers an ad-hoc connection - only allowed when the server runs with MSSQL_ALLOW_ADHOC_CONNECTIONS=true; ad-hoc connections are read-only unless readOnly=false. " +
                    "The connection is tested before it is registered. Adding a second connection makes the 'connection' argument mandatory on every tool - check connectionRequired in the response.")]
    public async Task<DbOperationResult> OpenConnection(
        [Description("Connection name (1-64 letters, digits, '-', '_', '.').")] string name,
        [Description("Optional full SQL Server connection string for an ad-hoc connection. Pass null to reopen a configured one.")] string? connectionString = null,
        [Description("Ad-hoc only: open as read-only (default true).")] bool readOnly = true,
        CancellationToken cancellationToken = default)
    {
        ConnectionProfile profile;
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            var existing = _connections.Find(name ?? string.Empty);
            if (existing is null)
            {
                return new DbOperationResult(false, $"No configured connection named '{name}'. Connections:{Environment.NewLine}{_connections.DescribeAll()}");
            }

            profile = existing;
        }
        else
        {
            if (!string.Equals(Environment.GetEnvironmentVariable("MSSQL_ALLOW_ADHOC_CONNECTIONS"), "true", StringComparison.OrdinalIgnoreCase))
            {
                return new DbOperationResult(false, "Ad-hoc connections are disabled. The server operator must set MSSQL_ALLOW_ADHOC_CONNECTIONS=true, or add the connection to MSSQL_CONNECTIONS.");
            }

            if (!ConnectionConfigLoader.IsValidName(name))
            {
                return new DbOperationResult(false, "Connection name is invalid. Use 1-64 letters, digits, '-', '_' or '.'.");
            }

            if (_connections.Find(name) is { Source: not ConnectionSource.Adhoc })
            {
                return new DbOperationResult(false, $"'{name}' is a configured connection and cannot be redefined ad hoc.");
            }

            profile = new ConnectionProfile(name, connectionString, readOnly, InsightsEnabled: false, ConnectionSource.Adhoc);
        }

        try
        {
            // The probe is capped at 5 s; the registered profile keeps the user's own connection string.
            var probe = new SqlConnectionStringBuilder(profile.ConnectionString);
            probe.ConnectTimeout = ProbeTimeoutSeconds(probe.ConnectTimeout);
            await using var conn = new SqlConnection(probe.ConnectionString);
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var cmd = new SqlCommand("SELECT 1", conn);
            _ = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new DbOperationResult(false, $"Connection test failed for '{profile.Name}': {ex.Message}");
        }

        var registered = _connections.Register(profile);
        return new DbOperationResult(true, data: new
        {
            connectionRequired = _connections.ConnectionArgumentRequired,
            connection = registered,
        });
    }

    /// <summary>Caps the probe timeout at 5 s; 0 means infinite in SqlClient and is capped too.</summary>
    internal static int ProbeTimeoutSeconds(int configured) => configured is > 0 and < 5 ? configured : 5;

    [McpServerTool(Name = ToolNames.CloseConnection, Title = "Close Connection", ReadOnly = false, Idempotent = true, Destructive = false),
        Description("Closes a connection: tools can no longer use it and its pooled sessions are released. Ad-hoc connections are forgotten (which can make 'connection' optional again if only one remains); " +
                    "configured ones stay listed as closed and can be reopened with " + ToolNames.OpenConnection + ". The last open connection cannot be closed.")]
    public DbOperationResult CloseConnection(
        [Description("Connection name to close.")] string name)
    {
        return _connections.TryClose(name ?? string.Empty) switch
        {
            CloseResult.Closed => new DbOperationResult(true, data: new { connectionRequired = _connections.ConnectionArgumentRequired }),
            CloseResult.LastOpen => new DbOperationResult(false, $"'{name}' is the last open connection and cannot be closed."),
            CloseResult.NotOpen => new DbOperationResult(false, $"Connection '{name}' is not open. Connections:{Environment.NewLine}{_connections.DescribeAll()}"),
            _ => new DbOperationResult(false, $"Connection '{name}' does not exist. Connections:{Environment.NewLine}{_connections.DescribeAll()}"),
        };
    }
}
