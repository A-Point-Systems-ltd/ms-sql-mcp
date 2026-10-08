using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using Mssql.McpServer.Connections;
using Mssql.McpServer.Connections.Managed;

namespace MssqlMcp.Tests.Connections.Managed;

/// <summary>
/// The connections view's Test / List databases / Bring online against throwaway LocalDB databases (skipped without
/// LocalDB). Every database taken offline here is brought back in a finally block.
/// </summary>
[Collection(DatabaseStateCollection.Name)]
public sealed class ManagedConnectionServiceDbTests : IDisposable
{
    private const string LocalDb = "(localdb)\\MSSQLLocalDB";
    private const string Master = "Server=(localdb)\\MSSQLLocalDB;Integrated Security=true;TrustServerCertificate=True;Initial Catalog=master";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mssqlmcp-managed-db-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private ManagedConnectionService CreateService()
    {
        var service = new ManagedConnectionService(
            new ManagedConnectionStore(Path.Combine(_dir, "connections.json")), new ConnectionRegistry([]), new FakeProtector(),
            NullLogger<ManagedConnectionService>.Instance);
        service.Sync();
        return service;
    }

    private static ManagedConnectionInput Windows(string database, bool readOnly = true) => new()
    {
        Name = "t", Auth = ManagedAuth.Windows, Server = LocalDb, Database = database,
        Encrypt = ManagedEncrypt.Optional, TrustServerCertificate = true, ReadOnly = readOnly,
    };

    [SkippableFact]
    public async Task Test_on_an_offline_database_reports_its_state_and_read_only_bring_online_fixes_it()
    {
        await using var scratch = await ScratchDatabases.CreateAsync(1);
        var name = scratch.Names[0];
        var service = CreateService();
        try
        {
            await SetOfflineAsync(name);

            var test = await service.TestAsync(Windows(name), isNew: true, CancellationToken.None);
            Assert.False(test.Success);
            Assert.Equal(DatabaseState.Offline, test.DatabaseState);
            Assert.StartsWith($"Database '{name}' is OFFLINE.", test.Message, StringComparison.Ordinal);

            // An empty Database field still lists (through master), with the offline database and its state.
            var list = await service.ListDatabasesAsync(Windows(""), isNew: true, CancellationToken.None);
            Assert.True(list.Success, list.Message);
            Assert.Contains(list.Databases!, d => d.Name == name && d.State == DatabaseState.Offline);
            Assert.Contains("not online", list.Message, StringComparison.Ordinal);

            // Read-only input: the flag restricts the model, not the person at the form.
            var online = await service.BringOnlineAsync(Windows(name, readOnly: true), isNew: true, CancellationToken.None);
            Assert.True(online.Success, online.Message);
            Assert.Equal(DatabaseState.Online, online.DatabaseState);

            Assert.True((await service.TestAsync(Windows(name), isNew: true, CancellationToken.None)).Success);
        }
        finally
        {
            await BringOnlineQuietlyAsync(name);
        }
    }

    [SkippableFact]
    public async Task Bring_online_with_a_raw_connection_string_targets_its_initial_catalog()
    {
        await using var scratch = await ScratchDatabases.CreateAsync(1);
        var name = scratch.Names[0];
        var service = CreateService();
        try
        {
            await SetOfflineAsync(name);
            var raw = new ManagedConnectionInput
            {
                Name = "t", Auth = ManagedAuth.Raw,
                RawConnectionString = $"Server={LocalDb};Initial Catalog={name};Integrated Security=true;TrustServerCertificate=True",
            };

            Assert.Equal(DatabaseState.Offline, (await service.TestAsync(raw, isNew: true, CancellationToken.None)).DatabaseState);
            var r = await service.BringOnlineAsync(raw, isNew: true, CancellationToken.None);
            Assert.True(r.Success, r.Message);
            Assert.Equal(DatabaseState.Online, await DatabaseStateOps.GetStateAsync(Master, name, CancellationToken.None));
        }
        finally
        {
            await BringOnlineQuietlyAsync(name);
        }
    }

    [SkippableFact]
    public async Task A_failed_login_does_not_look_up_the_database_state()
    {
        await using var scratch = await ScratchDatabases.CreateAsync(1);
        var service = CreateService();
        var badLogin = new ManagedConnectionInput
        {
            Name = "t", Auth = ManagedAuth.Sql, Server = LocalDb, Database = scratch.Names[0], User = "no_such_login_" + Guid.NewGuid().ToString("N")[..8],
            Password = "wrong", Encrypt = ManagedEncrypt.Optional, TrustServerCertificate = true,
        };

        var r = await service.TestAsync(badLogin, isNew: true, CancellationToken.None);
        Assert.False(r.Success);
        Assert.Null(r.DatabaseState);
        Assert.DoesNotContain("is OFFLINE", r.Message, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task Opening_an_offline_database_raises_a_database_unavailable_error()
    {
        await using var scratch = await ScratchDatabases.CreateAsync(1);
        var name = scratch.Names[0];
        try
        {
            await SetOfflineAsync(name);
            await using var conn = new SqlConnection(scratch.ConnectionStrings[0]);
            var ex = await Assert.ThrowsAsync<SqlException>(() => conn.OpenAsync());
            Assert.True(DatabaseStateOps.IsDatabaseUnavailable(ex), string.Join(", ", ex.Errors.Cast<SqlError>().Select(e => e.Number)));
        }
        finally
        {
            await BringOnlineQuietlyAsync(name);
        }
    }

    private static string Quote(string name) => "[" + name.Replace("]", "]]", StringComparison.Ordinal) + "]";

    private static Task SetOfflineAsync(string name)
    {
        SqlConnection.ClearAllPools();
        return ScratchDatabases.ExecAsync(Master, $"ALTER DATABASE {Quote(name)} SET OFFLINE WITH ROLLBACK IMMEDIATE;");
    }

    private static async Task BringOnlineQuietlyAsync(string name)
    {
        try
        {
            await ScratchDatabases.ExecAsync(Master,
                $"IF EXISTS (SELECT 1 FROM sys.databases WHERE name = N'{name}' AND state_desc = 'OFFLINE') ALTER DATABASE {Quote(name)} SET ONLINE;");
        }
        catch (SqlException)
        {
            // Best effort: the scratch drop still runs.
        }
    }
}
