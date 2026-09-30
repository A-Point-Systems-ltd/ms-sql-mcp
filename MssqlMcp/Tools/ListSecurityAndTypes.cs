// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.

using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Mssql.McpServer.Scripting;

namespace Mssql.McpServer;

/// <summary>
/// list_objects support for database triggers, user-defined types and security principals. Read-only sys.* queries;
/// low-privilege logins simply see fewer rows. No password, hash or SID is ever selected.
/// </summary>
public partial class Tools
{
    private const string ListDatabaseTriggersQuery = @"SELECT t.name, t.is_disabled,
            STUFF((SELECT ', ' + te.type_desc FROM sys.trigger_events te WHERE te.object_id = t.object_id FOR XML PATH(''), TYPE).value('.', 'nvarchar(max)'), 1, 2, '') AS events
        FROM sys.triggers t
        WHERE t.parent_class = 0 AND (@NamePattern IS NULL OR t.name LIKE @NamePattern)
        ORDER BY t.name";

    private const string ListTypesQuery = @"SELECT SCHEMA_NAME(t.schema_id) AS [schema], t.name,
            CASE WHEN t.is_table_type = 1 THEN 'Table' WHEN t.is_assembly_type = 1 THEN 'Clr' ELSE 'Alias' END AS kind,
            TYPE_NAME(t.system_type_id) AS base_type
        FROM sys.types t
        WHERE t.is_user_defined = 1
            AND (@NamePattern IS NULL OR t.name LIKE @NamePattern OR (SCHEMA_NAME(t.schema_id) + '.' + t.name) LIKE @NamePattern)
        ORDER BY SCHEMA_NAME(t.schema_id), t.name";

    private const string ListLoginsQuery = @"SELECT sp.name, sp.type_desc AS type, sp.is_disabled, sp.default_database_name
        FROM sys.server_principals sp
        WHERE sp.type IN ('S','U','G') AND sp.name NOT LIKE '##%'
            AND (@NamePattern IS NULL OR sp.name LIKE @NamePattern)
        ORDER BY sp.name";

    // is_fixed_role on sys.server_principals exists from 11.0; before that every server role is fixed.
    private const string ListServerRolesQueryTemplate = @"SELECT sp.name, {0} AS is_fixed,
            (SELECT COUNT(*) FROM sys.server_role_members m WHERE m.role_principal_id = sp.principal_id) AS member_count
        FROM sys.server_principals sp
        WHERE sp.type = 'R' AND (@NamePattern IS NULL OR sp.name LIKE @NamePattern)
        ORDER BY sp.name";

    private const string ListDatabaseUsersQuery = @"SELECT dp.name, dp.type_desc AS type, sp.name AS login_name, dp.default_schema_name
        FROM sys.database_principals dp
        LEFT JOIN sys.server_principals sp ON sp.sid = dp.sid AND dp.sid IS NOT NULL
        WHERE dp.type IN ('S','U','G','E','X') AND dp.principal_id > 4
            AND (@NamePattern IS NULL OR dp.name LIKE @NamePattern)
        ORDER BY dp.name";

    private const string ListDatabaseRolesQuery = @"SELECT dp.name,
            CONVERT(bit, CASE WHEN dp.is_fixed_role = 1 OR dp.principal_id = 0 THEN 1 ELSE 0 END) AS is_fixed,
            CONVERT(bit, CASE WHEN dp.type = 'A' THEN 1 ELSE 0 END) AS is_application_role,
            o.name AS owner,
            (SELECT COUNT(*) FROM sys.database_role_members m WHERE m.role_principal_id = dp.principal_id) AS member_count
        FROM sys.database_principals dp
        LEFT JOIN sys.database_principals o ON o.principal_id = dp.owning_principal_id
        WHERE dp.type IN ('R','A') AND (@NamePattern IS NULL OR dp.name LIKE @NamePattern)
        ORDER BY dp.name";

    private Task<DbOperationResult> ListDatabaseTriggers(string? partialName, CancellationToken ct) =>
        RunCatalogListAsync("DatabaseTrigger", _ => ListDatabaseTriggersQuery, partialName,
            r => new Dictionary<string, object?> { ["name"] = r[0], ["isDisabled"] = r[1], ["events"] = r[2] }, ct);

    private Task<DbOperationResult> ListUserDefinedTypes(string? partialName, CancellationToken ct) =>
        RunCatalogListAsync("Type", _ => ListTypesQuery, partialName,
            r => new Dictionary<string, object?> { ["schema"] = r[0], ["name"] = r[1], ["kind"] = r[2], ["baseType"] = r[3] }, ct);

    private Task<DbOperationResult> ListLogins(string? partialName, CancellationToken ct) =>
        RunCatalogListAsync("Login", _ => ListLoginsQuery, partialName,
            r => new Dictionary<string, object?> { ["name"] = r[0], ["type"] = r[1], ["isDisabled"] = r[2], ["defaultDatabase"] = r[3] }, ct);

    private Task<DbOperationResult> ListServerRoles(string? partialName, CancellationToken ct) =>
        RunCatalogListAsync("ServerRole",
            v => string.Format(System.Globalization.CultureInfo.InvariantCulture, ListServerRolesQueryTemplate, v.Major >= 11 ? "sp.is_fixed_role" : "CONVERT(bit, 1)"),
            partialName,
            r => new Dictionary<string, object?> { ["name"] = r[0], ["isFixed"] = r[1], ["memberCount"] = r[2] }, ct);

    private Task<DbOperationResult> ListDatabaseUsers(string? partialName, CancellationToken ct) =>
        RunCatalogListAsync("DatabaseUser", _ => ListDatabaseUsersQuery, partialName,
            r => new Dictionary<string, object?> { ["name"] = r[0], ["type"] = r[1], ["loginName"] = r[2], ["defaultSchema"] = r[3] }, ct);

    private Task<DbOperationResult> ListDatabaseRoles(string? partialName, CancellationToken ct) =>
        RunCatalogListAsync("DatabaseRole", _ => ListDatabaseRolesQuery, partialName,
            r => new Dictionary<string, object?> { ["name"] = r[0], ["isFixed"] = r[1], ["isApplicationRole"] = r[2], ["owner"] = r[3], ["memberCount"] = r[4] }, ct);

    private async Task<DbOperationResult> RunCatalogListAsync(
        string label,
        Func<SqlServerVersion, string> buildQuery,
        string? partialName,
        Func<object?[], Dictionary<string, object?>> map,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var conn = await _connectionFactory.GetOpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            var version = await CatalogReader.GetVersionAsync(conn, cancellationToken).ConfigureAwait(false);
            await using var cmd = new SqlCommand(buildQuery(version), conn);
            AddNamePatternParameter(cmd, partialName);
            var rows = new List<Dictionary<string, object?>>();
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            var values = new object?[reader.FieldCount];
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                for (var i = 0; i < values.Length; i++)
                {
                    values[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                }

                rows.Add(map(values));
            }

            return new DbOperationResult(success: true, data: rows);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "{Tool} ({Label}) failed: {Message}", ToolNames.ListObjects, label, ex.Message);
            return new DbOperationResult(success: false, error: ex.Message);
        }
    }
}
