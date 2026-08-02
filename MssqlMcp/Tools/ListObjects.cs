// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.

using System.ComponentModel;
using ModelContextProtocol.Server;

namespace Mssql.McpServer;

public partial class Tools
{
    [McpServerTool(
        Title = "List Objects",
        ReadOnly = true,
        Idempotent = true,
        Destructive = false),
        Description("Lists database objects by logical type via one tool. Supported objectType values: Table, View, StoredProcedure, TableFunction, ScalarFunction, Function, TableTrigger, SysObject. Optional partialName filters with a LIKE search on object name and schema.name. For objectType='SysObject', optional sysObjectType filters by sys.objects type code (e.g., 'U','V','P','FN').")]
    public async Task<DbOperationResult> ListObjects(
        [Description("Logical object type to list: Table, View, StoredProcedure, TableFunction, ScalarFunction, Function, TableTrigger, or SysObject.")] string objectType,
        [Description("Optional partial name filter (substring match). Matches object name and qualified schema.name (e.g. 'Doc' matches dbo.Documents). Pass null for no filter.")] string? partialName = null,
        [Description("Optional sys.objects type code used only when objectType='SysObject' (e.g., 'U','V','P','FN','IF','TF','TR'). Pass null to list all user-defined sys.objects.")] string? sysObjectType = null)
    {
        if (string.IsNullOrWhiteSpace(objectType))
        {
            return new DbOperationResult(success: false, error: "objectType is required.");
        }

        var normalized = NormalizeObjectType(objectType);
        return normalized switch
        {
            "table" => await ListTables(partialName),
            "view" => await ListViews(partialName),
            "storedprocedure" => await ListStoredProcedures(partialName),
            "tablefunction" => await ListTableFunctions(partialName),
            "scalarfunction" => await ListScalarFunctions(partialName),
            "function" => await ListAllFunctions(partialName),
            "tabletrigger" => await ListTableTriggers(partialName),
            "sysobject" => await ListSysObjects(sysObjectType, partialName),
            _ => new DbOperationResult(
                success: false,
                error: "Unsupported objectType. Use one of: Table, View, StoredProcedure, TableFunction, ScalarFunction, Function, TableTrigger, SysObject.")
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
            "sysobjects" => "sysobject",
            "objects" => "sysobject",
            _ => compact.ToLowerInvariant()
        };
    }

    private async Task<DbOperationResult> ListAllFunctions(string? partialName)
    {
        var scalarResult = await ListScalarFunctions(partialName);
        if (!scalarResult.Success)
        {
            return scalarResult;
        }

        var tableResult = await ListTableFunctions(partialName);
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
