// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.

using Microsoft.Data.SqlClient;

namespace MssqlMcp.Tests;

/// <summary>
/// Ensures integration tests have a connection string when none is provided (e.g. local dev / CI agents).
/// The LocalDB fallback uses a dedicated scratch database so tests never create tables in <c>master</c>.
/// </summary>
internal static class TestConnectionString
{
    private const string TestDatabase = "MssqlMcpTests";
    private const string LocalDbServer = "Server=(localdb)\\MSSQLLocalDB;Integrated Security=true;TrustServerCertificate=True";

    static TestConnectionString()
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CONNECTION_STRING")))
        {
            return;
        }

        try
        {
            using var conn = new SqlConnection($"{LocalDbServer};Initial Catalog=master");
            conn.Open();
            using var cmd = new SqlCommand($"IF DB_ID(N'{TestDatabase}') IS NULL CREATE DATABASE [{TestDatabase}];", conn);
            cmd.ExecuteNonQuery();
        }
        catch (SqlException)
        {
            // No LocalDB on this agent: DB-backed tests fail or skip on their own connection attempt.
        }

        Environment.SetEnvironmentVariable("CONNECTION_STRING", $"{LocalDbServer};Initial Catalog={TestDatabase}");
    }

    public static void EnsureInitialized() => _ = typeof(TestConnectionString);

    public static Mssql.McpServer.Connections.ConnectionRegistry CreateRegistry()
    {
        EnsureInitialized();
        return new Mssql.McpServer.Connections.ConnectionRegistry(
            Mssql.McpServer.Connections.ConnectionConfigLoader.Load(Environment.GetEnvironmentVariable, File.ReadAllText));
    }

    public static Mssql.McpServer.SqlConnectionFactory CreateFactory(Mssql.McpServer.Connections.ConnectionRegistry? registry = null) =>
        new(registry ?? CreateRegistry());
}
