// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.

using System.Globalization;
using System.Text;

namespace Mssql.McpServer.Scripting;

/// <summary>Pure renderer from catalog metadata to SSMS-style T-SQL. No I/O.</summary>
internal static class TableDdlRenderer
{
    private const string Go = "\r\nGO\r\n";

    public static string FormatType(ColumnMeta c)
    {
        if (c.UserTypeSchema is not null)
        {
            return Sql.Qualified(c.UserTypeSchema, c.TypeName);
        }

        var t = c.TypeName.ToLowerInvariant();
        var q = Sql.Q(t);
        return t switch
        {
            "varchar" or "char" or "varbinary" or "binary" => $"{q}({(c.MaxLength == -1 ? "max" : c.MaxLength.ToString(CultureInfo.InvariantCulture))})",
            "nvarchar" or "nchar" => $"{q}({(c.MaxLength == -1 ? "max" : (c.MaxLength / 2).ToString(CultureInfo.InvariantCulture))})",
            "decimal" or "numeric" => $"{q}({c.Precision}, {c.Scale})",
            "datetime2" or "time" or "datetimeoffset" => $"{q}({c.Scale})",
            "float" => c.Precision == 53 ? q : $"{q}({c.Precision})",
            _ => q,
        };
    }

    private static string RenderColumn(ColumnMeta c, string? databaseCollation, bool allowNamedDefault)
    {
        if (c.IsComputed)
        {
            return $"{Sql.Q(c.Name)} AS {c.ComputedDefinition}{(c.IsPersisted ? " PERSISTED" : "")}{(c.IsPersisted && !c.IsNullable ? " NOT NULL" : "")}";
        }

        var sb = new StringBuilder().Append(Sql.Q(c.Name)).Append(' ').Append(FormatType(c));
        if (c.Collation is not null && !string.Equals(c.Collation, databaseCollation, StringComparison.OrdinalIgnoreCase))
        {
            sb.Append(" COLLATE ").Append(c.Collation);
        }

        if (c.IsSparse)
        {
            sb.Append(" SPARSE");
        }

        if (c.IsIdentity)
        {
            sb.Append($" IDENTITY({c.IdentitySeed},{c.IdentityIncrement})");
        }

        if (c.IsRowGuidCol)
        {
            sb.Append(" ROWGUIDCOL");
        }

        sb.Append(c.IsNullable ? " NULL" : " NOT NULL");
        if (c.DefaultDefinition is not null)
        {
            sb.Append(allowNamedDefault && c.DefaultName is not null ? $" CONSTRAINT {Sql.Q(c.DefaultName)}" : "").Append(" DEFAULT ").Append(c.DefaultDefinition);
        }

        return sb.ToString();
    }

    private static string KeyColumns(IEnumerable<IndexColumnMeta> cols) =>
        string.Join(", ", cols.Where(c => !c.IsIncluded).Select(c => $"{Sql.Q(c.Name)} {(c.IsDescending ? "DESC" : "ASC")}"));

    private static string KeyClause(IndexMeta ix) =>
        $"{(ix.IsPrimaryKey ? "PRIMARY KEY" : "UNIQUE")} {(ix.Type == 1 ? "CLUSTERED" : "NONCLUSTERED")} ({KeyColumns(ix.Columns)})";

    private static string? ConstraintWarning(IndexMeta ix)
    {
        if (ix.Type is not (1 or 2))
        {
            return $"constraint {Sql.Q(ix.Name)} is backed by index type {ix.Type}; scripted as NONCLUSTERED";
        }

        return ix.IsDisabled ? $"constraint {Sql.Q(ix.Name)} is disabled on the source" : null;
    }

    private static bool IsUntrustedEnabled(CheckMeta c) => c.IsNotTrusted && !c.IsDisabled;

    private static bool IsScriptedPlainIndex(IndexMeta i) => !i.IsPrimaryKey && !i.IsUniqueConstraint && i.Type is 1 or 2;

    /// <summary>
    /// Renders CREATE TABLE. With <paramref name="includeDependents"/> false every CHECK is inlined (trust state is lost);
    /// with true, enabled-but-untrusted CHECKs are added after the table WITH NOCHECK so enforcement is preserved.
    /// </summary>
    public static string RenderTable(TableMeta t, bool includeDependents)
    {
        var table = Sql.Qualified(t.Schema, t.Name);
        var sb = new StringBuilder("SET ANSI_NULLS ON\r\nGO\r\nSET QUOTED_IDENTIFIER ON\r\nGO\r\n");
        foreach (var w in t.Warnings)
        {
            sb.Append("-- WARNING: ").Append(w).Append("\r\n");
        }

        foreach (var i in t.Indexes.Where(i => i.IsPrimaryKey || i.IsUniqueConstraint))
        {
            var cw = ConstraintWarning(i);
            if (cw is not null)
            {
                sb.Append("-- WARNING: ").Append(cw).Append("\r\n");
            }
        }

        var inlineChecks = t.Checks.Where(c => !includeDependents || !IsUntrustedEnabled(c)).ToList();
        var lines = t.Columns.Select(c => "\t" + RenderColumn(c, t.DatabaseCollation, allowNamedDefault: true)).ToList();
        lines.AddRange(t.Indexes.Where(i => i.IsPrimaryKey || i.IsUniqueConstraint).Select(i => $"\tCONSTRAINT {Sql.Q(i.Name)} {KeyClause(i)}"));
        lines.AddRange(inlineChecks.Select(c => $"\tCONSTRAINT {Sql.Q(c.Name)} CHECK {c.Definition}"));
        sb.Append("CREATE TABLE ").Append(table).Append("(\r\n").Append(string.Join(",\r\n", lines)).Append("\r\n);");

        if (!includeDependents)
        {
            return sb.ToString();
        }

        // Indexes first: a self-referencing FK may target a unique index.
        var plain = t.Indexes.Where(i => !i.IsPrimaryKey && !i.IsUniqueConstraint).ToList();
        foreach (var ix in plain)
        {
            sb.Append(Go).Append(RenderIndexCore(t.Schema, t.Name, ix, out _));
        }

        foreach (var fk in t.ForeignKeys)
        {
            sb.Append(Go).Append(RenderForeignKey(fk));
        }

        foreach (var c in t.Checks.Where(IsUntrustedEnabled))
        {
            sb.Append(Go).Append($"ALTER TABLE {table} WITH NOCHECK ADD CONSTRAINT {Sql.Q(c.Name)} CHECK {c.Definition};");
        }

        foreach (var c in t.Checks.Where(c => c.IsDisabled))
        {
            sb.Append(Go).Append($"ALTER TABLE {table} NOCHECK CONSTRAINT {Sql.Q(c.Name)};");
        }

        if (t.Description is not null)
        {
            sb.Append(Go).Append($"EXEC sys.sp_addextendedproperty @name = N'MS_Description', @value = {Sql.N(t.Description)}, @level0type = N'SCHEMA', @level0name = {Sql.N(t.Schema)}, @level1type = N'TABLE', @level1name = {Sql.N(t.Name)};");
        }

        // Disabled indexes last so nothing above depends on an unusable index.
        foreach (var ix in plain.Where(i => i.IsDisabled && IsScriptedPlainIndex(i)))
        {
            sb.Append(Go).Append($"ALTER INDEX {Sql.Q(ix.Name)} ON {table} DISABLE;");
        }

        return sb.ToString();
    }

    public static string RenderIndex(string schema, string table, IndexMeta ix, out string? warning)
    {
        var ddl = RenderIndexCore(schema, table, ix, out warning);
        if (ix.IsDisabled && IsScriptedPlainIndex(ix))
        {
            ddl += Go + $"ALTER INDEX {Sql.Q(ix.Name)} ON {Sql.Qualified(schema, table)} DISABLE;";
        }

        return ddl;
    }

    private static string RenderIndexCore(string schema, string table, IndexMeta ix, out string? warning)
    {
        warning = null;
        var target = Sql.Qualified(schema, table);
        if (ix.IsPrimaryKey || ix.IsUniqueConstraint)
        {
            warning = ConstraintWarning(ix);
            var stmt = $"ALTER TABLE {target} ADD CONSTRAINT {Sql.Q(ix.Name)} {KeyClause(ix)};";
            return warning is null ? stmt : $"-- WARNING: {warning}\r\n{stmt}";
        }

        if (ix.Type is not (1 or 2))
        {
            var kind = ix.Type switch { 3 => "XML", 4 => "spatial", 5 or 6 => "columnstore", 7 => "hash", _ => $"type {ix.Type}" };
            warning = $"Index {ix.Name} is a {kind} index and is not scripted.";
            return $"-- WARNING: {warning}";
        }

        var sb = new StringBuilder("CREATE ")
            .Append(ix.IsUnique ? "UNIQUE " : "")
            .Append(ix.Type == 1 ? "CLUSTERED" : "NONCLUSTERED")
            .Append($" INDEX {Sql.Q(ix.Name)} ON {target} ({KeyColumns(ix.Columns)})");
        var included = ix.Columns.Where(c => c.IsIncluded).Select(c => Sql.Q(c.Name)).ToList();
        if (included.Count > 0)
        {
            sb.Append(" INCLUDE (").Append(string.Join(", ", included)).Append(')');
        }

        if (ix.FilterDefinition is not null)
        {
            sb.Append(" WHERE ").Append(ix.FilterDefinition);
        }

        sb.Append(';');
        return sb.ToString();
    }

    public static string RenderForeignKey(ForeignKeyMeta fk)
    {
        var table = Sql.Qualified(fk.Schema, fk.Table);
        var sb = new StringBuilder($"ALTER TABLE {table} WITH {(fk.IsNotTrusted ? "NOCHECK" : "CHECK")} ADD CONSTRAINT {Sql.Q(fk.Name)} FOREIGN KEY (")
            .Append(string.Join(", ", fk.Columns.Select(Sql.Q)))
            .Append($") REFERENCES {Sql.Qualified(fk.RefSchema, fk.RefTable)} (")
            .Append(string.Join(", ", fk.RefColumns.Select(Sql.Q)))
            .Append(')');
        if (fk.DeleteAction != "NO_ACTION")
        {
            sb.Append(" ON DELETE ").Append(fk.DeleteAction.Replace('_', ' '));
        }

        if (fk.UpdateAction != "NO_ACTION")
        {
            sb.Append(" ON UPDATE ").Append(fk.UpdateAction.Replace('_', ' '));
        }

        if (fk.NotForReplication)
        {
            sb.Append(" NOT FOR REPLICATION");
        }

        sb.Append(';');
        if (fk.IsDisabled)
        {
            sb.Append(Go).Append($"ALTER TABLE {table} NOCHECK CONSTRAINT {Sql.Q(fk.Name)};");
        }

        return sb.ToString();
    }

    public static string RenderAliasType(string schema, string name, ColumnMeta baseType) =>
        $"CREATE TYPE {Sql.Qualified(schema, name)} FROM {FormatType(baseType)}{(baseType.IsNullable ? " NULL" : " NOT NULL")};";

    public static string RenderTableType(string schema, string name, TableMeta shape)
    {
        // Table types allow only unnamed constraints.
        var lines = shape.Columns.Select(c => "\t" + RenderColumn(c, shape.DatabaseCollation, allowNamedDefault: false)).ToList();
        lines.AddRange(shape.Indexes.Where(i => i.IsPrimaryKey || i.IsUniqueConstraint).Select(i => "\t" + KeyClause(i)));
        lines.AddRange(shape.Checks.Select(c => $"\tCHECK {c.Definition}"));
        return $"CREATE TYPE {Sql.Qualified(schema, name)} AS TABLE(\r\n{string.Join(",\r\n", lines)}\r\n);";
    }
}
