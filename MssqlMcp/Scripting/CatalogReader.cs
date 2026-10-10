// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.

using System.Data;
using Microsoft.Data.SqlClient;

namespace Mssql.McpServer.Scripting;

/// <summary>
/// Reads scripting metadata from sys.* catalog views into the renderer records. Every query runs on SQL Server 2008 R2;
/// newer catalog columns are read only after a <see cref="SqlServerVersion"/> check. Read-only, fully parameterized.
/// </summary>
internal static class CatalogReader
{
    public static async Task<SqlServerVersion> GetVersionAsync(SqlConnection conn, CancellationToken ct)
    {
        await using var cmd = new SqlCommand(
            "SELECT CONVERT(nvarchar(128), SERVERPROPERTY('ProductVersion')), CONVERT(int, SERVERPROPERTY('EngineEdition'));", conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return SqlServerVersion.Parse(string.Empty);
        }

        return SqlServerVersion.Parse(
            reader.IsDBNull(0) ? string.Empty : reader.GetString(0),
            reader.IsDBNull(1) ? 0 : reader.GetInt32(1));
    }

    /// <summary>Reads a table (or the internal table of a table type, via <c>type_table_object_id</c>). Null when the object is not visible.</summary>
    public static async Task<TableMeta?> ReadTableAsync(SqlConnection conn, int objectId, SqlServerVersion version, CancellationToken ct)
    {
        var names = await QueryAsync(conn, "SELECT SCHEMA_NAME(o.schema_id), o.name FROM sys.objects o WHERE o.object_id = @Id;",
            c => AddId(c, objectId), r => (Schema: r.GetString(0), Name: r.GetString(1)), ct).ConfigureAwait(false);
        if (names.Count == 0)
        {
            return null;
        }

        var (schema, name) = names[0];
        var columns = await QueryAsync(conn, """
            SELECT c.name, TYPE_NAME(c.system_type_id) AS base_type, ut.name AS user_type, SCHEMA_NAME(ut.schema_id) AS user_type_schema,
                   ut.is_user_defined, c.max_length, c.precision, c.scale, c.is_nullable, c.collation_name,
                   c.is_identity, CONVERT(nvarchar(40), ic.seed_value) AS seed, CONVERT(nvarchar(40), ic.increment_value) AS incr,
                   c.is_computed, cc.definition AS computed_definition, ISNULL(cc.is_persisted, 0) AS is_persisted,
                   dc.name AS default_name, dc.definition AS default_definition, c.is_rowguidcol, c.is_sparse,
                   ISNULL(ic.is_not_for_replication, 0) AS identity_not_for_replication
            FROM sys.columns c
            JOIN sys.types ut ON ut.user_type_id = c.user_type_id
            LEFT JOIN sys.identity_columns ic ON ic.object_id = c.object_id AND ic.column_id = c.column_id
            LEFT JOIN sys.computed_columns cc ON cc.object_id = c.object_id AND cc.column_id = c.column_id
            LEFT JOIN sys.default_constraints dc ON dc.parent_object_id = c.object_id AND dc.parent_column_id = c.column_id
            WHERE c.object_id = @Id
            ORDER BY c.column_id;
            """, c => AddId(c, objectId), MapColumn, ct).ConfigureAwait(false);

        var indexes = await ReadIndexesAsync(conn, objectId, null, ct).ConfigureAwait(false);

        var checks = await QueryAsync(conn,
            "SELECT name, definition, is_disabled, is_not_trusted, is_not_for_replication FROM sys.check_constraints WHERE parent_object_id = @Id ORDER BY name;",
            c => AddId(c, objectId), r => new CheckMeta(r.GetString(0), r.GetString(1), Bool(r, 2), Bool(r, 3), Bool(r, 4)), ct).ConfigureAwait(false);

        var foreignKeys = await ReadForeignKeysAsync(conn, objectId, byConstraint: false, ct).ConfigureAwait(false);
        var warnings = await ReadStorageWarningsAsync(conn, objectId, Sql.Qualified(schema, name), version, ct).ConfigureAwait(false);

        var description = await QueryAsync(conn,
            "SELECT CONVERT(nvarchar(max), value) FROM sys.extended_properties WHERE major_id = @Id AND minor_id = 0 AND class = 1 AND name = N'MS_Description';",
            c => AddId(c, objectId), r => Str(r, 0), ct).ConfigureAwait(false);

        var collation = await QueryAsync(conn, "SELECT CONVERT(sysname, DATABASEPROPERTYEX(DB_NAME(), 'Collation'));",
            null, r => Str(r, 0), ct).ConfigureAwait(false);

        // Heap / clustered data space, only when it is a filegroup other than the default (partition schemes are warnings).
        var fileGroup = await QueryAsync(conn, $"""
            SELECT {NonDefaultFileGroup}
            FROM sys.indexes i JOIN sys.data_spaces ds ON ds.data_space_id = i.data_space_id
            WHERE i.object_id = @Id AND i.index_id IN (0, 1);
            """, c => AddId(c, objectId), r => Str(r, 0), ct).ConfigureAwait(false);

        // LOB data (max types, text/ntext/image, xml) lands on the table's filegroup unless TEXTIMAGE_ON says otherwise;
        // lob_data_space_id is 0 when the table has no LOB columns.
        var lob = await QueryAsync(conn, """
            SELECT lob.name, lob.type, (SELECT d.name FROM sys.data_spaces d WHERE d.is_default = 1 AND d.type = 'FG')
            FROM sys.tables t JOIN sys.data_spaces lob ON lob.data_space_id = t.lob_data_space_id
            WHERE t.object_id = @Id;
            """, c => AddId(c, objectId), r => (Name: r.GetString(0), Type: Str(r, 1), Default: Str(r, 2)), ct).ConfigureAwait(false);
        var tableFileGroup = fileGroup.FirstOrDefault();
        string? lobFileGroup = null;
        if (lob.Count == 1 && lob[0].Type == "FG"
            && !string.Equals(lob[0].Name, tableFileGroup ?? lob[0].Default, StringComparison.OrdinalIgnoreCase))
        {
            lobFileGroup = lob[0].Name;
        }

        return new TableMeta(schema, name, collation.FirstOrDefault(), description.FirstOrDefault(), columns, indexes, checks, foreignKeys, warnings,
            tableFileGroup, lobFileGroup);
    }

    /// <summary>Select expression over <c>ds</c> (sys.data_spaces): the filegroup name when it is not the database default, else NULL.</summary>
    private const string NonDefaultFileGroup = "CASE WHEN ds.type = 'FG' AND ds.is_default = 0 THEN ds.name END AS file_group";

    public static async Task<(IndexMeta ix, string schema, string table)?> ReadIndexAsync(SqlConnection conn, int parentObjectId, string indexName, CancellationToken ct)
    {
        var indexes = await ReadIndexesAsync(conn, parentObjectId, indexName, ct).ConfigureAwait(false);
        if (indexes.Count == 0)
        {
            return null;
        }

        var names = await QueryAsync(conn, "SELECT SCHEMA_NAME(o.schema_id), o.name FROM sys.objects o WHERE o.object_id = @Id;",
            c => AddId(c, parentObjectId), r => (Schema: r.GetString(0), Name: r.GetString(1)), ct).ConfigureAwait(false);
        return names.Count == 0 ? null : (indexes[0], names[0].Schema, names[0].Name);
    }

    /// <summary>Indexes (type &gt; 0, i.e. not the heap) of a table or view; <paramref name="indexName"/> narrows to one.</summary>
    public static async Task<IReadOnlyList<IndexMeta>> ReadIndexesAsync(SqlConnection conn, int objectId, string? indexName, CancellationToken ct)
    {
        var rows = await QueryAsync(conn, $"""
            SELECT i.index_id, i.name, i.type, i.is_unique, i.is_primary_key, i.is_unique_constraint, i.filter_definition, i.is_disabled,
                   i.ignore_dup_key, {NonDefaultFileGroup}
            FROM sys.indexes i LEFT JOIN sys.data_spaces ds ON ds.data_space_id = i.data_space_id
            WHERE i.object_id = @Id AND i.type > 0 AND (@Name IS NULL OR i.name = @Name) ORDER BY i.index_id;
            """,
            c =>
            {
                AddId(c, objectId);
                AddName(c, "@Name", indexName);
            },
            r => (Id: r.GetInt32(0), Name: r.GetString(1), Type: r.GetByte(2), Unique: Bool(r, 3), Pk: Bool(r, 4), Uq: Bool(r, 5), Filter: Str(r, 6), Disabled: Bool(r, 7),
                  IgnoreDupKey: Bool(r, 8), FileGroup: Str(r, 9)),
            ct).ConfigureAwait(false);
        if (rows.Count == 0)
        {
            return [];
        }

        var columns = await QueryAsync(conn, """
            SELECT ic.index_id, c.name, ic.is_descending_key, ic.is_included_column
            FROM sys.index_columns ic JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE ic.object_id = @Id ORDER BY ic.index_id, ic.is_included_column, ic.key_ordinal, ic.index_column_id;
            """, c => AddId(c, objectId), r => (IndexId: r.GetInt32(0), Col: new IndexColumnMeta(r.GetString(1), Bool(r, 2), Bool(r, 3))), ct).ConfigureAwait(false);

        return rows.Select(i => new IndexMeta(i.Name, i.Type, i.Unique, i.Pk, i.Uq, i.Filter, i.Disabled,
            columns.Where(c => c.IndexId == i.Id).Select(c => c.Col).ToList(), i.IgnoreDupKey, i.FileGroup)).ToList();
    }

    public static async Task<ForeignKeyMeta?> ReadForeignKeyAsync(SqlConnection conn, int fkObjectId, CancellationToken ct)
    {
        var list = await ReadForeignKeysAsync(conn, fkObjectId, byConstraint: true, ct).ConfigureAwait(false);
        return list.Count == 0 ? null : list[0];
    }

    /// <summary>Null when no sys.sql_modules row exists (e.g. CLR); <c>definition</c> is null when the module is encrypted.</summary>
    public static async Task<(string? definition, bool ansiNulls, bool quotedIdentifier)?> ReadModuleAsync(SqlConnection conn, int objectId, CancellationToken ct)
    {
        var rows = await QueryAsync(conn,
            "SELECT m.definition, m.uses_ansi_nulls, m.uses_quoted_identifier FROM sys.sql_modules m WHERE m.object_id = @Id;",
            c => AddId(c, objectId), MapModule, ct).ConfigureAwait(false);
        return rows.Count == 0 ? null : rows[0];
    }

    /// <summary>A database-scoped DDL trigger (<c>parent_class = 0</c>); <c>definition</c> is null when encrypted.</summary>
    public static async Task<(string? definition, bool ansiNulls, bool quotedIdentifier)?> ReadDatabaseTriggerAsync(SqlConnection conn, string name, CancellationToken ct)
    {
        var rows = await QueryAsync(conn, """
            SELECT m.definition, m.uses_ansi_nulls, m.uses_quoted_identifier
            FROM sys.triggers t JOIN sys.sql_modules m ON m.object_id = t.object_id WHERE t.parent_class = 0 AND t.name = @Name;
            """, c => AddName(c, "@Name", name), MapModule, ct).ConfigureAwait(false);
        return rows.Count == 0 ? null : rows[0];
    }

    /// <summary>
    /// DISABLE TRIGGER statement for a disabled trigger, or null when it is enabled / not found. DML triggers are looked up
    /// by object id (<paramref name="databaseTriggerName"/> null); database DDL triggers by name.
    /// </summary>
    public static async Task<string?> ReadTriggerDisableStatementAsync(SqlConnection conn, int? objectId, string? databaseTriggerName, CancellationToken ct)
    {
        if (databaseTriggerName is not null)
        {
            var ddl = await QueryAsync(conn, "SELECT t.is_disabled FROM sys.triggers t WHERE t.parent_class = 0 AND t.name = @Name;",
                c => AddName(c, "@Name", databaseTriggerName), r => Bool(r, 0), ct).ConfigureAwait(false);
            return ddl.Count == 1 && ddl[0] ? $"DISABLE TRIGGER {Sql.Q(databaseTriggerName)} ON DATABASE;" : null;
        }

        var dml = await QueryAsync(conn, """
            SELECT t.is_disabled, SCHEMA_NAME(o.schema_id), o.name, SCHEMA_NAME(tro.schema_id), t.name
            FROM sys.triggers t
            JOIN sys.objects o ON o.object_id = t.parent_id
            JOIN sys.objects tro ON tro.object_id = t.object_id
            WHERE t.object_id = @Id AND t.parent_class = 1;
            """, c => AddId(c, objectId ?? 0),
            r => (Disabled: Bool(r, 0), ParentSchema: r.GetString(1), Parent: r.GetString(2), Schema: r.GetString(3), Name: r.GetString(4)), ct).ConfigureAwait(false);
        return dml.Count == 1 && dml[0].Disabled
            ? $"DISABLE TRIGGER {Sql.Qualified(dml[0].Schema, dml[0].Name)} ON {Sql.Qualified(dml[0].ParentSchema, dml[0].Parent)};"
            : null;
    }

    public static async Task<LoginMeta?> ReadLoginAsync(SqlConnection conn, string name, CancellationToken ct)
    {
        var rows = await QueryAsync(conn, """
            SELECT sp.name, sp.type, sp.is_disabled, sp.default_database_name, sp.default_language_name, sl.is_policy_checked, sl.is_expiration_checked
            FROM sys.server_principals sp LEFT JOIN sys.sql_logins sl ON sl.principal_id = sp.principal_id
            WHERE sp.name = @Name AND sp.type IN ('S','U','G','C','K');
            """, c => AddName(c, "@Name", name),
            r => (Name: r.GetString(0), Type: r.GetString(1)[0], Disabled: Bool(r, 2), Db: Str(r, 3), Lang: Str(r, 4), Policy: NBool(r, 5), Expiration: NBool(r, 6)),
            ct).ConfigureAwait(false);
        if (rows.Count == 0)
        {
            return null;
        }

        var roles = await Names(conn, """
            SELECT r.name FROM sys.server_role_members m JOIN sys.server_principals r ON r.principal_id = m.role_principal_id
            JOIN sys.server_principals p ON p.principal_id = m.member_principal_id WHERE p.name = @Name ORDER BY r.name;
            """, name, ct).ConfigureAwait(false);
        var l = rows[0];
        var serverPermissions = await ReadServerPermissionsAsync(conn, name, ct).ConfigureAwait(false);

        // The login's user in the connection's database (by SID): its schemas, roles and permissions there.
        LoginDatabaseMeta? database = null;
        var mapped = await QueryAsync(conn, """
            SELECT dp.name, DB_NAME() FROM sys.database_principals dp JOIN sys.server_principals sp ON sp.sid = dp.sid
            WHERE sp.name = @Name AND dp.type IN ('S','U','G');
            """, c => AddName(c, "@Name", name), r => (User: r.GetString(0), Database: r.GetString(1)), ct).ConfigureAwait(false);
        if (mapped.Count > 0)
        {
            var version = SqlServerVersion.Parse(conn.ServerVersion);
            if (await ReadDatabaseUserAsync(conn, mapped[0].User, version, ct).ConfigureAwait(false) is { } user)
            {
                database = new LoginDatabaseMeta(mapped[0].Database, user, user.OwnedSchemas ?? [], user.Permissions ?? []);
            }
        }

        return new LoginMeta(l.Name, l.Type, l.Disabled, l.Db, l.Lang, l.Policy, l.Expiration, roles, serverPermissions, database);
    }

    /// <summary>Schemas the database principal owns.</summary>
    public static Task<List<string>> ReadOwnedSchemasAsync(SqlConnection conn, string principal, CancellationToken ct) =>
        Names(conn, """
            SELECT s.name FROM sys.schemas s JOIN sys.database_principals p ON p.principal_id = s.principal_id
            WHERE p.name = @Name ORDER BY s.name;
            """, principal, ct);

    /// <summary>
    /// The database permissions granted, denied or revoked to the principal, with each securable rendered as an ON
    /// clause (objects and columns, schemas, principals, assemblies, types, XML schema collections, keys and
    /// certificates). Other classes come back marked unsupported. Only what the caller may see (VIEW DEFINITION).
    /// </summary>
    public static async Task<List<PermissionMeta>> ReadDatabasePermissionsAsync(SqlConnection conn, string principal, CancellationToken ct)
    {
        var rows = await QueryAsync(conn, """
            SELECT p.state, p.permission_name, p.class, p.class_desc,
                   CASE p.class WHEN 1 THEN OBJECT_SCHEMA_NAME(p.major_id) WHEN 3 THEN SCHEMA_NAME(p.major_id)
                       WHEN 6 THEN SCHEMA_NAME(t.schema_id) WHEN 10 THEN SCHEMA_NAME(x.schema_id) END,
                   CASE p.class WHEN 1 THEN OBJECT_NAME(p.major_id) WHEN 4 THEN dp.name WHEN 5 THEN a.name WHEN 6 THEN t.name
                       WHEN 10 THEN x.name WHEN 24 THEN sk.name WHEN 25 THEN ce.name WHEN 26 THEN ak.name END,
                   CASE WHEN p.class = 1 AND p.minor_id > 0 THEN COL_NAME(p.major_id, p.minor_id) END,
                   dp.type
            FROM sys.database_permissions p
                JOIN sys.database_principals g ON g.principal_id = p.grantee_principal_id
                LEFT JOIN sys.database_principals dp ON p.class = 4 AND dp.principal_id = p.major_id
                LEFT JOIN sys.assemblies a ON p.class = 5 AND a.assembly_id = p.major_id
                LEFT JOIN sys.types t ON p.class = 6 AND t.user_type_id = p.major_id
                LEFT JOIN sys.xml_schema_collections x ON p.class = 10 AND x.xml_collection_id = p.major_id
                LEFT JOIN sys.symmetric_keys sk ON p.class = 24 AND sk.symmetric_key_id = p.major_id
                LEFT JOIN sys.certificates ce ON p.class = 25 AND ce.certificate_id = p.major_id
                LEFT JOIN sys.asymmetric_keys ak ON p.class = 26 AND ak.asymmetric_key_id = p.major_id
            WHERE g.name = @Name
            ORDER BY p.class, 5, 6, 7, p.state, p.permission_name;
            """, c => AddName(c, "@Name", principal),
            r => (State: r.GetString(0)[0], Permission: r.GetString(1), Class: (int)r.GetByte(2), ClassDesc: r.GetString(3),
                  Schema: Str(r, 4), Name: Str(r, 5), Column: Str(r, 6), PrincipalType: Str(r, 7)),
            ct).ConfigureAwait(false);
        return [.. rows.Select(p =>
        {
            var (on, unsupported) = DatabaseSecurable(p.Class, p.ClassDesc, p.Schema, p.Name, p.Column, p.PrincipalType);
            return new PermissionMeta(p.State, p.Permission, on, unsupported);
        })];
    }

    /// <summary>The ON clause of a database permission, or (null, class) when the class is not scripted.</summary>
    internal static (string? On, string? Unsupported) DatabaseSecurable(int cls, string classDesc, string? schema, string? name, string? column, string? principalType)
    {
        string Named(string keyword) => name is null ? throw new InvalidOperationException() : $"{keyword}::{Sql.Q(name)}";
        try
        {
            return cls switch
            {
                0 => (null, null),
                1 when schema is not null && name is not null => (Sql.Qualified(schema, name) + (column is null ? "" : $" ({Sql.Q(column)})"), null),
                3 when name is null && schema is not null => ($"SCHEMA::{Sql.Q(schema)}", null),
                4 => (Named(principalType switch { "R" => "ROLE", "A" => "APPLICATION ROLE", _ => "USER" }), null),
                5 => (Named("ASSEMBLY"), null),
                6 when schema is not null && name is not null => ($"TYPE::{Sql.Qualified(schema, name)}", null),
                10 when schema is not null && name is not null => ($"XML SCHEMA COLLECTION::{Sql.Qualified(schema, name)}", null),
                24 => (Named("SYMMETRIC KEY"), null),
                25 => (Named("CERTIFICATE"), null),
                26 => (Named("ASYMMETRIC KEY"), null),
                _ => (null, classDesc.Replace('_', ' ').ToLowerInvariant()),
            };
        }
        catch (InvalidOperationException)
        {
            return (null, classDesc.Replace('_', ' ').ToLowerInvariant());
        }
    }

    /// <summary>Server permissions of a login or server role (server level, logins / server roles, endpoints).</summary>
    public static async Task<List<PermissionMeta>> ReadServerPermissionsAsync(SqlConnection conn, string principal, CancellationToken ct)
    {
        var rows = await QueryAsync(conn, """
            SELECT p.state, p.permission_name, p.class, p.class_desc, t.name, t.type, e.name
            FROM sys.server_permissions p
                JOIN sys.server_principals g ON g.principal_id = p.grantee_principal_id
                LEFT JOIN sys.server_principals t ON p.class = 101 AND t.principal_id = p.major_id
                LEFT JOIN sys.endpoints e ON p.class = 105 AND e.endpoint_id = p.major_id
            WHERE g.name = @Name
            ORDER BY p.class, 5, 7, p.state, p.permission_name;
            """, c => AddName(c, "@Name", principal),
            r => (State: r.GetString(0)[0], Permission: r.GetString(1), Class: (int)r.GetByte(2), ClassDesc: r.GetString(3),
                  Principal: Str(r, 4), PrincipalType: Str(r, 5), Endpoint: Str(r, 6)),
            ct).ConfigureAwait(false);
        return [.. rows.Select(p => p.Class switch
        {
            100 => new PermissionMeta(p.State, p.Permission, null),
            101 when p.Principal is not null => new PermissionMeta(p.State, p.Permission, $"{(p.PrincipalType == "R" ? "SERVER ROLE" : "LOGIN")}::{Sql.Q(p.Principal)}"),
            105 when p.Endpoint is not null => new PermissionMeta(p.State, p.Permission, $"ENDPOINT::{Sql.Q(p.Endpoint)}"),
            _ => new PermissionMeta(p.State, p.Permission, null, p.ClassDesc.Replace('_', ' ').ToLowerInvariant()),
        })];
    }

    public static async Task<ServerRoleMeta?> ReadServerRoleAsync(SqlConnection conn, string name, SqlServerVersion version, CancellationToken ct)
    {
        // is_fixed_role exists on sys.server_principals from 11.0; before that every server role is fixed.
        var sql = version.Major >= 11
            ? "SELECT sp.name, sp.is_fixed_role FROM sys.server_principals sp WHERE sp.name = @Name AND sp.type = 'R';"
            : "SELECT sp.name, CONVERT(bit, 1) FROM sys.server_principals sp WHERE sp.name = @Name AND sp.type = 'R';";
        var rows = await QueryAsync(conn, sql, c => AddName(c, "@Name", name), r => (Name: r.GetString(0), Fixed: Bool(r, 1)), ct).ConfigureAwait(false);
        if (rows.Count == 0)
        {
            return null;
        }

        var members = await Names(conn, """
            SELECT p.name FROM sys.server_role_members m JOIN sys.server_principals r ON r.principal_id = m.role_principal_id
            JOIN sys.server_principals p ON p.principal_id = m.member_principal_id WHERE r.name = @Name ORDER BY p.name;
            """, name, ct).ConfigureAwait(false);
        var permissions = await ReadServerPermissionsAsync(conn, name, ct).ConfigureAwait(false);
        return new ServerRoleMeta(rows[0].Name, version.Major < 11 || rows[0].Fixed, members, permissions);
    }

    public static async Task<DatabaseUserMeta?> ReadDatabaseUserAsync(SqlConnection conn, string name, SqlServerVersion version, CancellationToken ct)
    {
        // authentication_type (2 = DATABASE, contained users) exists from 11.0; 2008 R2 lacks the column.
        var authColumn = version.Major >= 11 ? "dp.authentication_type" : "0";
        var rows = await QueryAsync(conn, $"""
            SELECT dp.name, dp.type, sp.name AS login_name, dp.default_schema_name, CASE WHEN dp.sid IS NULL OR (dp.type = 'S' AND DATALENGTH(dp.sid) = 28) THEN 1 ELSE 0 END AS without_login, {authColumn} AS auth_type
            FROM sys.database_principals dp LEFT JOIN sys.server_principals sp ON sp.sid = dp.sid
            WHERE dp.name = @Name AND dp.type IN ('S','U','G','E','X','C','K');
            """, c => AddName(c, "@Name", name),
            r => (Name: r.GetString(0), Type: r.GetString(1)[0], Login: Str(r, 2), Schema: Str(r, 3), WithoutLogin: r.GetInt32(4) == 1, Auth: Convert.ToInt32(r.GetValue(5), System.Globalization.CultureInfo.InvariantCulture)),
            ct).ConfigureAwait(false);
        if (rows.Count == 0)
        {
            return null;
        }

        var roles = await Names(conn, """
            SELECT r.name FROM sys.database_role_members m JOIN sys.database_principals r ON r.principal_id = m.role_principal_id
            JOIN sys.database_principals p ON p.principal_id = m.member_principal_id WHERE p.name = @Name ORDER BY r.name;
            """, name, ct).ConfigureAwait(false);
        var u = rows[0];
        var owned = await ReadOwnedSchemasAsync(conn, u.Name, ct).ConfigureAwait(false);
        var permissions = await ReadDatabasePermissionsAsync(conn, u.Name, ct).ConfigureAwait(false);
        return new DatabaseUserMeta(u.Name, u.Type, u.Login, u.Schema, u.WithoutLogin, roles, u.Auth == 2, owned, permissions);
    }

    public static async Task<DatabaseRoleMeta?> ReadDatabaseRoleAsync(SqlConnection conn, string name, CancellationToken ct)
    {
        var rows = await QueryAsync(conn, """
            SELECT dp.name, CONVERT(bit, CASE WHEN dp.is_fixed_role = 1 OR dp.principal_id = 0 THEN 1 ELSE 0 END), dp.type, o.name AS owner, dp.default_schema_name
            FROM sys.database_principals dp LEFT JOIN sys.database_principals o ON o.principal_id = dp.owning_principal_id
            WHERE dp.name = @Name AND dp.type IN ('R','A');
            """, c => AddName(c, "@Name", name),
            r => (Name: r.GetString(0), Fixed: Bool(r, 1), App: r.GetString(2) == "A", Owner: Str(r, 3), Schema: Str(r, 4)),
            ct).ConfigureAwait(false);
        if (rows.Count == 0)
        {
            return null;
        }

        var members = await Names(conn, """
            SELECT p.name FROM sys.database_role_members m JOIN sys.database_principals r ON r.principal_id = m.role_principal_id
            JOIN sys.database_principals p ON p.principal_id = m.member_principal_id WHERE r.name = @Name ORDER BY p.name;
            """, name, ct).ConfigureAwait(false);
        var d = rows[0];
        var owned = await ReadOwnedSchemasAsync(conn, d.Name, ct).ConfigureAwait(false);
        var permissions = await ReadDatabasePermissionsAsync(conn, d.Name, ct).ConfigureAwait(false);
        return new DatabaseRoleMeta(d.Name, d.Fixed, d.App, d.Owner, d.Schema, members, owned, permissions);
    }

    /// <summary>
    /// A user-defined type. Alias types return <c>aliasBase</c>, table types <c>tableShape</c>; CLR (assembly) types return both null.
    /// When <paramref name="schema"/> is null, dbo wins, then the first schema by name.
    /// </summary>
    public static async Task<(string schema, string name, ColumnMeta? aliasBase, TableMeta? tableShape)?> ReadUserTypeAsync(
        SqlConnection conn, string? schema, string name, SqlServerVersion version, CancellationToken ct)
    {
        var memoryOptimized = version.SupportsMemoryOptimized ? "tt.is_memory_optimized" : "CONVERT(bit, 0)";
        var rows = await QueryAsync(conn, $"""
            SELECT TOP (1) SCHEMA_NAME(t.schema_id), t.name, TYPE_NAME(t.system_type_id), t.max_length, t.precision, t.scale, t.is_nullable,
                   t.collation_name, t.is_table_type, t.is_assembly_type, tt.type_table_object_id, {memoryOptimized}
            FROM sys.types t LEFT JOIN sys.table_types tt ON tt.user_type_id = t.user_type_id
            WHERE t.is_user_defined = 1 AND t.name = @Name AND (@Schema IS NULL OR SCHEMA_NAME(t.schema_id) = @Schema)
            ORDER BY CASE WHEN SCHEMA_NAME(t.schema_id) = N'dbo' THEN 0 ELSE 1 END, SCHEMA_NAME(t.schema_id);
            """,
            c =>
            {
                AddName(c, "@Name", name);
                AddName(c, "@Schema", schema);
            },
            r => (Schema: r.GetString(0), Name: r.GetString(1), Base: Str(r, 2), MaxLength: r.GetInt16(3), Precision: r.GetByte(4), Scale: r.GetByte(5),
                  Nullable: Bool(r, 6), Collation: Str(r, 7), IsTable: Bool(r, 8), IsClr: Bool(r, 9), TableId: r.IsDBNull(10) ? (int?)null : r.GetInt32(10), MemOpt: Bool(r, 11)),
            ct).ConfigureAwait(false);
        if (rows.Count == 0)
        {
            return null;
        }

        var t = rows[0];
        if (t.IsClr || (!t.IsTable && t.Base is null))
        {
            return (t.Schema, t.Name, null, null);
        }

        if (!t.IsTable)
        {
            var baseType = new ColumnMeta(string.Empty, t.Base!, null, t.MaxLength, t.Precision, t.Scale, t.Nullable, t.Collation,
                false, null, null, false, null, false, null, null, false, false);
            return (t.Schema, t.Name, baseType, null);
        }

        var shape = t.TableId is { } id ? await ReadTableAsync(conn, id, version, ct).ConfigureAwait(false) : null;
        if (shape is null)
        {
            return (t.Schema, t.Name, null, null);
        }

        var display = Sql.Qualified(t.Schema, t.Name);
        var extra = new List<string>(shape.Warnings);
        if (t.MemOpt)
        {
            extra.Add($"table type {display} is memory-optimized; MEMORY_OPTIMIZED = ON and its indexes are not scripted.");
        }

        foreach (var ix in shape.Indexes.Where(i => !i.IsPrimaryKey && !i.IsUniqueConstraint))
        {
            extra.Add($"table type {display} index {Sql.Q(ix.Name)} is not scripted.");
        }

        return (t.Schema, t.Name, null, shape with { Warnings = extra });
    }

    private static async Task<IReadOnlyList<ForeignKeyMeta>> ReadForeignKeysAsync(SqlConnection conn, int id, bool byConstraint, CancellationToken ct)
    {
        var fkFilter = byConstraint ? "fk.object_id = @Id" : "fk.parent_object_id = @Id";
        var colFilter = byConstraint ? "fkc.constraint_object_id = @Id" : "fkc.parent_object_id = @Id";
        var fks = await QueryAsync(conn, $"""
            SELECT fk.object_id, fk.name, SCHEMA_NAME(p.schema_id) AS s, p.name AS t, SCHEMA_NAME(r.schema_id) AS rs, r.name AS rt,
                   fk.delete_referential_action_desc, fk.update_referential_action_desc, fk.is_not_for_replication, fk.is_disabled, fk.is_not_trusted
            FROM sys.foreign_keys fk JOIN sys.tables p ON p.object_id = fk.parent_object_id JOIN sys.objects r ON r.object_id = fk.referenced_object_id
            WHERE {fkFilter}
            ORDER BY fk.name;
            """, c => AddId(c, id),
            r => (Id: r.GetInt32(0), Name: r.GetString(1), S: r.GetString(2), T: r.GetString(3), Rs: r.GetString(4), Rt: r.GetString(5),
                  Del: r.GetString(6), Upd: r.GetString(7), Nfr: Bool(r, 8), Disabled: Bool(r, 9), NotTrusted: Bool(r, 10)),
            ct).ConfigureAwait(false);
        if (fks.Count == 0)
        {
            return [];
        }

        var cols = await QueryAsync(conn, $"""
            SELECT fkc.constraint_object_id, pc.name AS col, rc.name AS ref_col
            FROM sys.foreign_key_columns fkc
            JOIN sys.columns pc ON pc.object_id = fkc.parent_object_id AND pc.column_id = fkc.parent_column_id
            JOIN sys.columns rc ON rc.object_id = fkc.referenced_object_id AND rc.column_id = fkc.referenced_column_id
            WHERE {colFilter} ORDER BY fkc.constraint_object_id, fkc.constraint_column_id;
            """, c => AddId(c, id), r => (FkId: r.GetInt32(0), Col: r.GetString(1), RefCol: r.GetString(2)), ct).ConfigureAwait(false);

        return fks.Select(f =>
        {
            var mine = cols.Where(c => c.FkId == f.Id).ToList();
            return new ForeignKeyMeta(f.Name, f.S, f.T, mine.Select(c => c.Col).ToList(), f.Rs, f.Rt, mine.Select(c => c.RefCol).ToList(),
                f.Del, f.Upd, f.Nfr, f.Disabled, f.NotTrusted);
        }).ToList();
    }

    private static async Task<IReadOnlyList<string>> ReadStorageWarningsAsync(SqlConnection conn, int objectId, string display, SqlServerVersion version, CancellationToken ct)
    {
        var temporal = version.SupportsTemporal ? "t.temporal_type" : "CONVERT(tinyint, 0)";
        var memoryOptimized = version.SupportsMemoryOptimized ? "t.is_memory_optimized" : "CONVERT(bit, 0)";
        var rows = await QueryAsync(conn, $"""
            SELECT ds.type AS data_space_type,
                   (SELECT MAX(p.data_compression) FROM sys.partitions p WHERE p.object_id = @Id) AS max_compression,
                   t.filestream_data_space_id, t.lock_escalation_desc, {temporal} AS temporal_type, {memoryOptimized} AS is_memory_optimized
            FROM sys.tables t JOIN sys.indexes i ON i.object_id = t.object_id AND i.index_id IN (0, 1)
            JOIN sys.data_spaces ds ON ds.data_space_id = i.data_space_id
            WHERE t.object_id = @Id;
            """, c => AddId(c, objectId),
            r => (Space: Str(r, 0), Compression: r.IsDBNull(1) ? 0 : Convert.ToInt32(r.GetValue(1), System.Globalization.CultureInfo.InvariantCulture),
                  Filestream: !r.IsDBNull(2), LockEscalation: Str(r, 3), Temporal: r.IsDBNull(4) ? 0 : r.GetByte(4), MemOpt: Bool(r, 5)),
            ct).ConfigureAwait(false);

        var warnings = new List<string>();
        foreach (var p in rows.Take(1))
        {
            if (p.Space == "PS")
            {
                warnings.Add($"table {display} is partitioned; the partition scheme is not scripted (the table is created on the default filegroup).");
            }

            if (p.Compression > 0)
            {
                warnings.Add($"table {display} uses data compression; DATA_COMPRESSION is not scripted.");
            }

            if (p.Filestream)
            {
                warnings.Add($"table {display} has FILESTREAM data; FILESTREAM storage is not scripted.");
            }

            if (p.LockEscalation is not null && p.LockEscalation != "TABLE")
            {
                warnings.Add($"table {display} has LOCK_ESCALATION = {p.LockEscalation}; this option is not scripted.");
            }

            if (p.Temporal > 0)
            {
                warnings.Add($"table {display} is a system-versioned temporal table or history table; PERIOD FOR SYSTEM_TIME / SYSTEM_VERSIONING is not scripted.");
            }

            if (p.MemOpt)
            {
                warnings.Add($"table {display} is memory-optimized; MEMORY_OPTIMIZED / DURABILITY options are not scripted.");
            }
        }

        return warnings;
    }

    private static ColumnMeta MapColumn(SqlDataReader r)
    {
        var isUserDefined = Bool(r, 4);
        var baseType = Str(r, 1);
        return new ColumnMeta(
            Name: r.GetString(0),
            // System CLR types (hierarchyid, geometry, geography) have no TYPE_NAME for their system_type_id.
            TypeName: isUserDefined || baseType is null ? r.GetString(2) : baseType,
            UserTypeSchema: isUserDefined ? Str(r, 3) : null,
            MaxLength: r.GetInt16(5),
            Precision: r.GetByte(6),
            Scale: r.GetByte(7),
            IsNullable: Bool(r, 8),
            Collation: Str(r, 9),
            IsIdentity: Bool(r, 10),
            IdentitySeed: Str(r, 11),
            IdentityIncrement: Str(r, 12),
            IsComputed: Bool(r, 13),
            ComputedDefinition: Str(r, 14),
            IsPersisted: Bool(r, 15),
            DefaultName: Str(r, 16),
            DefaultDefinition: Str(r, 17),
            IsRowGuidCol: Bool(r, 18),
            IsSparse: Bool(r, 19),
            IdentityNotForReplication: Bool(r, 20));
    }

    private static (string? definition, bool ansiNulls, bool quotedIdentifier) MapModule(SqlDataReader r) =>
        (Str(r, 0), Bool(r, 1), Bool(r, 2));

    private static Task<List<string>> Names(SqlConnection conn, string sql, string name, CancellationToken ct) =>
        QueryAsync(conn, sql, c => AddName(c, "@Name", name), r => r.GetString(0), ct);

    private static async Task<List<T>> QueryAsync<T>(SqlConnection conn, string sql, Action<SqlCommand>? bind, Func<SqlDataReader, T> map, CancellationToken ct)
    {
        await using var cmd = new SqlCommand(sql, conn);
        bind?.Invoke(cmd);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var list = new List<T>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(map(reader));
        }

        return list;
    }

    private static void AddId(SqlCommand cmd, int id) => cmd.Parameters.Add("@Id", SqlDbType.Int).Value = id;

    private static void AddName(SqlCommand cmd, string parameter, string? value) =>
        cmd.Parameters.Add(parameter, SqlDbType.NVarChar, 128).Value = value is null ? DBNull.Value : value;

    private static string? Str(SqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    private static bool Bool(SqlDataReader r, int i) => !r.IsDBNull(i) && r.GetBoolean(i);

    private static bool? NBool(SqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetBoolean(i);
}
