using System.Text;

namespace Mssql.McpServer.LanguageService;

/// <summary>Which same-named columns JOIN / ON suggestions use when no foreign key connects two tables.</summary>
public enum JoinNameMatch
{
    /// <summary>Foreign keys only.</summary>
    Off,

    /// <summary>Also a single-column primary key and its same-named reference (see <see cref="EnhancedCompletion.NameMatchAllowed"/>).</summary>
    Keyed,

    /// <summary>Also any other same-named columns of the same type family (see <see cref="EnhancedCompletion.LooseNameMatchAllowed"/>), ranked last.</summary>
    Any,
}

/// <summary>The editor's settings for the enhanced completions; the defaults are the behaviour before they existed.</summary>
public sealed record EnhancedOptions(bool TableAliases = true, JoinNameMatch NameMatch = JoinNameMatch.Keyed)
{
    public static readonly EnhancedOptions Default = new();

    /// <summary>off | keyed | any (any case); anything else is <see cref="JoinNameMatch.Keyed"/>.</summary>
    public static JoinNameMatch ParseNameMatch(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "off" => JoinNameMatch.Off,
        "any" => JoinNameMatch.Any,
        _ => JoinNameMatch.Keyed,
    };
}

/// <summary>
/// The enhanced completions (the editor's "Enhanced Completions" switch): whole JOIN clauses and ON conditions from
/// foreign keys or matching column names, generated table aliases, and the column-picker entry. Pure: works on a
/// <see cref="SqlScopeInfo"/> and a <see cref="CatalogSnapshot"/>.
/// </summary>
public static class EnhancedCompletion
{
    /// <summary>At most this many JOIN or ON suggestions.</summary>
    internal const int MaxSuggestions = 40;

    public static List<CompletionItemInfo> Create(SqlScopeInfo scope, CatalogSnapshot? catalog, EnhancedOptions? options = null)
    {
        options ??= EnhancedOptions.Default;
        var items = new List<CompletionItemInfo>();
        switch (scope.Context)
        {
            case CaretContext.JoinTable when catalog is not null:
                items.AddRange(JoinItems(scope, catalog, options));
                break;
            case CaretContext.OnCondition when catalog is not null && scope.JoinedTable is { } joined:
                items.AddRange(OnItems(scope, joined, catalog, options));
                break;
            case CaretContext.SelectList when scope.Tables.Count > 0:
                items.Add(new CompletionItemInfo("Pick columns…", CompletionKinds.Picker, "Choose columns of the statement's tables", string.Empty, "0000"));
                break;
        }

        return items;
    }

    /// <summary>Appends a generated alias to table, view and function names offered after FROM or JOIN (none when one is already written).</summary>
    public static List<CompletionItemInfo> WithAliases(IEnumerable<CompletionItemInfo> items, SqlScopeInfo scope, EnhancedOptions? options = null)
    {
        if (!(options ?? EnhancedOptions.Default).TableAliases || scope.Context is not (CaretContext.FromTable or CaretContext.JoinTable) || scope.AliasFollows)
        {
            return [.. items];
        }

        var taken = Taken(scope);
        return [.. items.Select(i => i.Kind is CompletionKinds.Table or CompletionKinds.View
            ? i with { InsertText = $"{i.InsertText} {AliasGenerator.Create(Unquote(i.Label), taken)}" }
            : i)];
    }

    private static HashSet<string> Taken(SqlScopeInfo scope) =>
        new(scope.Tables.Select(t => t.Qualifier), StringComparer.OrdinalIgnoreCase);

    private static IEnumerable<CompletionItemInfo> JoinItems(SqlScopeInfo scope, CatalogSnapshot catalog, EnhancedOptions options)
    {
        var taken = Taken(scope);
        var inScope = scope.Tables.Where(t => !t.IsVariable).Select(t => (Table: t, Object: catalog.Find(t.Schema, t.Name)))
            .Where(x => x.Object is not null).Select(x => (x.Table, Object: x.Object!)).ToList();
        var scopeObjects = new HashSet<CatalogObject>(inScope.Select(x => x.Object), ReferenceEqualityComparer.Instance);
        var results = new List<(int Rank, string Label, CompletionItemInfo Item)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (table, obj) in inScope)
        {
            foreach (var key in catalog.ForeignKeysOf(obj))
            {
                var other = ReferenceEquals(key.Parent, obj) ? key.Referenced : key.Parent;
                if (scopeObjects.Contains(other))
                {
                    continue;
                }

                var alias = NewAlias(other);
                var pairs = ReferenceEquals(key.Parent, obj)
                    ? key.Columns.Select(c => (New: c.Referenced, Existing: c.Parent))
                    : key.Columns.Select(c => (New: c.Parent, Existing: c.Referenced));
                Add(0, other, alias, table, pairs, $"FK {key.Name}");
            }
        }

        // Name matches anchored on a single-column key (see NameMatchAllowed). Indexed lookups, so the catalog's size
        // does not matter.
        foreach (var (table, obj) in options.NameMatch == JoinNameMatch.Off ? [] : inScope)
        {
            var objKey = obj.SingleKey;
            foreach (var column in obj.Columns.Where(c => !ReferenceEquals(c, objKey)))
            {
                foreach (var other in catalog.TablesKeyedBy(column.Name).Where(o => !scopeObjects.Contains(o)))
                {
                    if (NameMatchAllowed(catalog, other, other.SingleKey!, obj, column))
                    {
                        Add(SchemaRank(other, obj), other, NewAlias(other), table, [(other.SingleKey!.Name, column.Name)], "same column name");
                    }
                }
            }

            if (objKey is not null)
            {
                foreach (var other in catalog.TablesWithNonKeyColumn(objKey.Name).Where(o => !scopeObjects.Contains(o)))
                {
                    var match = other.Column(objKey.Name)!;
                    if (NameMatchAllowed(catalog, obj, objKey, other, match))
                    {
                        Add(SchemaRank(other, obj), other, NewAlias(other), table, [(match.Name, objKey.Name)], "same column name");
                    }
                }
            }
        }

        // Any other same-named columns (tables without a key, non-key = non-key), after every keyed match. A pair the
        // keyed pass already offered has the same text, so it is not added twice.
        foreach (var (table, obj) in options.NameMatch == JoinNameMatch.Any ? inScope : [])
        {
            foreach (var column in obj.Columns)
            {
                foreach (var other in catalog.TablesWithColumn(column.Name).Where(o => !scopeObjects.Contains(o)))
                {
                    var match = other.Column(column.Name)!;
                    if (LooseNameMatchAllowed(other, match, obj, column))
                    {
                        Add(SchemaRank(other, obj) + 2, other, NewAlias(other), table, [(match.Name, column.Name)], "same column name (no key)");
                    }
                }
            }
        }

        return results.OrderBy(r => r.Rank).ThenBy(r => r.Label, StringComparer.OrdinalIgnoreCase)
            .Take(MaxSuggestions).Select((r, n) => r.Item with { SortText = $"0{n:D3}" });

        // Foreign keys rank 0, keyed name matches 1-2, loose ones 3-4; name matches in the same schema as the table in
        // scope come before other schemas (one schema per tenant or an archive schema must not push the right partner
        // out of the list).
        static int SchemaRank(CatalogObject other, CatalogObject inScope) =>
            string.Equals(other.Schema, inScope.Schema, StringComparison.OrdinalIgnoreCase) ? 1 : 2;

        // Without table aliases the new table is qualified by its (schema-qualified when needed) name.
        string? NewAlias(CatalogObject other) => options.TableAliases ? AliasGenerator.Create(other.Name, taken) : null;

        void Add(int rank, CatalogObject other, string? alias, ScopeTable existing, IEnumerable<(string New, string Existing)> pairs, string detail)
        {
            var name = ObjectName(other, catalog);
            var qualifier = alias is null ? name : QuoteName(alias);
            var condition = string.Join(" and ", pairs.Select(p => $"{qualifier}.{QuoteName(p.New)} = {QuoteName(existing.Qualifier)}.{QuoteName(p.Existing)}"));
            var text = alias is null ? $"{name} on {condition}" : $"{name} {qualifier} on {condition}";
            if (seen.Add(text))
            {
                results.Add((rank, text, new CompletionItemInfo(text, CompletionKinds.Join, detail, text, "0")));
            }
        }
    }

    private static IEnumerable<CompletionItemInfo> OnItems(SqlScopeInfo scope, ScopeTable joined, CatalogSnapshot catalog, EnhancedOptions options)
    {
        var joinedObject = joined.IsVariable ? null : catalog.Find(joined.Schema, joined.Name);
        if (joinedObject is null)
        {
            return [];
        }

        var results = new List<(int Rank, CompletionItemInfo Item)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var other in scope.Tables.Where(t => t != joined && t.Position < joined.Position && !t.IsVariable))
        {
            var otherObject = catalog.Find(other.Schema, other.Name);
            if (otherObject is null)
            {
                continue;
            }

            foreach (var key in catalog.ForeignKeysOf(joinedObject))
            {
                IEnumerable<(string Joined, string Other)>? pairs =
                    ReferenceEquals(key.Parent, joinedObject) && ReferenceEquals(key.Referenced, otherObject) ? key.Columns.Select(c => (c.Parent, c.Referenced))
                    : ReferenceEquals(key.Referenced, joinedObject) && ReferenceEquals(key.Parent, otherObject) ? key.Columns.Select(c => (c.Referenced, c.Parent))
                    : null;
                if (pairs is not null)
                {
                    Add(0, pairs, other, $"FK {key.Name}");
                }
            }

            foreach (var column in options.NameMatch == JoinNameMatch.Off ? [] : joinedObject.Columns)
            {
                if (otherObject.Column(column.Name) is not { } match)
                {
                    continue;
                }

                if (NameMatchAllowed(catalog, joinedObject, column, otherObject, match))
                {
                    Add(1, [(column.Name, match.Name)], other, "same column name");
                }
                else if (options.NameMatch == JoinNameMatch.Any && LooseNameMatchAllowed(joinedObject, column, otherObject, match))
                {
                    Add(2, [(column.Name, match.Name)], other, "same column name (no key)");
                }
            }
        }

        return results.OrderBy(r => r.Rank).Take(MaxSuggestions).Select((r, n) => r.Item with { SortText = $"0{n:D3}" });

        void Add(int rank, IEnumerable<(string Joined, string Other)> pairs, ScopeTable other, string detail)
        {
            var text = string.Join(" and ", pairs.Select(p => $"{QuoteName(joined.Qualifier)}.{QuoteName(p.Joined)} = {QuoteName(other.Qualifier)}.{QuoteName(p.Other)}"));
            if (seen.Add(text))
            {
                results.Add((rank, new CompletionItemInfo(text, CompletionKinds.Join, detail, text, "0")));
            }
        }
    }

    /// <summary>Column names that say nothing about a relationship: they join only through a foreign key.</summary>
    private static readonly HashSet<string> GenericNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "id", "rowid", "row_id", "guid", "rowguid", "lineno", "line_no", "line", "seq", "sequence", "ordinal",
        "code", "name", "description", "title", "key", "value", "no", "num", "number",
    };

    /// <summary>
    /// Whether two same-named columns (no foreign key between their tables) are suggested as a join. One side must be
    /// a table's single-column key (the parent); the other side must be the reference to it: a non-key column
    /// (Units.BID → Buildings.BID) or a leading part of a composite key (OrderLines.OrderId → Orders.OrderId). Never:
    /// generic names (Id, LineNo, Code, Name, ...), an IDENTITY column on the referencing side (a table's own surrogate number), the
    /// last part of a composite key (the row's number within its parent), key = key or non-key = non-key. Those pair
    /// unrelated rows; foreign keys still suggest any of them.
    /// </summary>
    internal static bool NameMatchAllowed(CatalogSnapshot catalog, CatalogObject a, CatalogColumn ca, CatalogObject b, CatalogColumn cb)
    {
        _ = catalog;
        if (GenericNames.Contains(ca.Name))
        {
            return false;
        }

        var aParent = ReferenceEquals(ca, a.SingleKey);
        var bParent = ReferenceEquals(cb, b.SingleKey);
        if (aParent == bParent)
        {
            return false;
        }

        var (child, reference) = aParent ? (b, cb) : (a, ca);
        return !reference.IsIdentity && !child.IsLastKeyPart(reference);
    }

    /// <summary>
    /// Whether two same-named columns are suggested as a join under <see cref="JoinNameMatch.Any"/> (tables without a
    /// primary key, non-key = non-key, key = key). Never: generic names, an IDENTITY column on either side (each table's
    /// own row number), the last part of a composite key, or columns of different type families (int Status vs
    /// nvarchar Status).
    /// </summary>
    internal static bool LooseNameMatchAllowed(CatalogObject a, CatalogColumn ca, CatalogObject b, CatalogColumn cb) =>
        !GenericNames.Contains(ca.Name) && !ca.IsIdentity && !cb.IsIdentity
        && !a.IsLastKeyPart(ca) && !b.IsLastKeyPart(cb)
        && TypeFamily(ca.Type) == TypeFamily(cb.Type);

    /// <summary>The comparable family of a column type: varchar(20) and nvarchar(50) match, int and bigint match.</summary>
    internal static string TypeFamily(string type)
    {
        var paren = type.IndexOf('(', StringComparison.Ordinal);
        var name = (paren < 0 ? type : type[..paren]).Trim().ToLowerInvariant();
        return name switch
        {
            "char" or "varchar" or "nchar" or "nvarchar" or "text" or "ntext" or "sysname" => "string",
            "tinyint" or "smallint" or "int" or "bigint" => "integer",
            "decimal" or "numeric" or "money" or "smallmoney" => "exact",
            "date" or "datetime" or "datetime2" or "smalldatetime" or "datetimeoffset" => "date",
            _ => name,
        };
    }

    /// <summary>A schema-qualified name, without the schema when it is the default one.</summary>
    internal static string ObjectName(CatalogObject o, CatalogSnapshot catalog) =>
        string.Equals(o.Schema, catalog.DefaultSchema, StringComparison.OrdinalIgnoreCase) ? QuoteName(o.Name) : $"{QuoteName(o.Schema)}.{QuoteName(o.Name)}";

    /// <summary>The name as is when it is a valid regular identifier and not a reserved word, else in brackets.</summary>
    internal static string QuoteName(string name)
    {
        if (name.Length > 0 && (char.IsLetter(name[0]) || name[0] is '_' or '@' or '#')
            && name.All(c => char.IsLetterOrDigit(c) || c is '_' or '@' or '#' or '$')
            && !SqlKeywords.IsReservedWord(name))
        {
            return name;
        }

        return "[" + name.Replace("]", "]]", StringComparison.Ordinal) + "]";
    }

    private static string Unquote(string label) =>
        label.Length >= 2 && label[0] == '[' && label[^1] == ']' ? label[1..^1].Replace("]]", "]", StringComparison.Ordinal) : label;
}

/// <summary>
/// Table aliases for new table references: the lower-case initials of the name's words (TableProblemsLastSub → tpls,
/// CC_Meshulam_Buildings → cmb), with a number added when the alias is taken or is a reserved word.
/// </summary>
public static class AliasGenerator
{
    public static string Create(string tableName, ISet<string> taken)
    {
        var initials = Initials(tableName);
        if (initials.Length == 0)
        {
            initials = "t";
        }

        var alias = initials;
        for (var n = 2; taken.Contains(alias) || SqlKeywords.IsReservedWord(alias); n++)
        {
            alias = initials + n.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return alias;
    }

    internal static string Initials(string name)
    {
        var sb = new StringBuilder();
        var start = true;
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (!char.IsLetterOrDigit(c))
            {
                start = true;
                continue;
            }

            // A word starts after a separator, at an upper-case letter after a lower-case one, or at the last upper-case
            // letter of an acronym followed by a lower-case one (XMLParser: x, p).
            var upperAfterLower = char.IsUpper(c) && i > 0 && char.IsLower(name[i - 1]);
            var acronymEnd = char.IsUpper(c) && i > 0 && char.IsUpper(name[i - 1]) && i + 1 < name.Length && char.IsLower(name[i + 1]);
            if ((start || upperAfterLower || acronymEnd) && char.IsLetter(c))
            {
                sb.Append(char.ToLowerInvariant(c));
            }

            start = false;
        }

        return sb.ToString();
    }
}
