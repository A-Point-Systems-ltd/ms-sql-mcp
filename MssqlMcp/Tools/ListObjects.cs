// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.

using System.ComponentModel;
using ModelContextProtocol.Server;

namespace Mssql.McpServer;

public partial class Tools
{
    [McpServerTool(
        Name = ToolNames.ListObjects,
        Title = "List Objects",
        ReadOnly = true,
        Idempotent = true,
        Destructive = false),
        Description("Lists database objects by logical type via one tool. Supported objectType values: Table, View, StoredProcedure, TableFunction, ScalarFunction, Function, TableTrigger, SysObject, DatabaseTrigger, Type (user-defined alias/table types), Login, ServerRole, DatabaseUser, DatabaseRole. Security types return principal names (which may identify people) and role membership - never passwords. Optional partialName filters with a LIKE search on object name and schema.name. For objectType='SysObject', optional sysObjectType filters by sys.objects type code (e.g., 'U','V','P','FN')." + MultiConnectionNote)]
    public async Task<DbOperationResult> ListObjects(
        [Description("Logical object type to list: Table, View, StoredProcedure, TableFunction, ScalarFunction, Function, TableTrigger, SysObject, DatabaseTrigger, Type, Login, ServerRole, DatabaseUser, or DatabaseRole.")] string objectType,
        [Description("Optional partial name filter (substring match). Matches object name and qualified schema.name (e.g. 'Doc' matches dbo.Documents). Pass null for no filter.")] string? partialName = null,
        [Description("Optional sys.objects type code used only when objectType='SysObject' (e.g., 'U','V','P','FN','IF','TF','TR'). Pass null to list all user-defined sys.objects.")] string? sysObjectType = null,
        [Description(ConnectionParamDescription)] string? connection = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(objectType))
        {
            return new DbOperationResult(success: false, error: "objectType is required.");
        }

        var normalized = NormalizeObjectType(objectType);
        return normalized switch
        {
            "table" => await ListTables(partialName, cancellationToken).ConfigureAwait(false),
            "view" => await ListViews(partialName, cancellationToken).ConfigureAwait(false),
            "storedprocedure" => await ListStoredProcedures(partialName, cancellationToken).ConfigureAwait(false),
            "tablefunction" => await ListTableFunctions(partialName, cancellationToken).ConfigureAwait(false),
            "scalarfunction" => await ListScalarFunctions(partialName, cancellationToken).ConfigureAwait(false),
            "function" => await ListAllFunctions(partialName, cancellationToken).ConfigureAwait(false),
            "tabletrigger" => await ListTableTriggers(partialName, cancellationToken).ConfigureAwait(false),
            "databasetrigger" => await ListDatabaseTriggers(partialName, cancellationToken).ConfigureAwait(false),
            "type" => await ListUserDefinedTypes(partialName, cancellationToken).ConfigureAwait(false),
            "login" => await ListLogins(partialName, cancellationToken).ConfigureAwait(false),
            "serverrole" => await ListServerRoles(partialName, cancellationToken).ConfigureAwait(false),
            "databaseuser" => await ListDatabaseUsers(partialName, cancellationToken).ConfigureAwait(false),
            "databaserole" => await ListDatabaseRoles(partialName, cancellationToken).ConfigureAwait(false),
            "sysobject" => await ListSysObjects(sysObjectType, partialName, cancellationToken).ConfigureAwait(false),
            _ => new DbOperationResult(
                success: false,
                error: "Unsupported objectType. Use one of: Table, View, StoredProcedure, TableFunction, ScalarFunction, Function, TableTrigger, SysObject, DatabaseTrigger, Type, Login, ServerRole, DatabaseUser, DatabaseRole.")
        };
    }

    private static string NormalizeObjectType(string objectType)
    {
        var compact = objectType.Trim().Replace("_", string.Empty, StringComparison.Ordinal).Replace("-", string.Empty, StringComparison.Ordinal);
        return compact.ToLowerInvariant() switch
        {
            "tables" => "table",
            "views" => "view",
            "storedprocedures" => "storedprocedure",
            "procedure" => "storedprocedure",
            "procedures" => "storedprocedure",
            "proc" => "storedprocedure",
            "procs" => "storedprocedure",
            "tablefunctions" => "tablefunction",
            "tvf" => "tablefunction",
            "scalarfunctions" => "scalarfunction",
            "scalar" => "scalarfunction",
            "functions" => "function",
            "tabletriggers" => "tabletrigger",
            "triggers" => "tabletrigger",
            "dbtrigger" or "databasetriggers" => "databasetrigger",
            "types" or "userdefinedtype" or "userdefinedtypes" => "type",
            "logins" => "login",
            "serverroles" => "serverrole",
            "users" or "databaseusers" => "databaseuser",
            "roles" or "databaseroles" => "databaserole",
            "sysobjects" => "sysobject",
            "objects" => "sysobject",
            _ => compact.ToLowerInvariant()
        };
    }

    private async Task<DbOperationResult> ListAllFunctions(string? partialName, CancellationToken cancellationToken)
    {
        var scalarResult = await ListScalarFunctions(partialName, cancellationToken).ConfigureAwait(false);
        if (!scalarResult.Success)
        {
            return scalarResult;
        }

        var tableResult = await ListTableFunctions(partialName, cancellationToken).ConfigureAwait(false);
        if (!tableResult.Success)
        {
            return tableResult;
        }

        var combined = new List<Dictionary<string, object?>>();
        AppendFunctionRows(combined, scalarResult.Data, "ScalarFunction");
        AppendFunctionRows(combined, tableResult.Data, "TableFunction");

        return new DbOperationResult(success: true, data: combined);
    }

    private static void AppendFunctionRows(List<Dictionary<string, object?>> destination, object? sourceData, string functionKind)
    {
        if (sourceData is not IEnumerable<Dictionary<string, object?>> rows)
        {
            return;
        }

        foreach (var row in rows)
        {
            var copy = new Dictionary<string, object?>(row)
            {
                ["function_kind"] = functionKind
            };
            destination.Add(copy);
        }
    }
}
