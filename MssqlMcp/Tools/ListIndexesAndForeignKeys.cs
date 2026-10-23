// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.

namespace Mssql.McpServer;

/// <summary>
/// list_objects support for indexes and foreign keys across the database, each with its parent table or view, so a
/// caller can find a child object by name without describing every table. partialName matches the object's own name.
/// </summary>
public partial class Tools
{
    // Heaps (type 0) and hypothetical (DTA) indexes have no scriptable index; PK/UQ constraint indexes are included.
    private const string ListIndexesQuery = @"SELECT SCHEMA_NAME(o.schema_id) AS [schema], o.name AS parent,
            CASE o.type WHEN 'V' THEN 'View' ELSE 'Table' END AS parent_type, i.name, i.type_desc AS type
        FROM sys.indexes i
        INNER JOIN sys.objects o ON o.object_id = i.object_id
        WHERE o.type IN ('U','V') AND o.is_ms_shipped = 0 AND i.type > 0 AND i.is_hypothetical = 0
            AND (@NamePattern IS NULL OR i.name LIKE @NamePattern)
        ORDER BY SCHEMA_NAME(o.schema_id), o.name, i.name";

    private const string ListForeignKeysQuery = @"SELECT SCHEMA_NAME(fk.schema_id) AS [schema], OBJECT_NAME(fk.parent_object_id) AS parent,
            fk.name, fk.is_disabled
        FROM sys.foreign_keys fk
        WHERE fk.is_ms_shipped = 0
            AND (@NamePattern IS NULL OR fk.name LIKE @NamePattern OR (SCHEMA_NAME(fk.schema_id) + '.' + fk.name) LIKE @NamePattern)
        ORDER BY SCHEMA_NAME(fk.schema_id), OBJECT_NAME(fk.parent_object_id), fk.name";

    private Task<DbOperationResult> ListIndexes(string? partialName, CancellationToken ct) =>
        RunCatalogListAsync("Index", _ => ListIndexesQuery, partialName,
            r => new Dictionary<string, object?> { ["schema"] = r[0], ["parent"] = r[1], ["parentType"] = r[2], ["name"] = r[3], ["type"] = r[4] }, ct);

    private Task<DbOperationResult> ListForeignKeys(string? partialName, CancellationToken ct) =>
        RunCatalogListAsync("ForeignKey", _ => ListForeignKeysQuery, partialName,
            r => new Dictionary<string, object?> { ["schema"] = r[0], ["parent"] = r[1], ["name"] = r[2], ["isDisabled"] = r[3] }, ct);
}
