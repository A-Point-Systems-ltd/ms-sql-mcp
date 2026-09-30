// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.

using System.Text;
using Microsoft.Data.SqlClient;

namespace Mssql.McpServer.Scripting;

/// <summary>
/// Resolves an object by type and name, reads its catalog metadata and renders ready-to-run DDL.
/// Read-only: only sys.* views and SERVERPROPERTY are queried. Every warning a renderer produces is returned in
/// <see cref="ScriptResult.Warnings"/> and also appears as a <c>-- WARNING:</c> comment in the script.
/// </summary>
internal static class ObjectScripter
{
    public const string SupportedTypes =
        "Table, View, Index, ForeignKey, TableTrigger, StoredProcedure, TableFunction, ScalarFunction, DatabaseTrigger, Type, Login, ServerRole, DatabaseUser, DatabaseRole";

    private const string Go = "\r\nGO\r\n";

    private const int MaxSysnameLength = 128;

    public static async Task<(ScriptResult? result, string? error)> ScriptAsync(
        SqlConnection conn, string objectType, string name, string? parent, CancellationToken ct)
    {
        var type = NormalizeObjectType(objectType);
        if (type is null)
        {
            return (null, $"Unsupported objectType '{objectType}'. Use one of: {SupportedTypes}.");
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            return (null, "name is required.");
        }

        if (ValidateNames(type, name, parent) is { } invalid)
        {
            return (null, invalid);
        }

        var version = await CatalogReader.GetVersionAsync(conn, ct).ConfigureAwait(false);
        return type switch
        {
            "Table" => await ScriptTableAsync(conn, name, version, ct).ConfigureAwait(false),
            "View" => await ScriptViewAsync(conn, name, ct).ConfigureAwait(false),
            "Index" => await ScriptIndexAsync(conn, name, parent, ct).ConfigureAwait(false),
            "ForeignKey" => await ScriptForeignKeyAsync(conn, name, ct).ConfigureAwait(false),
            "TableTrigger" => await ScriptModuleAsync(conn, type, name, Tools.TriggerObjectTypes, version, ct).ConfigureAwait(false),
            "StoredProcedure" => await ScriptModuleAsync(conn, type, name, Tools.ProcedureObjectTypes, version, ct).ConfigureAwait(false),
            "TableFunction" => await ScriptModuleAsync(conn, type, name, Tools.TableFunctionObjectTypes, version, ct).ConfigureAwait(false),
            "ScalarFunction" => await ScriptModuleAsync(conn, type, name, Tools.ScalarFunctionObjectTypes, version, ct).ConfigureAwait(false),
            "DatabaseTrigger" => await ScriptDatabaseTriggerAsync(conn, name, version, ct).ConfigureAwait(false),
            "Type" => await ScriptTypeAsync(conn, name, version, ct).ConfigureAwait(false),
            _ => await ScriptPrincipalAsync(conn, type, name, version, ct).ConfigureAwait(false),
        };
    }

    /// <summary>Case-insensitive; '_', '-' and spaces are ignored and plural / short aliases are accepted. Null when unsupported.</summary>
    internal static string? NormalizeObjectType(string? objectType)
    {
        if (string.IsNullOrWhiteSpace(objectType))
        {
            return null;
        }

        var compact = objectType.Trim().Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal).Replace(" ", string.Empty, StringComparison.Ordinal);
        return compact.ToLowerInvariant() switch
        {
            "table" or "tables" => "Table",
            "view" or "views" => "View",
            "index" or "indexes" or "indices" => "Index",
            "foreignkey" or "foreignkeys" or "fk" => "ForeignKey",
            "tabletrigger" or "tabletriggers" or "trigger" or "triggers" => "TableTrigger",
            "storedprocedure" or "storedprocedures" or "procedure" or "procedures" or "proc" or "procs" => "StoredProcedure",
            "tablefunction" or "tablefunctions" or "tvf" => "TableFunction",
            "scalarfunction" or "scalarfunctions" or "scalar" => "ScalarFunction",
            "databasetrigger" or "databasetriggers" or "ddltrigger" => "DatabaseTrigger",
            "type" or "types" or "usertype" or "userdefinedtype" or "udt" => "Type",
            "login" or "logins" => "Login",
            "serverrole" or "serverroles" => "ServerRole",
            "databaseuser" or "databaseusers" or "user" or "users" => "DatabaseUser",
            "databaserole" or "databaseroles" or "role" or "roles" => "DatabaseRole",
            _ => null,
        };
    }

    private static string NotFound(string type, string name) => $"{type} '{name.Trim()}' not found (or not visible to this login).";

    /// <summary>
    /// A single, non-schema-scoped name (principal, index, database trigger). "[a.b]" unquotes to "a.b"; an unquoted
    /// name that is not a single identifier (e.g. a login "john.doe") is taken verbatim.
    /// </summary>
    private static string SingleName(string input) =>
        ObjectNameParser.TryParseParts(input, out var parts, out _) && parts.Count == 1 ? parts[0] : input.Trim();

    private static bool IsSingleNameType(string type) =>
        type is "Index" or "DatabaseTrigger" or "Login" or "ServerRole" or "DatabaseUser" or "DatabaseRole";

    /// <summary>
    /// Validates name and parent before any query. Names longer than sysname (128) are rejected here so they are never
    /// silently truncated by an nvarchar(128) parameter; multi-part names use the parser's own per-part limit.
    /// </summary>
    private static string? ValidateNames(string type, string name, string? parent)
    {
        string? error;
        if (IsSingleNameType(type))
        {
            var single = SingleName(name);
            if (single.Length > MaxSysnameLength)
            {
                return $"Invalid name '{single[..32]}...': {single.Length} characters exceeds the {MaxSysnameLength}-character sysname limit.";
            }
        }
        else if (!(type == "TableTrigger"
            ? ObjectNameParser.TryParseTrigger(name, out _, out error)
            : ObjectNameParser.TryParse(name, out _, out error)))
        {
            return error;
        }

        return type == "Index" && !string.IsNullOrWhiteSpace(parent) && !ObjectNameParser.TryParse(parent, out _, out error) ? error : null;
    }

    private static async Task<(ResolvedObject? obj, string? error)> ResolveAsync(
        SqlConnection conn, string type, string name, string[] typeCodes, bool trigger, CancellationToken ct)
    {
        var ok = trigger
            ? ObjectNameParser.TryParseTrigger(name, out var parts, out var error)
            : ObjectNameParser.TryParse(name, out parts, out error);
        if (!ok)
        {
            return (null, error);
        }

        var resolved = await Tools.ResolveObjectAsync(conn, parts, typeCodes, ct).ConfigureAwait(false);
        return resolved is null ? (null, NotFound(type, name)) : (resolved, null);
    }

    private static async Task<(ScriptResult?, string?)> ScriptTableAsync(SqlConnection conn, string name, SqlServerVersion version, CancellationToken ct)
    {
        var (obj, error) = await ResolveAsync(conn, "Table", name, Tools.TableObjectTypes, false, ct).ConfigureAwait(false);
        if (obj is not { } o)
        {
            return (null, error);
        }

        var meta = await CatalogReader.ReadTableAsync(conn, o.ObjectId, version, ct).ConfigureAwait(false);
        if (meta is null)
        {
            return (null, NotFound("Table", name));
        }

        var ddl = TableDdlRenderer.RenderTable(meta, includeDependents: true, out var warnings);
        return (new ScriptResult("Table", o.Schema, o.Name, DdlForm.Create, ddl, warnings), null);
    }

    private static async Task<(ScriptResult?, string?)> ScriptViewAsync(SqlConnection conn, string name, CancellationToken ct)
    {
        var (obj, error) = await ResolveAsync(conn, "View", name, Tools.ViewObjectTypes, false, ct).ConfigureAwait(false);
        if (obj is not { } o)
        {
            return (null, error);
        }

        var module = await CatalogReader.ReadModuleAsync(conn, o.ObjectId, ct).ConfigureAwait(false);
        if (UnavailableModule("View", o.Schema, o.Name, module, DdlForm.Create) is { } unavailable)
        {
            return (unavailable, null);
        }

        var (definition, ansiNulls, quotedIdentifier) = module!.Value;
        var warnings = new List<string>();
        var sb = new StringBuilder(SetHeader(ansiNulls, quotedIdentifier)).Append(definition).Append("\r\nGO");
        foreach (var ix in await CatalogReader.ReadIndexesAsync(conn, o.ObjectId, null, ct).ConfigureAwait(false))
        {
            sb.Append("\r\n").Append(TableDdlRenderer.RenderIndex(o.Schema, o.Name, ix, out var w)).Append("\r\nGO");
            if (w is not null)
            {
                warnings.Add(Sql.CommentSafe(w));
            }
        }

        return (new ScriptResult("View", o.Schema, o.Name, DdlForm.Create, sb.ToString(), warnings), null);
    }

    private static async Task<(ScriptResult?, string?)> ScriptIndexAsync(SqlConnection conn, string name, string? parent, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(parent))
        {
            return (null, "parent (the [schema.]table or view that owns the index) is required for objectType Index.");
        }

        var (obj, error) = await ResolveAsync(conn, "Table or view", parent, [.. Tools.TableObjectTypes, .. Tools.ViewObjectTypes], false, ct).ConfigureAwait(false);
        if (obj is not { } o)
        {
            return (null, error);
        }

        var indexName = SingleName(name);
        var found = await CatalogReader.ReadIndexAsync(conn, o.ObjectId, indexName, ct).ConfigureAwait(false);
        if (found is not { } f)
        {
            return (null, $"Index '{indexName}' not found on '{parent.Trim()}' (or not visible to this login).");
        }

        var ddl = TableDdlRenderer.RenderIndex(f.schema, f.table, f.ix, out var warning);
        IReadOnlyList<string> warnings = warning is null ? [] : [Sql.CommentSafe(warning)];
        return (new ScriptResult("Index", f.schema, f.ix.Name, DdlForm.Create, ddl, warnings), null);
    }

    private static async Task<(ScriptResult?, string?)> ScriptForeignKeyAsync(SqlConnection conn, string name, CancellationToken ct)
    {
        var (obj, error) = await ResolveAsync(conn, "ForeignKey", name, Tools.ForeignKeyObjectTypes, false, ct).ConfigureAwait(false);
        if (obj is not { } o)
        {
            return (null, error);
        }

        var fk = await CatalogReader.ReadForeignKeyAsync(conn, o.ObjectId, ct).ConfigureAwait(false);
        return fk is null
            ? (null, NotFound("ForeignKey", name))
            : (new ScriptResult("ForeignKey", o.Schema, o.Name, DdlForm.Create, TableDdlRenderer.RenderForeignKey(fk), []), null);
    }

    private static async Task<(ScriptResult?, string?)> ScriptModuleAsync(
        SqlConnection conn, string type, string name, string[] typeCodes, SqlServerVersion version, CancellationToken ct)
    {
        var (obj, error) = await ResolveAsync(conn, type, name, typeCodes, trigger: type == "TableTrigger", ct).ConfigureAwait(false);
        if (obj is not { } o)
        {
            return (null, error);
        }

        var module = await CatalogReader.ReadModuleAsync(conn, o.ObjectId, ct).ConfigureAwait(false);
        return (ComposeProgrammable(type, o.Schema, o.Name, module, version), null);
    }

    private static async Task<(ScriptResult?, string?)> ScriptDatabaseTriggerAsync(SqlConnection conn, string name, SqlServerVersion version, CancellationToken ct)
    {
        var triggerName = SingleName(name);
        var module = await CatalogReader.ReadDatabaseTriggerAsync(conn, triggerName, ct).ConfigureAwait(false);
        return module is null
            ? (null, NotFound("DatabaseTrigger", name))
            : (ComposeProgrammable("DatabaseTrigger", null, triggerName, module, version), null);
    }

    /// <summary>SET headers + GO + the definition with its leading keyword rewritten for the server version + GO.</summary>
    private static ScriptResult ComposeProgrammable(string type, string? schema, string name, (string? definition, bool ansiNulls, bool quotedIdentifier)? module, SqlServerVersion version)
    {
        var form = ModuleFormRewriter.ProgrammableFormFor(version);
        if (UnavailableModule(type, schema, name, module, form) is { } unavailable)
        {
            return unavailable;
        }

        var (definition, ansiNulls, quotedIdentifier) = module!.Value;
        var body = ModuleFormRewriter.Rewrite(definition!, form, quotedIdentifier, out var warning);
        var warnings = new List<string>();
        var sb = new StringBuilder();
        if (warning is not null)
        {
            var safe = Sql.CommentSafe(warning);
            warnings.Add(safe);
            sb.Append("-- WARNING: ").Append(safe).Append("\r\n");
        }

        sb.Append(SetHeader(ansiNulls, quotedIdentifier)).Append(body).Append("\r\nGO");
        return new ScriptResult(type, schema, name, form, sb.ToString(), warnings);
    }

    /// <summary>A warning-only result for a module without a T-SQL definition (CLR / extended: no row; encrypted: NULL definition).</summary>
    private static ScriptResult? UnavailableModule(string type, string? schema, string name, (string? definition, bool, bool)? module, DdlForm form)
    {
        var display = schema is null ? Sql.Q(name) : Sql.Qualified(schema, name);
        string? warning = module switch
        {
            null => $"{display} has no T-SQL definition (CLR or extended object) and is not scripted.",
            { definition: null } => $"{display} is WITH ENCRYPTION; definition not available.",
            _ => null,
        };
        return warning is null ? null : WarningOnly(type, schema, name, form, warning);
    }

    private static ScriptResult WarningOnly(string type, string? schema, string name, DdlForm form, string warning)
    {
        var safe = Sql.CommentSafe(warning);
        return new ScriptResult(type, schema, name, form, "-- WARNING: " + safe, [safe]);
    }

    private static string SetHeader(bool ansiNulls, bool quotedIdentifier) =>
        $"SET ANSI_NULLS {(ansiNulls ? "ON" : "OFF")}{Go}SET QUOTED_IDENTIFIER {(quotedIdentifier ? "ON" : "OFF")}{Go}";

    private static async Task<(ScriptResult?, string?)> ScriptTypeAsync(SqlConnection conn, string name, SqlServerVersion version, CancellationToken ct)
    {
        if (!ObjectNameParser.TryParse(name, out var parts, out var error))
        {
            return (null, error);
        }

        if (parts.Database is not null && !string.Equals(parts.Database, conn.Database, StringComparison.OrdinalIgnoreCase))
        {
            return (null, NotFound("Type", name));
        }

        var found = await CatalogReader.ReadUserTypeAsync(conn, parts.Schema, parts.Name, version, ct).ConfigureAwait(false);
        if (found is not { } t)
        {
            return (null, NotFound("Type", name));
        }

        if (t.aliasBase is not null)
        {
            return (new ScriptResult("Type", t.schema, t.name, DdlForm.Create, TableDdlRenderer.RenderAliasType(t.schema, t.name, t.aliasBase), []), null);
        }

        if (t.tableShape is null)
        {
            return (WarningOnly("Type", t.schema, t.name, DdlForm.Create, $"{Sql.Qualified(t.schema, t.name)} is a CLR type and is not scripted."), null);
        }

        var ddl = TableDdlRenderer.RenderTableType(t.schema, t.name, t.tableShape, out var warnings);
        return (new ScriptResult("Type", t.schema, t.name, DdlForm.Create, ddl, warnings), null);
    }

    private static async Task<(ScriptResult?, string?)> ScriptPrincipalAsync(SqlConnection conn, string type, string name, SqlServerVersion version, CancellationToken ct)
    {
        var principal = SingleName(name);
        string? ddl = null;
        IReadOnlyList<string> warnings = [];
        switch (type)
        {
            case "Login":
                if (await CatalogReader.ReadLoginAsync(conn, principal, ct).ConfigureAwait(false) is { } login)
                {
                    ddl = SecurityDdlRenderer.RenderLogin(login, version, out warnings);
                }

                break;
            case "ServerRole":
                if (await CatalogReader.ReadServerRoleAsync(conn, principal, version, ct).ConfigureAwait(false) is { } serverRole)
                {
                    ddl = SecurityDdlRenderer.RenderServerRole(serverRole, version, out warnings);
                }

                break;
            case "DatabaseUser":
                if (await CatalogReader.ReadDatabaseUserAsync(conn, principal, ct).ConfigureAwait(false) is { } user)
                {
                    ddl = SecurityDdlRenderer.RenderDatabaseUser(user, version, out warnings);
                }

                break;
            default:
                if (await CatalogReader.ReadDatabaseRoleAsync(conn, principal, ct).ConfigureAwait(false) is { } role)
                {
                    ddl = SecurityDdlRenderer.RenderDatabaseRole(role, version);
                }

                break;
        }

        return ddl is null
            ? (null, NotFound(type, name))
            : (new ScriptResult(type, null, principal, DdlForm.Create, ddl, warnings), null);
    }
}
