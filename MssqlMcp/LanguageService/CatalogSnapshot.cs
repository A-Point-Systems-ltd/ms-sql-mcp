using System.Globalization;
using Microsoft.Data.SqlClient;

namespace Mssql.McpServer.LanguageService;

/// <summary>
/// A column of a table, view or table-valued function; <paramref name="IsKey"/> marks primary-key columns,
/// <paramref name="KeyOrdinal"/> its 1-based position in the key (0 when not a key column), <paramref name="IsIdentity"/>
/// an IDENTITY column.
/// </summary>
public sealed record CatalogColumn(string Name, string Type, bool Nullable, bool IsKey, bool IsIdentity = false, int KeyOrdinal = 0);

/// <summary>A table (U), view (V) or table-valued function (IF / TF) and its columns in column order.</summary>
public sealed record CatalogObject(string Schema, string Name, string Kind, IReadOnlyList<CatalogColumn> Columns)
{
    public bool IsTable => Kind == "U";

    /// <summary>The single primary-key column, or null for no key or a composite key.</summary>
    public CatalogColumn? SingleKey => Columns.Count(c => c.IsKey) == 1 ? Columns.First(c => c.IsKey) : null;

    public CatalogColumn? Column(string name) => Columns.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>True when the column is the last column of a composite primary key (the row's own number within its parent).</summary>
    public bool IsLastKeyPart(CatalogColumn column)
    {
        var keyColumns = Columns.Count(c => c.IsKey);
        return column.IsKey && keyColumns > 1 && (column.KeyOrdinal == keyColumns
            || (column.KeyOrdinal == 0 && ReferenceEquals(Columns.Last(c => c.IsKey), column)));
    }
}

/// <summary>A foreign key: <paramref name="Columns"/> pairs (referencing column, referenced column) in key order.</summary>
public sealed record CatalogForeignKey(string Name, CatalogObject Parent, CatalogObject Referenced, IReadOnlyList<(string Parent, string Referenced)> Columns);

/// <summary>
/// One database's tables, views, table-valued functions, their columns and foreign keys, read with two catalog queries
/// and kept per connection for the enhanced completions (JOIN / ON suggestions, column picker, * expansion). Read-only.
/// </summary>
public sealed class CatalogSnapshot
{
    private readonly Dictionary<string, CatalogObject> _byQualifiedName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<CatalogObject>> _byName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<CatalogObject, List<CatalogForeignKey>> _foreignKeys = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<string, List<CatalogObject>> _tablesBySingleKey = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<CatalogObject>> _tablesByNonKeyColumn = new(StringComparer.OrdinalIgnoreCase);

    public CatalogSnapshot(IEnumerable<CatalogObject> objects, IEnumerable<(string Name, string ParentSchema, string Parent, string ReferencedSchema, string Referenced, string ParentColumn, string ReferencedColumn)> foreignKeyColumns, string defaultSchema = "dbo")
    {
        DefaultSchema = defaultSchema;
        foreach (var o in objects)
        {
            _byQualifiedName[$"{o.Schema}.{o.Name}"] = o;
            if (!_byName.TryGetValue(o.Name, out var list))
            {
                _byName[o.Name] = list = [];
            }

            list.Add(o);
            if (!o.IsTable)
            {
                continue;
            }

            if (o.SingleKey is { } key)
            {
                Index(_tablesBySingleKey, key.Name, o);
            }

            // Every column but the single-column key: parts of a composite key (OrderLines.OrderId) join by name too.
            var single = o.SingleKey;
            foreach (var column in o.Columns.Where(c => !ReferenceEquals(c, single)))
            {
                Index(_tablesByNonKeyColumn, column.Name, o);
            }
        }

        var keys = new List<CatalogForeignKey>();
        foreach (var group in foreignKeyColumns.GroupBy(f => (f.Name, f.ParentSchema, f.Parent)))
        {
            var first = group.First();
            var parent = Find(first.ParentSchema, first.Parent);
            var referenced = Find(first.ReferencedSchema, first.Referenced);
            if (parent is null || referenced is null)
            {
                continue;
            }

            keys.Add(new CatalogForeignKey(first.Name, parent, referenced, [.. group.Select(g => (g.ParentColumn, g.ReferencedColumn))]));
        }

        foreach (var key in keys)
        {
            Add(key.Parent, key);
            if (!ReferenceEquals(key.Parent, key.Referenced))
            {
                Add(key.Referenced, key);
            }
        }

        ObjectCount = _byQualifiedName.Count;
        ForeignKeyCount = keys.Count;

        static void Index(Dictionary<string, List<CatalogObject>> index, string column, CatalogObject o)
        {
            if (!index.TryGetValue(column, out var list))
            {
                index[column] = list = [];
            }

            list.Add(o);
        }

        void Add(CatalogObject o, CatalogForeignKey key)
        {
            if (!_foreignKeys.TryGetValue(o, out var list))
            {
                _foreignKeys[o] = list = [];
            }

            list.Add(key);
        }
    }

    public string DefaultSchema { get; }

    public int ObjectCount { get; }

    public int ForeignKeyCount { get; }

    public IEnumerable<CatalogObject> Objects => _byQualifiedName.Values;

    /// <summary>The object by schema and name; without a schema, the default schema's object, else the only one by that name.</summary>
    public CatalogObject? Find(string? schema, string name)
    {
        if (!string.IsNullOrEmpty(schema))
        {
            return _byQualifiedName.GetValueOrDefault($"{schema}.{name}");
        }

        if (_byQualifiedName.TryGetValue($"{DefaultSchema}.{name}", out var o))
        {
            return o;
        }

        return _byName.TryGetValue(name, out var list) && list.Count == 1 ? list[0] : null;
    }

    /// <summary>Tables whose single-column primary key is named <paramref name="column"/>.</summary>
    public IReadOnlyList<CatalogObject> TablesKeyedBy(string column) => _tablesBySingleKey.TryGetValue(column, out var list) ? list : [];

    /// <summary>Tables with a column named <paramref name="column"/> that is not their single-column primary key.</summary>
    public IReadOnlyList<CatalogObject> TablesWithNonKeyColumn(string column) => _tablesByNonKeyColumn.TryGetValue(column, out var list) ? list : [];

    /// <summary>Foreign keys where the object is the referencing or the referenced table.</summary>
    public IReadOnlyList<CatalogForeignKey> ForeignKeysOf(CatalogObject o) => _foreignKeys.TryGetValue(o, out var list) ? list : [];

    private const string ObjectsQuery = """
        SELECT s.name, o.name, RTRIM(o.type), c.name, t.name, c.max_length, c.precision, c.scale, c.is_nullable,
               CAST(CASE WHEN pk.column_id IS NULL THEN 0 ELSE 1 END AS bit), c.is_identity, ISNULL(pk.key_ordinal, 0)
        FROM sys.objects o
            INNER JOIN sys.schemas s ON s.schema_id = o.schema_id
            INNER JOIN sys.columns c ON c.object_id = o.object_id
            INNER JOIN sys.types t ON t.user_type_id = c.user_type_id
            LEFT JOIN (SELECT ic.object_id, ic.column_id, CAST(ic.key_ordinal AS int) AS key_ordinal
                       FROM sys.indexes i
                           INNER JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
                       WHERE i.is_primary_key = 1) pk ON pk.object_id = c.object_id AND pk.column_id = c.column_id
        WHERE o.type IN ('U', 'V', 'IF', 'TF') AND o.is_ms_shipped = 0
        ORDER BY o.object_id, c.column_id;
        """;

    private const string ForeignKeysQuery = """
        SELECT fk.name, ps.name, po.name, rs.name, ro.name, pc.name, rc.name
        FROM sys.foreign_keys fk
            INNER JOIN sys.objects po ON po.object_id = fk.parent_object_id
            INNER JOIN sys.schemas ps ON ps.schema_id = po.schema_id
            INNER JOIN sys.objects ro ON ro.object_id = fk.referenced_object_id
            INNER JOIN sys.schemas rs ON rs.schema_id = ro.schema_id
            INNER JOIN sys.foreign_key_columns fkc ON fkc.constraint_object_id = fk.object_id
            INNER JOIN sys.columns pc ON pc.object_id = fkc.parent_object_id AND pc.column_id = fkc.parent_column_id
            INNER JOIN sys.columns rc ON rc.object_id = fkc.referenced_object_id AND rc.column_id = fkc.referenced_column_id
        WHERE fk.is_disabled = 0
        ORDER BY fk.object_id, fkc.constraint_column_id;
        """;

    /// <summary>Reads the snapshot over an open connection (catalog views only).</summary>
    public static async Task<CatalogSnapshot> LoadAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        var objects = new List<CatalogObject>();
        await using (var cmd = new SqlCommand(ObjectsQuery, connection) { CommandTimeout = 60 })
        await using (var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            string? schema = null, name = null, kind = null;
            var columns = new List<CatalogColumn>();
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var s = reader.GetString(0);
                var n = reader.GetString(1);
                if (schema != s || name != n)
                {
                    Flush();
                    (schema, name, kind) = (s, n, reader.GetString(2));
                    columns = [];
                }

                columns.Add(new CatalogColumn(
                    reader.GetString(3),
                    TypeText(reader.GetString(4), reader.GetInt16(5), reader.GetByte(6), reader.GetByte(7)),
                    reader.GetBoolean(8),
                    reader.GetBoolean(9),
                    reader.GetBoolean(10),
                    reader.GetInt32(11)));
            }

            Flush();

            void Flush()
            {
                if (schema is not null && name is not null && kind is not null)
                {
                    objects.Add(new CatalogObject(schema, name, kind, columns));
                }
            }
        }

        var keys = new List<(string, string, string, string, string, string, string)>();
        await using (var cmd = new SqlCommand(ForeignKeysQuery, connection) { CommandTimeout = 60 })
        await using (var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                keys.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6)));
            }
        }

        string defaultSchema;
        await using (var cmd = new SqlCommand("SELECT SCHEMA_NAME();", connection))
        {
            defaultSchema = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string ?? "dbo";
        }

        return new CatalogSnapshot(objects, keys, defaultSchema);
    }

    /// <summary>A column type as written in T-SQL: varchar(20), nvarchar(max), decimal(18,2), datetime2(7).</summary>
    internal static string TypeText(string type, short maxLength, byte precision, byte scale)
    {
        var t = type.ToLowerInvariant();
        return t switch
        {
            "varchar" or "char" or "varbinary" or "binary" => $"{t}({(maxLength == -1 ? "max" : maxLength.ToString(CultureInfo.InvariantCulture))})",
            "nvarchar" or "nchar" => $"{t}({(maxLength == -1 ? "max" : (maxLength / 2).ToString(CultureInfo.InvariantCulture))})",
            "decimal" or "numeric" => $"{t}({precision},{scale})",
            "datetime2" or "time" or "datetimeoffset" => $"{t}({scale})",
            _ => t,
        };
    }
}
