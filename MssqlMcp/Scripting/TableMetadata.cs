// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.

namespace Mssql.McpServer.Scripting;

internal sealed record ColumnMeta(
    string Name, string TypeName, string? UserTypeSchema, short MaxLength, byte Precision, byte Scale,
    bool IsNullable, string? Collation, bool IsIdentity, string? IdentitySeed, string? IdentityIncrement,
    bool IsComputed, string? ComputedDefinition, bool IsPersisted,
    string? DefaultName, string? DefaultDefinition, bool IsRowGuidCol, bool IsSparse,
    bool IdentityNotForReplication = false);

internal sealed record IndexColumnMeta(string Name, bool IsDescending, bool IsIncluded);

/// <summary>
/// Type: 1 clustered, 2 nonclustered (others are warnings). IsPrimaryKey / IsUniqueConstraint mark constraint-backed indexes.
/// FileGroup is set only when the index lives on a filegroup other than the database default.
/// </summary>
internal sealed record IndexMeta(
    string Name, byte Type, bool IsUnique, bool IsPrimaryKey, bool IsUniqueConstraint,
    string? FilterDefinition, bool IsDisabled, IReadOnlyList<IndexColumnMeta> Columns,
    bool IgnoreDupKey = false, string? FileGroup = null);

internal sealed record CheckMeta(string Name, string Definition, bool IsDisabled, bool IsNotTrusted, bool NotForReplication = false);

internal sealed record ForeignKeyMeta(
    string Name, string Schema, string Table, IReadOnlyList<string> Columns,
    string RefSchema, string RefTable, IReadOnlyList<string> RefColumns,
    string DeleteAction, string UpdateAction, bool NotForReplication, bool IsDisabled, bool IsNotTrusted);

internal sealed record TableMeta(
    string Schema, string Name, string? DatabaseCollation, string? Description,
    IReadOnlyList<ColumnMeta> Columns, IReadOnlyList<IndexMeta> Indexes, IReadOnlyList<CheckMeta> Checks,
    IReadOnlyList<ForeignKeyMeta> ForeignKeys, IReadOnlyList<string> Warnings,
    string? FileGroup = null);
