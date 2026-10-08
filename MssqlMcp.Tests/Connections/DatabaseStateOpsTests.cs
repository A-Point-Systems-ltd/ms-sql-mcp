using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using Mssql.McpServer.Connections;

namespace MssqlMcp.Tests.Connections;

/// <summary>Against throwaway LocalDB databases; skipped when LocalDB is not available.</summary>
[Collection(DatabaseStateCollection.Name)]
public sealed class DatabaseStateOpsTests
{
    private const string Server = "Server=(localdb)\\MSSQLLocalDB;Integrated Security=true;TrustServerCertificate=True";

    [SkippableFact]
    public async Task Lists_states_and_brings_an_offline_database_online()
    {
        await using var scratch = await ScratchDatabases.CreateAsync(1);
        var name = scratch.Names[0];
        var cs = scratch.ConnectionStrings[0];
        try
        {
            await SetOfflineAsync(name);

            var list = await DatabaseStateOps.ListAsync(cs, CancellationToken.None);
            Assert.Contains(list, d => d.Name == name && d.State == DatabaseState.Offline);
            Assert.Contains(list, d => d.Name == "master" && d.State == DatabaseState.Online);
            Assert.Equal(DatabaseState.Offline, await DatabaseStateOps.GetStateAsync(cs, name, CancellationToken.None));

            // The profile's own database is offline: everything goes through master.
            var r = await DatabaseStateOps.BringOnlineAsync(cs, name, NullLogger.Instance, CancellationToken.None);
            Assert.True(r.Success, r.Message);
            Assert.Equal(DatabaseState.Online, r.State);
            Assert.Equal(DatabaseState.Online, await DatabaseStateOps.GetStateAsync(cs, name, CancellationToken.None));
        }
        finally
        {
            await BringOnlineQuietlyAsync(name);
        }
    }

    [SkippableFact]
    public async Task Check_open_classifies_offline_and_online_databases()
    {
        await using var scratch = await ScratchDatabases.CreateAsync(1);
        var name = scratch.Names[0];
        var cs = scratch.ConnectionStrings[0];
        try
        {
            Assert.Equal(new OpenCheckResult(true, null, false, DatabaseState.Online), await DatabaseStateOps.CheckOpenAsync(cs, CancellationToken.None));

            await SetOfflineAsync(name);
            var offline = await DatabaseStateOps.CheckOpenAsync(cs, CancellationToken.None);
            Assert.False(offline.Ok);
            Assert.True(offline.DatabaseUnavailable);
            Assert.Equal(DatabaseState.Offline, offline.State);
            Assert.False(string.IsNullOrEmpty(offline.Message));

            var missing = await DatabaseStateOps.CheckOpenAsync(
                new SqlConnectionStringBuilder(cs) { InitialCatalog = "NoSuchDb_" + Guid.NewGuid().ToString("N")[..8] }.ConnectionString, CancellationToken.None);
            Assert.False(missing.Ok);
            Assert.Null(missing.State); // unknown database: no state to offer, and nothing to bring online
        }
        finally
        {
            await BringOnlineQuietlyAsync(name);
        }
    }

    [SkippableFact]
    public async Task Refuses_a_database_that_is_not_offline_and_an_unknown_one()
    {
        await using var scratch = await ScratchDatabases.CreateAsync(1);
        var cs = scratch.ConnectionStrings[0];

        var online = await DatabaseStateOps.BringOnlineAsync(cs, scratch.Names[0], NullLogger.Instance, CancellationToken.None);
        Assert.False(online.Success);
        Assert.Contains("not OFFLINE", online.Message, StringComparison.Ordinal);
        Assert.Equal(DatabaseState.Online, online.State);

        var missing = await DatabaseStateOps.BringOnlineAsync(cs, "NoSuchDb_" + Guid.NewGuid().ToString("N"), NullLogger.Instance, CancellationToken.None);
        Assert.False(missing.Success);
        Assert.Contains("not found", missing.Message, StringComparison.Ordinal);
        Assert.Null(await DatabaseStateOps.GetStateAsync(cs, "NoSuchDb_x", CancellationToken.None));
    }

    [SkippableFact]
    public async Task Quotes_names_with_brackets_spaces_and_quotes()
    {
        var name = $"Mcp Odd]Name'{Guid.NewGuid():N}"[..30];
        var cs = await CreateRawAsync(name);
        try
        {
            await SetOfflineAsync(name);
            var r = await DatabaseStateOps.BringOnlineAsync(cs, name, NullLogger.Instance, CancellationToken.None);
            Assert.True(r.Success, r.Message);
            Assert.Equal(DatabaseState.Online, await DatabaseStateOps.GetStateAsync(cs, name, CancellationToken.None));
        }
        finally
        {
            await BringOnlineQuietlyAsync(name);
            await ScratchDatabases.ExecAsync($"{Server};Initial Catalog=master", $"DROP DATABASE IF EXISTS {Quote(name)};");
        }
    }

    [SkippableFact]
    public async Task Listing_uses_the_own_database_and_falls_back_to_master_only_when_it_cannot_be_opened()
    {
        await using var scratch = await ScratchDatabases.CreateAsync(1);
        var cs = scratch.ConnectionStrings[0];

        // Online catalog: listed through it (what a contained-database user needs; such a user cannot log in to master).
        Assert.Contains(await DatabaseStateOps.ListAsync(cs, CancellationToken.None), d => d.Name == scratch.Names[0]);

        // A catalog that does not exist (4060) falls back to master.
        var missing = new SqlConnectionStringBuilder(cs) { InitialCatalog = "NoSuchDb_" + Guid.NewGuid().ToString("N")[..8] }.ConnectionString;
        Assert.Contains(await DatabaseStateOps.ListAsync(missing, CancellationToken.None), d => d.Name == "master");

        // A failed login is not retried against master.
        var badLogin = new SqlConnectionStringBuilder(cs) { IntegratedSecurity = false, UserID = "no_such_login_x", Password = "wrong" }.ConnectionString;
        await Assert.ThrowsAsync<SqlException>(() => DatabaseStateOps.ListAsync(badLogin, CancellationToken.None));
    }

    [Fact]
    public void For_master_switches_the_catalog_and_disables_pooling()
    {
        var b = new SqlConnectionStringBuilder(DatabaseStateOps.ForMaster("Server=s;Initial Catalog=Sales;Integrated Security=true", 7));
        Assert.Equal("master", b.InitialCatalog);
        Assert.False(b.Pooling);
        Assert.Equal(7, b.ConnectTimeout);
        Assert.Equal("s", b.DataSource);
    }

    private static string Quote(string name) => "[" + name.Replace("]", "]]", StringComparison.Ordinal) + "]";

    private static async Task<string> CreateRawAsync(string name)
    {
        try
        {
            await ScratchDatabases.ExecAsync($"{Server};Initial Catalog=master", $"CREATE DATABASE {Quote(name)};");
        }
        catch (SqlException ex)
        {
            throw new SkipException($"LocalDB unavailable: {ex.Message}");
        }

        return new SqlConnectionStringBuilder(Server) { InitialCatalog = name }.ConnectionString;
    }

    private static Task SetOfflineAsync(string name)
    {
        SqlConnection.ClearAllPools();
        return ScratchDatabases.ExecAsync($"{Server};Initial Catalog=master", $"ALTER DATABASE {Quote(name)} SET OFFLINE WITH ROLLBACK IMMEDIATE;");
    }

    private static async Task BringOnlineQuietlyAsync(string name)
    {
        try
        {
            await ScratchDatabases.ExecAsync($"{Server};Initial Catalog=master",
                $"IF EXISTS (SELECT 1 FROM sys.databases WHERE name = N'{name.Replace("'", "''", StringComparison.Ordinal)}' AND state_desc = 'OFFLINE') ALTER DATABASE {Quote(name)} SET ONLINE;");
        }
        catch (SqlException)
        {
            // Best effort: the scratch drop still runs.
        }
    }
}
