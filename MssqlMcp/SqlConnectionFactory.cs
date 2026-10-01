// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.

using Microsoft.Data.SqlClient;
using Mssql.McpServer.Connections;

namespace Mssql.McpServer;

public class SqlConnectionFactory(ConnectionRegistry registry) : ISqlConnectionFactory
{
    public Task<SqlConnection> GetOpenConnectionAsync(CancellationToken cancellationToken) =>
        OpenAsync(CurrentProfile().ConnectionString, cancellationToken);

    public Task<SqlConnection> GetOpenUnpooledConnectionAsync(CancellationToken cancellationToken)
    {
        var builder = new SqlConnectionStringBuilder(CurrentProfile().ConnectionString) { Pooling = false };
        return OpenAsync(builder.ConnectionString, cancellationToken);
    }

    // The routing filter binds CurrentConnection per tool call; background work binds it per connection.
    private ConnectionProfile CurrentProfile() => CurrentConnection.Value ?? registry.Resolve(null);

    private static async Task<SqlConnection> OpenAsync(string connectionString, CancellationToken cancellationToken)
    {
        var conn = new SqlConnection(connectionString);
        try
        {
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
            return conn;
        }
        catch
        {
            await conn.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
