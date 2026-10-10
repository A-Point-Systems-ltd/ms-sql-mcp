// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.

using System.Text;

namespace Mssql.McpServer.Scripting;

/// <summary>
/// One permission row (sys.database_permissions / sys.server_permissions) of a principal. <paramref name="State"/> is
/// G (grant), W (grant with grant option), D (deny) or R (revoke). <paramref name="On"/> is the securable clause
/// without ON (<c>[dbo].[T]</c>, <c>SCHEMA::[s]</c>, <c>[dbo].[T] ([c])</c>), null for database / server level;
/// <paramref name="Unsupported"/> names a securable class that is not scripted.
/// </summary>
internal sealed record PermissionMeta(char State, string Permission, string? On, string? Unsupported = null);

/// <summary>A login's user in the connection's database: the user, the schemas it owns and its permissions.</summary>
internal sealed record LoginDatabaseMeta(
    string DatabaseName,
    DatabaseUserMeta User,
    IReadOnlyList<string> OwnedSchemas,
    IReadOnlyList<PermissionMeta> Permissions);

/// <summary>Server login (Type: S sql, U windows user, G windows group, C certificate, K asymmetric key).</summary>
internal sealed record LoginMeta(
    string Name,
    char Type,
    bool IsDisabled,
    string? DefaultDatabase,
    string? DefaultLanguage,
    bool? CheckPolicy,
    bool? CheckExpiration,
    IReadOnlyList<string> ServerRoles,
    IReadOnlyList<PermissionMeta>? ServerPermissions = null,
    LoginDatabaseMeta? Database = null);

internal sealed record ServerRoleMeta(string Name, bool IsFixed, IReadOnlyList<string> Members, IReadOnlyList<PermissionMeta>? Permissions = null);

/// <summary>Database user (Type: S, U, G, E, X, C, K as in sys.database_principals.type).</summary>
internal sealed record DatabaseUserMeta(
    string Name,
    char Type,
    string? LoginName,
    string? DefaultSchema,
    bool WithoutLogin,
    IReadOnlyList<string> Roles,
    bool HasDatabasePassword = false,
    IReadOnlyList<string>? OwnedSchemas = null,
    IReadOnlyList<PermissionMeta>? Permissions = null);

internal sealed record DatabaseRoleMeta(
    string Name,
    bool IsFixed,
    bool IsApplicationRole,
    string? Owner,
    string? DefaultSchema,
    IReadOnlyList<string> Members,
    IReadOnlyList<string>? OwnedSchemas = null,
    IReadOnlyList<PermissionMeta>? Permissions = null);

/// <summary>
/// Pure renderer for security principals. Never emits passwords, password hashes or SIDs. Scripts are grouped in
/// sections, each headed by a comment (<c>--create</c>, <c>--members</c>, <c>--permissions</c>, ...); empty sections
/// are left out.
/// </summary>
internal static class SecurityDdlRenderer
{
    public const string PasswordPlaceholder = "N'<password not scripted - set before running>'";

    private const string Sep = "\r\n";

    public static string RenderLogin(LoginMeta l, SqlServerVersion v, out IReadOnlyList<string> warnings)
    {
        var list = new List<string>();
        warnings = list;

        if (l.Type is 'C' or 'K')
        {
            return Warn(list, $"{l.Name} is mapped to a certificate/asymmetric key and is not scripted.");
        }

        if (l.Type is not ('S' or 'U' or 'G'))
        {
            return Warn(list, $"{l.Name} is a login of unsupported type '{l.Type}' and is not scripted.");
        }

        var options = new List<string>();
        string head;
        if (l.Type == 'S')
        {
            head = $"CREATE LOGIN {Sql.Q(l.Name)} WITH PASSWORD = {PasswordPlaceholder}";
            AddLoginOptions(options, l);
            // SQL Server rejects CHECK_EXPIRATION = ON unless CHECK_POLICY is ON.
            bool? policy = l.CheckExpiration == true ? true : l.CheckPolicy;

            if (policy is { } cp)
            {
                options.Add("CHECK_POLICY = " + (cp ? "ON" : "OFF"));
            }

            if (l.CheckExpiration is { } ce)
            {
                options.Add("CHECK_EXPIRATION = " + (ce ? "ON" : "OFF"));
            }

            head += options.Count > 0 ? ", " + string.Join(", ", options) : "";
        }
        else
        {
            head = $"CREATE LOGIN {Sql.Q(l.Name)} FROM WINDOWS";
            AddLoginOptions(options, l);
            head += options.Count > 0 ? " WITH " + string.Join(", ", options) : "";
        }

        var create = new List<string> { head + ";" };
        if (l.IsDisabled)
        {
            create.Add($"ALTER LOGIN {Sql.Q(l.Name)} DISABLE;");
        }

        var sections = new List<(string, IReadOnlyList<string>)>
        {
            ("create", create),
            ("server roles", [.. l.ServerRoles.Select(role => ServerMember(role, l.Name, v))]),
            ("server permissions", Permissions(l.ServerPermissions, l.Name, list)),
        };

        if (l.Database is { } db)
        {
            var user = db.User;
            if (IsBuiltInUser(user.Name))
            {
                sections.Add(($"database {Sql.Q(db.DatabaseName)}", [
                    $"-- {Sql.CommentSafe($"{l.Name} is the built-in user {user.Name} in this database (database owner); nothing to script.")}"]));
            }
            else
            {
                // The login script runs in master: the database part switches to the database first.
                sections.Add(($"database {Sql.Q(db.DatabaseName)}: user {Sql.Q(user.Name)}", [$"USE {Sql.Q(db.DatabaseName)};", .. UserCreate(user, list)]));
                sections.Add(("owned schemas", OwnedSchemas(db.OwnedSchemas, user.Name)));
                sections.Add(("database roles", [.. user.Roles.Select(role => DbMember(role, user.Name, v))]));
                sections.Add(("database permissions", Permissions(db.Permissions, user.Name, list)));
            }
        }

        return Sections(sections);
    }

    public static string RenderServerRole(ServerRoleMeta r, SqlServerVersion v) => RenderServerRole(r, v, out _);

    public static string RenderServerRole(ServerRoleMeta r, SqlServerVersion v, out IReadOnlyList<string> warnings)
    {
        var list = new List<string>();
        warnings = list;
        var create = new List<string>();
        if (!r.IsFixed && !IsPublic(r.Name))
        {
            if (v.Major >= 11)
            {
                create.Add($"CREATE SERVER ROLE {Sql.Q(r.Name)};");
            }
            else
            {
                return Warn(list, $"user-defined server roles require SQL Server 2012 or later; {r.Name} is not scripted.");
            }
        }

        var script = Sections([
            ("create", create),
            ("members", [.. r.Members.Select(m => ServerMember(r.Name, m, v))]),
            ("permissions", Permissions(r.Permissions, r.Name, list)),
        ]);
        return script.Length > 0 ? script : NothingToScript(r.Name);
    }

    public static string RenderDatabaseUser(DatabaseUserMeta u, SqlServerVersion v, out IReadOnlyList<string> warnings)
    {
        var list = new List<string>();
        warnings = list;

        if (IsBuiltInUser(u.Name))
        {
            var message = Sql.CommentSafe($"{u.Name} is a built-in principal and is not scripted.");
            list.Add(message);
            return "-- " + message;
        }

        if (u.Type is 'C' or 'K')
        {
            return Warn(list, $"{u.Name} is mapped to a certificate/asymmetric key and is not scripted.");
        }

        if (u.Type is not ('S' or 'U' or 'G'))
        {
            return Warn(list, $"{u.Name} is a user of unsupported type '{u.Type}' (external/Entra) and is not scripted.");
        }

        return Sections([
            ("create", UserCreate(u, list)),
            ("owned schemas", OwnedSchemas(u.OwnedSchemas, u.Name)),
            ("roles", [.. u.Roles.Select(role => DbMember(role, u.Name, v))]),
            ("permissions", Permissions(u.Permissions, u.Name, list)),
        ]);
    }

    public static string RenderDatabaseRole(DatabaseRoleMeta r, SqlServerVersion v) => RenderDatabaseRole(r, v, out _);

    public static string RenderDatabaseRole(DatabaseRoleMeta r, SqlServerVersion v, out IReadOnlyList<string> warnings)
    {
        var list = new List<string>();
        warnings = list;
        var create = new List<string>();
        if (r.IsApplicationRole)
        {
            var statement = $"CREATE APPLICATION ROLE {Sql.Q(r.Name)} WITH PASSWORD = {PasswordPlaceholder}";
            if (r.DefaultSchema is not null)
            {
                statement += $", DEFAULT_SCHEMA = {Sql.Q(r.DefaultSchema)}";
            }

            create.Add(statement + ";");
        }
        else if (!r.IsFixed && !IsPublic(r.Name))
        {
            create.Add($"CREATE ROLE {Sql.Q(r.Name)}" + (r.Owner is not null ? $" AUTHORIZATION {Sql.Q(r.Owner)}" : "") + ";");
        }

        var script = Sections([
            ("create", create),
            // A fixed role owns its built-in schema of the same name (db_datareader owns [db_datareader]): not scripted.
            ("owned schemas", OwnedSchemas(r.IsFixed ? r.OwnedSchemas?.Where(s => !string.Equals(s, r.Name, StringComparison.OrdinalIgnoreCase)).ToList() : r.OwnedSchemas, r.Name)),
            ("members", [.. r.Members.Select(m => DbMember(r.Name, m, v))]),
            ("permissions", Permissions(r.Permissions, r.Name, list)),
        ]);
        return script.Length > 0 ? script : NothingToScript(r.Name);
    }

    /// <summary>
    /// GRANT / DENY / REVOKE statements, one per securable and state: permissions on the same securable are listed
    /// together (<c>GRANT SELECT, INSERT ON [dbo].[T] TO [r];</c>). Unsupported securable classes become warnings.
    /// </summary>
    internal static List<string> Permissions(IReadOnlyList<PermissionMeta>? permissions, string grantee, List<string> warnings)
    {
        var statements = new List<string>();
        if (permissions is null)
        {
            return statements;
        }

        foreach (var skipped in permissions.Where(p => p.Unsupported is not null))
        {
            statements.Add(Warn(warnings, $"{skipped.Permission} on a {skipped.Unsupported} securable is not scripted."));
        }

        foreach (var group in permissions.Where(p => p.Unsupported is null).GroupBy(p => (p.State, p.On)))
        {
            var names = string.Join(", ", group.Select(p => p.Permission).Distinct(StringComparer.OrdinalIgnoreCase));
            var on = group.Key.On is null ? "" : " ON " + group.Key.On;
            statements.Add(group.Key.State switch
            {
                'D' => $"DENY {names}{on} TO {Sql.Q(grantee)};",
                'R' => $"REVOKE {names}{on} FROM {Sql.Q(grantee)};",
                'W' => $"GRANT {names}{on} TO {Sql.Q(grantee)} WITH GRANT OPTION;",
                _ => $"GRANT {names}{on} TO {Sql.Q(grantee)};",
            });
        }

        return statements;
    }

    /// <summary>Joins non-empty sections as <c>--title</c> lines followed by their statements, separated by a blank line.</summary>
    private static string Sections(IEnumerable<(string Title, IReadOnlyList<string> Lines)> sections)
    {
        var parts = sections.Where(s => s.Lines.Count > 0)
            .Select(s => "--" + Sql.CommentSafe(s.Title) + Sep + string.Join(Sep, s.Lines));
        return string.Join(Sep + Sep, parts);
    }

    private static List<string> OwnedSchemas(IReadOnlyList<string>? schemas, string owner) =>
        [.. (schemas ?? []).Select(s => $"ALTER AUTHORIZATION ON SCHEMA::{Sql.Q(s)} TO {Sql.Q(owner)};")];

    /// <summary>The CREATE USER statement (and its warnings) of a user that is not built in.</summary>
    private static List<string> UserCreate(DatabaseUserMeta u, List<string> list)
    {
        var lines = new List<string>();
        var sb = new StringBuilder($"CREATE USER {Sql.Q(u.Name)}");
        if (u.HasDatabasePassword)
        {
            // Contained-database user (authentication_type = DATABASE): the password lives in the database, not in a login.
            var safe = Sql.CommentSafe($"{u.Name} is a contained-database user with a password; the password is not scripted - set it before running.");
            list.Add(safe);
            lines.Add("-- WARNING: " + safe);
            sb.Append(" WITH PASSWORD = ").Append(PasswordPlaceholder);
            if (u.DefaultSchema is not null)
            {
                sb.Append(", DEFAULT_SCHEMA = ").Append(Sql.Q(u.DefaultSchema));
            }
        }
        else if (u.WithoutLogin || (u.Type == 'S' && u.LoginName is null))
        {
            if (!u.WithoutLogin)
            {
                lines.Add(Warn(list, $"user {Sql.Q(u.Name)} has no matching login; scripted as CREATE USER {Sql.Q(u.Name)} WITHOUT LOGIN"));
            }

            sb.Append(" WITHOUT LOGIN");
        }
        else if (u.LoginName is not null)
        {
            sb.Append(" FOR LOGIN ").Append(Sql.Q(u.LoginName));
        }

        if (!u.HasDatabasePassword && u.DefaultSchema is not null)
        {
            sb.Append(" WITH DEFAULT_SCHEMA = ").Append(Sql.Q(u.DefaultSchema));
        }

        lines.Add(sb.Append(';').ToString());
        return lines;
    }

    /// <summary>dbo, guest, sys and INFORMATION_SCHEMA exist in every database and cannot be created.</summary>
    private static bool IsBuiltInUser(string name) =>
        name.ToUpperInvariant() is "DBO" or "GUEST" or "SYS" or "INFORMATION_SCHEMA";

    /// <summary>The built-in <c>public</c> role (database principal_id 0, server principal_id 2) always exists and is never created.</summary>
    private static bool IsPublic(string roleName) => string.Equals(roleName, "public", StringComparison.OrdinalIgnoreCase);

    private static string NothingToScript(string roleName) =>
        "-- " + Sql.CommentSafe($"{Sql.Q(roleName)} is a built-in role with no explicit members or permissions; nothing to script.");

    private static string DbMember(string role, string member, SqlServerVersion v) =>
        v.SupportsAlterRoleAddMember
            ? $"ALTER ROLE {Sql.Q(role)} ADD MEMBER {Sql.Q(member)};"
            : $"EXEC sys.sp_addrolemember @rolename = {Sql.N(role)}, @membername = {Sql.N(member)};";

    private static string ServerMember(string role, string member, SqlServerVersion v) =>
        v.SupportsAlterRoleAddMember
            ? $"ALTER SERVER ROLE {Sql.Q(role)} ADD MEMBER {Sql.Q(member)};"
            : $"EXEC sys.sp_addsrvrolemember @loginame = {Sql.N(member)}, @rolename = {Sql.N(role)};";

    private static void AddLoginOptions(List<string> options, LoginMeta l)
    {
        if (l.DefaultDatabase is not null)
        {
            options.Add("DEFAULT_DATABASE = " + Sql.Q(l.DefaultDatabase));
        }

        if (l.DefaultLanguage is not null)
        {
            options.Add("DEFAULT_LANGUAGE = " + Sql.Q(l.DefaultLanguage));
        }
    }

    private static string Warn(List<string> warnings, string message)
    {
        var safe = Sql.CommentSafe(message);
        warnings.Add(safe);
        return "-- WARNING: " + safe;
    }
}
