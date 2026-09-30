// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.

using Microsoft.Data.SqlClient;
using Mssql.McpServer.Connections;

namespace Mssql.McpServer;

public class SqlConnectionFactory(ConnectionRegistry registry) : ISqlConnectionFactory
{
    public async Task<SqlConnection> GetOpenConnectionAsync(CancellationToken cancellationToken)
    {
        // The routing filter binds CurrentConnection per tool call; background work binds it per connection.
        var profile = CurrentConnection.Value ?? registry.Resolve(null);
        var conn = new SqlConnection(profile.ConnectionString);
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
