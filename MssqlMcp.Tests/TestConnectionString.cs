// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.

namespace MssqlMcp.Tests;

/// <summary>
/// Ensures integration tests have a connection string when none is provided (e.g. local dev / CI agents).
/// </summary>
internal static class TestConnectionString
{
    private const string LocalDbFallback =
        "Server=(localdb)\\MSSQLLocalDB;Integrated Security=true;Initial Catalog=master;TrustServerCertificate=True";

    static TestConnectionString()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CONNECTION_STRING")))
        {
            Environment.SetEnvironmentVariable("CONNECTION_STRING", LocalDbFallback);
        }
    }

    public static void EnsureInitialized() => _ = typeof(TestConnectionString);
}
