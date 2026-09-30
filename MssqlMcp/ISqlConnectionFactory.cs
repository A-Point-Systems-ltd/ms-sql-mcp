// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.

using Microsoft.Data.SqlClient;

namespace Mssql.McpServer;

/// <summary>
/// Defines a factory interface for creating SQL database connections.
/// </summary>
public interface ISqlConnectionFactory
{
    Task<SqlConnection> GetOpenConnectionAsync() => GetOpenConnectionAsync(CancellationToken.None);

    /// <summary>Opens a pooled connection; the caller owns and disposes it.</summary>
    Task<SqlConnection> GetOpenConnectionAsync(CancellationToken cancellationToken);
}
