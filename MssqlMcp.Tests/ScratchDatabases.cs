// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.

using Microsoft.Data.SqlClient;

namespace MssqlMcp.Tests;

/// <summary>
/// Throwaway LocalDB databases for integration tests: created with unique names, always dropped on dispose.
/// Skips the calling test when LocalDB is not available. Never touches an existing database.
/// </summary>
internal sealed class ScratchDatabases : IAsyncDisposable
{
    private const string Server = "Server=(localdb)\\MSSQLLocalDB;Integrated Security=true;TrustServerCertificate=True";

    private ScratchDatabases(IReadOnlyList<string> names) => Names = names;

    public IReadOnlyList<string> Names { get; }

    public IReadOnlyList<string> ConnectionStrings => Names.Select(n => $"{Server};Initial Catalog={n}").ToList();

    public static async Task<ScratchDatabases> CreateAsync(int count)
    {
        var names = Enumerable.Range(0, count).Select(i => $"McpScratch{i}_{Guid.NewGuid():N}"[..24]).ToList();
        await using var master = new SqlConnection($"{Server};Initial Catalog=master");
        try
        {
            await master.OpenAsync();
        }
        catch (SqlException ex)
        {
            throw new SkipException($"LocalDB unavailable: {ex.Message}");
        }

        var created = new List<string>();
        try
        {
            foreach (var name in names)
            {
                await using var cmd = new SqlCommand($"CREATE DATABASE [{name}];", master);
                await cmd.ExecuteNonQueryAsync();
                created.Add(name);
            }
        }
        catch
        {
            await DropAsync(created);
            throw;
        }

        return new ScratchDatabases(names);
    }

    public static async Task ExecAsync(string cs, string sql)
    {
        await using var conn = new SqlConnection(cs);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    public static async Task<T> ScalarAsync<T>(string cs, string sql)
    {
        await using var conn = new SqlConnection(cs);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(sql, conn);
        return (T)(await cmd.ExecuteScalarAsync())!;
    }

    public ValueTask DisposeAsync() => new(DropAsync(Names));

    private static async Task DropAsync(IEnumerable<string> names)
    {
        SqlConnection.ClearAllPools();
        await using var master = new SqlConnection($"{Server};Initial Catalog=master");
        await master.OpenAsync();
        foreach (var name in names)
        {
            await using var cmd = new SqlCommand(
                $"IF DB_ID(N'{name}') IS NOT NULL BEGIN ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{name}]; END",
                master);
            await cmd.ExecuteNonQueryAsync();
        }
    }
}
