// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.

using System.ComponentModel;
using ModelContextProtocol.Server;

namespace Mssql.McpServer;

public partial class Tools
{
    [McpServerTool(
        Title = "Get Object",
        ReadOnly = true,
        Idempotent = true,
        Destructive = false),
        Description("Returns metadata, parameters (where applicable), and T-SQL definition for a programmable object. objectType: StoredProcedure, Function, or Trigger. MANDATORY POST-CALL CONTRACT (MCP-Insight-Enrichment-v1): If the response contains insightEnrichment.required=true (or _agentDirective / pendingEnrichments), call UpsertInsight using insightEnrichment.nextAction.args before your final answer, and introspect relatedObjectsToIntrospect. When USE_INSIGHTS_LAYER!=false the response includes insight metadata.")]
    public async Task<DbOperationResult> GetObject(
        [Description("Object type: StoredProcedure, Function, or Trigger.")] string objectType,
        [Description("Object name. Stored procedures/functions: 'name' or 'schema.name'. Triggers also accept 'schema.table.name' (e.g. dbo.Orders.trgAudit).")] string name)
    {
        if (string.IsNullOrWhiteSpace(objectType))
        {
            return new DbOperationResult(success: false, error: "objectType is required.");
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            return new DbOperationResult(success: false, error: "name is required.");
        }

        var normalized = NormalizeGetObjectType(objectType);
        return normalized switch
        {
            "storedprocedure" => await GetStoredProc(name),
            "function" => await GetFunction(name),
            "trigger" => await GetTrigger(name),
            _ => new DbOperationResult(
                success: false,
                error: "Unsupported objectType. Use one of: StoredProcedure, Function, Trigger.")
        };
    }

    private static string NormalizeGetObjectType(string objectType)
    {
        var compact = objectType.Trim().Replace("_", string.Empty, StringComparison.Ordinal).Replace("-", string.Empty, StringComparison.Ordinal);
        return compact.ToLowerInvariant() switch
        {
            "storedprocedures" => "storedprocedure",
            "procedure" => "storedprocedure",
            "procedures" => "storedprocedure",
            "proc" => "storedprocedure",
            "procs" => "storedprocedure",
            "sp" => "storedprocedure",
            "sps" => "storedprocedure",
            "scalarfunction" => "function",
            "tablefunction" => "function",
            "scalarfunctions" => "function",
            "tablefunctions" => "function",
            "functions" => "function",
            "fn" => "function",
            "triggers" => "trigger",
            "tr" => "trigger",
            _ => compact.ToLowerInvariant()
        };
    }
}
