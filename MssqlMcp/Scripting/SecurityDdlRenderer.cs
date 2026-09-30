// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.

using System.Text;

namespace Mssql.McpServer.Scripting;

/// <summary>Server login (Type: S sql, U windows user, G windows group, C certificate, K asymmetric key).</summary>
internal sealed record LoginMeta(
    string Name,
    char Type,
    bool IsDisabled,
    string? DefaultDatabase,
    string? DefaultLanguage,
    bool? CheckPolicy,
    bool? CheckExpiration,
    IReadOnlyList<string> ServerRoles);

internal sealed record ServerRoleMeta(string Name, bool IsFixed, IReadOnlyList<string> Members);

/// <summary>Database user (Type: S, U, G, E, X, C, K as in sys.database_principals.type).</summary>
internal sealed record DatabaseUserMeta(
    string Name,
    char Type,
    string? LoginName,
    string? DefaultSchema,
    bool WithoutLogin,
    IReadOnlyList<string> Roles);

internal sealed record DatabaseRoleMeta(
    string Name,
    bool IsFixed,
    bool IsApplicationRole,
    string? Owner,
    string? DefaultSchema,
    IReadOnlyList<string> Members);

/// <summary>Pure renderer for security principals. Never emits passwords, password hashes or SIDs.</summary>
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

        var stmts = new List<string> { head + ";" };
        if (l.IsDisabled)
        {
            stmts.Add($"ALTER LOGIN {Sql.Q(l.Name)} DISABLE;");
        }

        foreach (var role in l.ServerRoles)
        {
            stmts.Add(v.SupportsAlterRoleAddMember
                ? $"ALTER SERVER ROLE {Sql.Q(role)} ADD MEMBER {Sql.Q(l.Name)};"
                : $"EXEC sys.sp_addsrvrolemember @loginame = {Sql.N(l.Name)}, @rolename = {Sql.N(role)};");
        }

        return string.Join(Sep, stmts);
    }

    public static string RenderServerRole(ServerRoleMeta r, SqlServerVersion v) => RenderServerRole(r, v, out _);

    public static string RenderServerRole(ServerRoleMeta r, SqlServerVersion v, out IReadOnlyList<string> warnings)
    {
        var list = new List<string>();
        warnings = list;
        var stmts = new List<string>();
        if (!r.IsFixed && !IsPublic(r.Name))
        {
            if (v.Major >= 11)
            {
                stmts.Add($"CREATE SERVER ROLE {Sql.Q(r.Name)};");
            }
            else
            {
                return Warn(list, $"user-defined server roles require SQL Server 2012 or later; {r.Name} is not scripted.");
            }
        }

        foreach (var m in r.Members)
        {
            stmts.Add(v.SupportsAlterRoleAddMember
                ? $"ALTER SERVER ROLE {Sql.Q(r.Name)} ADD MEMBER {Sql.Q(m)};"
                : $"EXEC sys.sp_addsrvrolemember @loginame = {Sql.N(m)}, @rolename = {Sql.N(r.Name)};");
        }

        return stmts.Count > 0 ? string.Join(Sep, stmts) : NothingToScript(r.Name);
    }

    public static string RenderDatabaseUser(DatabaseUserMeta u, SqlServerVersion v, out IReadOnlyList<string> warnings)
    {
        var list = new List<string>();
        warnings = list;

        if (u.Type is 'C' or 'K')
        {
            return Warn(list, $"{u.Name} is mapped to a certificate/asymmetric key and is not scripted.");
        }

        if (u.Type is not ('S' or 'U' or 'G'))
        {
            return Warn(list, $"{u.Name} is a user of unsupported type '{u.Type}' (external/Entra) and is not scripted.");
        }

        var sb = new StringBuilder($"CREATE USER {Sql.Q(u.Name)}");
        if (u.WithoutLogin || (u.Type == 'S' && u.LoginName is null))
        {
            if (!u.WithoutLogin)
            {
                sb.Insert(0, Warn(list, $"user {Sql.Q(u.Name)} has no matching login; scripted as CREATE USER {Sql.Q(u.Name)} WITHOUT LOGIN") + Sep);
            }

            sb.Append(" WITHOUT LOGIN");
        }
        else if (u.LoginName is not null)
        {
            sb.Append(" FOR LOGIN ").Append(Sql.Q(u.LoginName));
        }

        if (u.DefaultSchema is not null)
        {
            sb.Append(" WITH DEFAULT_SCHEMA = ").Append(Sql.Q(u.DefaultSchema));
        }

        var stmts = new List<string> { sb.Append(';').ToString() };
        foreach (var role in u.Roles)
        {
            stmts.Add(DbMember(role, u.Name, v));
        }

        return string.Join(Sep, stmts);
    }

    public static string RenderDatabaseRole(DatabaseRoleMeta r, SqlServerVersion v)
    {
        var stmts = new List<string>();
        if (r.IsApplicationRole)
        {
            var create = $"CREATE APPLICATION ROLE {Sql.Q(r.Name)} WITH PASSWORD = {PasswordPlaceholder}";
            if (r.DefaultSchema is not null)
            {
                create += $", DEFAULT_SCHEMA = {Sql.Q(r.DefaultSchema)}";
            }

            stmts.Add(create + ";");
        }
        else if (!r.IsFixed && !IsPublic(r.Name))
        {
            stmts.Add($"CREATE ROLE {Sql.Q(r.Name)}" + (r.Owner is not null ? $" AUTHORIZATION {Sql.Q(r.Owner)}" : "") + ";");
        }

        foreach (var m in r.Members)
        {
            stmts.Add(DbMember(r.Name, m, v));
        }

        return stmts.Count > 0 ? string.Join(Sep, stmts) : NothingToScript(r.Name);
    }

    /// <summary>The built-in <c>public</c> role (database principal_id 0, server principal_id 2) always exists and is never created.</summary>
    private static bool IsPublic(string roleName) => string.Equals(roleName, "public", StringComparison.OrdinalIgnoreCase);

    private static string NothingToScript(string roleName) =>
        "-- " + Sql.CommentSafe($"{Sql.Q(roleName)} is a built-in role with no explicit members; nothing to script.");

    private static string DbMember(string role, string member, SqlServerVersion v) =>
        v.SupportsAlterRoleAddMember
            ? $"ALTER ROLE {Sql.Q(role)} ADD MEMBER {Sql.Q(member)};"
            : $"EXEC sys.sp_addrolemember @rolename = {Sql.N(role)}, @membername = {Sql.N(member)};";

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
