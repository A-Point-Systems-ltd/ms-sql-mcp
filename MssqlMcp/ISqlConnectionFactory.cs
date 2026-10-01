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

    /// <summary>
    /// Opens a connection outside the pool (<c>Pooling=false</c>), so it is a new session that no earlier caller's
    /// session state (isolation level, application role, EXECUTE AS) can reach, and closing it ends the session.
    /// Only for <c>run_script</c>, which runs arbitrary session-level statements. The caller owns and disposes it.
    /// </summary>
    Task<SqlConnection> GetOpenUnpooledConnectionAsync(CancellationToken cancellationToken);
}
