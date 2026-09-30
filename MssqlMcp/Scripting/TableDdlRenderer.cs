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
        if (c.IsSparse)
        {
            sb.Append(" SPARSE");
        }

        if (c.Collation is not null && c.UserTypeSchema is null && !string.Equals(c.Collation, databaseCollation, StringComparison.OrdinalIgnoreCase))
        {
            sb.Append(" COLLATE ").Append(c.Collation);
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

    public static string RenderTable(TableMeta t, bool includeDependents)
    {
        var table = Sql.Qualified(t.Schema, t.Name);
        var sb = new StringBuilder();
        foreach (var w in t.Warnings)
        {
            sb.Append("-- WARNING: ").Append(w).Append("\r\n");
        }

        var lines = t.Columns.Select(c => "\t" + RenderColumn(c, t.DatabaseCollation, allowNamedDefault: true)).ToList();
        lines.AddRange(t.Indexes.Where(i => i.IsPrimaryKey || i.IsUniqueConstraint).Select(i => $"\tCONSTRAINT {Sql.Q(i.Name)} {KeyClause(i)}"));
        lines.AddRange(t.Checks.Select(c => $"\tCONSTRAINT {Sql.Q(c.Name)} CHECK {c.Definition}"));
        sb.Append("CREATE TABLE ").Append(table).Append("(\r\n").Append(string.Join(",\r\n", lines)).Append("\r\n);");

        if (!includeDependents)
        {
            return sb.ToString();
        }

        foreach (var fk in t.ForeignKeys)
        {
            sb.Append(Go).Append(RenderForeignKey(fk));
        }

        foreach (var ix in t.Indexes.Where(i => !i.IsPrimaryKey && !i.IsUniqueConstraint))
        {
            sb.Append(Go).Append(RenderIndex(t.Schema, t.Name, ix, out _));
        }

        foreach (var c in t.Checks.Where(c => c.IsDisabled || c.IsNotTrusted))
        {
            sb.Append(Go).Append($"ALTER TABLE {table} NOCHECK CONSTRAINT {Sql.Q(c.Name)};");
        }

        if (t.Description is not null)
        {
            sb.Append(Go).Append($"EXEC sys.sp_addextendedproperty @name = N'MS_Description', @value = {Sql.N(t.Description)}, @level0type = N'SCHEMA', @level0name = {Sql.N(t.Schema)}, @level1type = N'TABLE', @level1name = {Sql.N(t.Name)};");
        }

        return sb.ToString();
    }

    public static string RenderIndex(string schema, string table, IndexMeta ix, out string? warning)
    {
        warning = null;
        var target = Sql.Qualified(schema, table);
        if (ix.IsPrimaryKey || ix.IsUniqueConstraint)
        {
            return $"ALTER TABLE {target} ADD CONSTRAINT {Sql.Q(ix.Name)} {KeyClause(ix)};";
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
        if (ix.IsDisabled)
        {
            sb.Append(Go).Append($"ALTER INDEX {Sql.Q(ix.Name)} ON {target} DISABLE;");
        }

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
