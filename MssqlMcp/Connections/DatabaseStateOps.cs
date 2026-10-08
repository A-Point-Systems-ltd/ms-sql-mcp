using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace Mssql.McpServer.Connections;

/// <summary>One database of a server and its <c>sys.databases.state_desc</c> (ONLINE, OFFLINE, RESTORING, ...).</summary>
public sealed record DatabaseState(string Name, string State)
{
    public const string Online = "ONLINE";
    public const string Offline = "OFFLINE";
}

/// <summary>
/// Database listing and the "bring online" action for the connection forms (the Claude Desktop connections view and
/// the VS Code extension's probe process). Every call goes through <c>master</c> on an unpooled connection, because the
/// form's own database may be the offline one. Only ever reached from a human action in a form - never by the model.
/// </summary>
internal static class DatabaseStateOps
{
    /// <summary>SET ONLINE runs crash recovery, which can take minutes on a large log.</summary>
    internal const int BringOnlineTimeoutSeconds = 300;

    private const string StateSql = "SELECT state_desc FROM sys.databases WHERE name = @db";

    // QUOTENAME on the server: the name never becomes part of SQL text built in C#.
    private const string BringOnlineSql =
        "DECLARE @s nvarchar(max) = N'ALTER DATABASE ' + QUOTENAME(@db) + N' SET ONLINE'; EXEC (@s);";

    /// <summary>
    /// SQL Server errors meaning "the login worked but this database cannot be opened": 4060 cannot open database,
    /// 942 offline, 922 being recovered, 927 restoring. A failed login (18456) is not one of them.
    /// </summary>
    internal static readonly IReadOnlySet<int> DatabaseUnavailableErrors = new HashSet<int> { 4060, 942, 922, 927 };

    internal static bool IsDatabaseUnavailable(SqlException ex) =>
        ex.Errors.Cast<SqlError>().Any(e => DatabaseUnavailableErrors.Contains(e.Number));

    /// <summary>The same server and credentials, on master, without pooling.</summary>
    internal static string ForMaster(string connectionString, int? connectTimeoutSeconds = null)
    {
        var builder = new SqlConnectionStringBuilder(connectionString) { InitialCatalog = "master", Pooling = false };
        if (connectTimeoutSeconds is { } t)
        {
            builder.ConnectTimeout = t;
        }

        return builder.ConnectionString;
    }

    /// <summary>Every database of the server with its state, by name.</summary>
    public static async Task<IReadOnlyList<DatabaseState>> ListAsync(string connectionString, CancellationToken ct)
    {
        await using var conn = new SqlConnection(ForMaster(connectionString));
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new SqlCommand("SELECT name, state_desc FROM sys.databases ORDER BY name", conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var list = new List<DatabaseState>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(new DatabaseState(reader.GetString(0), reader.GetString(1)));
        }

        return list;
    }

    /// <summary>
    /// Opens the connection's own database once (unpooled). On failure, and only when the error says the database
    /// cannot be opened (<see cref="DatabaseUnavailableErrors"/>), reads its state through master; a failed login is
    /// not retried, so a wrong password counts once toward a lockout policy. Classified by error number, so it works
    /// whatever the server's message language.
    /// </summary>
    public static async Task<OpenCheckResult> CheckOpenAsync(string connectionString, CancellationToken ct)
    {
        var builder = new SqlConnectionStringBuilder(connectionString) { Pooling = false };
        try
        {
            await using var conn = new SqlConnection(builder.ConnectionString);
            await conn.OpenAsync(ct).ConfigureAwait(false);
            return new OpenCheckResult(true, null, false, DatabaseState.Online);
        }
        catch (SqlException ex)
        {
            if (!IsDatabaseUnavailable(ex) || string.IsNullOrWhiteSpace(builder.InitialCatalog))
            {
                return new OpenCheckResult(false, ex.Message, false, null);
            }

            string? state;
            try
            {
                state = await GetStateAsync(connectionString, builder.InitialCatalog, ct).ConfigureAwait(false);
            }
            catch (SqlException)
            {
                state = null; // master is not reachable for this login: the original error says enough
            }

            return new OpenCheckResult(false, ex.Message, true, state);
        }
    }

    /// <summary>The database's state_desc, or null when no database has that name (or it is not visible to the login).</summary>
    public static async Task<string?> GetStateAsync(string connectionString, string database, CancellationToken ct)
    {
        await using var conn = new SqlConnection(ForMaster(connectionString));
        await conn.OpenAsync(ct).ConfigureAwait(false);
        return await ReadStateAsync(conn, database, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs <c>ALTER DATABASE ... SET ONLINE</c> when, and only when, the database is OFFLINE. Any other state
    /// (RESTORING, RECOVERING, SUSPECT, EMERGENCY, ONLINE) is reported and left untouched.
    /// </summary>
    /// <returns>Success and the state read back afterwards; SQL Server's own message on failure (e.g. missing permission).</returns>
    public static async Task<BringOnlineResult> BringOnlineAsync(string connectionString, string database, ILogger logger, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(database))
        {
            return new BringOnlineResult(false, "A database name is required.", null);
        }

        // Read-only profiles carry ApplicationIntent=ReadOnly, which a listener could route to a readable secondary.
        var masterConnectionString = new SqlConnectionStringBuilder(ForMaster(connectionString))
        {
            ApplicationIntent = ApplicationIntent.ReadWrite,
        }.ConnectionString;
        var server = new SqlConnectionStringBuilder(masterConnectionString).DataSource;
        await using var conn = new SqlConnection(masterConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        var before = await ReadStateAsync(conn, database, ct).ConfigureAwait(false);
        if (before is null)
        {
            return new BringOnlineResult(false, $"Database '{database}' was not found on {server}.", null);
        }

        if (!string.Equals(before, DatabaseState.Offline, StringComparison.OrdinalIgnoreCase))
        {
            return new BringOnlineResult(false, $"Database '{database}' is {before}, not OFFLINE; nothing was changed.", before);
        }

        try
        {
            await using (var cmd = new SqlCommand(BringOnlineSql, conn) { CommandTimeout = BringOnlineTimeoutSeconds })
            {
                _ = cmd.Parameters.Add(new SqlParameter("@db", System.Data.SqlDbType.NVarChar, 128) { Value = database });
                _ = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            var after = await ReadStateAsync(conn, database, ct).ConfigureAwait(false);
            if (string.Equals(after, DatabaseState.Online, StringComparison.OrdinalIgnoreCase))
            {
                logger.LogInformation("Database '{Database}' on {Server} brought online from a connection form.", database, server);
                return new BringOnlineResult(true, $"Database '{database}' is now ONLINE.", after);
            }

            logger.LogWarning("ALTER DATABASE SET ONLINE completed but '{Database}' on {Server} is {State}.", database, server, after);
            return new BringOnlineResult(false, $"ALTER DATABASE completed but '{database}' is {after}.", after);
        }
        catch (SqlException ex)
        {
            logger.LogWarning("Bringing database '{Database}' on {Server} online failed: {Error}", database, server, ex.Message);
            return new BringOnlineResult(false, ex.Message, before);
        }
    }

    private static async Task<string?> ReadStateAsync(SqlConnection conn, string database, CancellationToken ct)
    {
        await using var cmd = new SqlCommand(StateSql, conn);
        _ = cmd.Parameters.Add(new SqlParameter("@db", System.Data.SqlDbType.NVarChar, 128) { Value = database });
        return await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
    }
}

public sealed record BringOnlineResult(bool Success, string Message, string? State);

/// <param name="Ok">The database opened.</param>
/// <param name="Message">SQL Server's error when it did not.</param>
/// <param name="DatabaseUnavailable">The login worked but the database could not be opened (offline, restoring, ...).</param>
/// <param name="State">ONLINE when it opened; otherwise its state_desc when known (only looked up for DatabaseUnavailable).</param>
public sealed record OpenCheckResult(bool Ok, string? Message, bool DatabaseUnavailable, string? State);
