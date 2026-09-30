// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.

using System.ComponentModel;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using Mssql.McpServer.Scripting;

namespace Mssql.McpServer;

public partial class Tools
{
    [McpServerTool(
        Name = ToolNames.ScriptObject,
        Title = "Script Object",
        ReadOnly = true,
        Idempotent = true,
        Destructive = false),
        Description("Returns ready-to-run T-SQL DDL for one object. objectType: Table (CREATE TABLE with defaults, PK/unique/check constraints, then foreign keys, indexes and description), View (definition + its indexes), Index (name + parent table/view), ForeignKey, TableTrigger, StoredProcedure, TableFunction, ScalarFunction, DatabaseTrigger, Type (alias or table type), Login, ServerRole, DatabaseUser, DatabaseRole. Procedures, functions and triggers are returned as CREATE OR ALTER on SQL Server 2016 SP1+ and as ALTER on older servers (see 'form'). Passwords, hashes and SIDs are never scripted - SQL logins and application roles contain a password placeholder. Features the generator cannot express (partitioning, compression, XML/columnstore indexes, encrypted modules, ...) are listed in 'warnings' and as comments. Read-only: nothing is executed." + MultiConnectionNote)]
    public async Task<DbOperationResult> ScriptObject(
        [Description("Object type to script: Table, View, Index, ForeignKey, TableTrigger, StoredProcedure, TableFunction, ScalarFunction, DatabaseTrigger, Type, Login, ServerRole, DatabaseUser or DatabaseRole.")] string objectType,
        [Description("Object name: '[schema.]name' for schema-scoped objects (parts may be [bracketed] or \"quoted\"); the plain principal or trigger name for Login, ServerRole, DatabaseUser, DatabaseRole, DatabaseTrigger and Index.")] string name,
        [Description("Only for objectType='Index': the '[schema.]table' or view that owns the index. Pass null otherwise.")] string? parent = null,
        [Description(ConnectionParamDescription)] string? connection = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var conn = await _connectionFactory.GetOpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            var (result, error) = await ObjectScripter.ScriptAsync(conn, objectType, name, parent, cancellationToken).ConfigureAwait(false);
            if (result is null)
            {
                return new DbOperationResult(success: false, error: error ?? "Object could not be scripted.");
            }

            return new DbOperationResult(success: true, data: new
            {
                objectType = result.ObjectType,
                schema = result.Schema,
                name = result.Name,
                form = result.Form.ToString(),
                ddl = result.Ddl,
                warnings = result.Warnings,
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "{Tool} failed: {Message}", ToolNames.ScriptObject, ex.Message);
            return new DbOperationResult(success: false, error: ex.Message);
        }
    }
}
