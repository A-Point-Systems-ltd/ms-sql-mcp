using System.Text;

namespace Mssql.McpServer.LanguageService;

/// <summary>
/// The enhanced completions (the editor's "Enhanced Completions" switch): whole JOIN clauses and ON conditions from
/// foreign keys or matching column names, generated table aliases, and the column-picker entry. Pure: works on a
/// <see cref="SqlScopeInfo"/> and a <see cref="CatalogSnapshot"/>.
/// </summary>
public static class EnhancedCompletion
{
    /// <summary>At most this many JOIN or ON suggestions.</summary>
    internal const int MaxSuggestions = 40;

    public static List<CompletionItemInfo> Create(SqlScopeInfo scope, CatalogSnapshot? catalog)
    {
        var items = new List<CompletionItemInfo>();
        switch (scope.Context)
        {
            case CaretContext.JoinTable when catalog is not null:
                items.AddRange(JoinItems(scope, catalog));
                break;
            case CaretContext.OnCondition when catalog is not null && scope.JoinedTable is { } joined:
                items.AddRange(OnItems(scope, joined, catalog));
                break;
            case CaretContext.SelectList when scope.Tables.Count > 0:
                items.Add(new CompletionItemInfo("Pick columns…", CompletionKinds.Picker, "Choose columns of the statement's tables", string.Empty, "0000"));
                break;
        }

        return items;
    }

    /// <summary>Appends a generated alias to table, view and function names offered after FROM or JOIN (none when one is already written).</summary>
    public static List<CompletionItemInfo> WithAliases(IEnumerable<CompletionItemInfo> items, SqlScopeInfo scope)
    {
        if (scope.Context is not (CaretContext.FromTable or CaretContext.JoinTable) || scope.AliasFollows)
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

    private static IEnumerable<CompletionItemInfo> JoinItems(SqlScopeInfo scope, CatalogSnapshot catalog)
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

                var alias = AliasGenerator.Create(other.Name, taken);
                var pairs = ReferenceEquals(key.Parent, obj)
                    ? key.Columns.Select(c => (New: c.Referenced, Existing: c.Parent))
                    : key.Columns.Select(c => (New: c.Parent, Existing: c.Referenced));
                Add(0, other, alias, table, pairs, $"FK {key.Name}");
            }
        }

        // Name matches anchored on a single-column key (see NameMatchAllowed). Indexed lookups, so the catalog's size
        // does not matter.
        foreach (var (table, obj) in inScope)
        {
            var objKey = obj.SingleKey;
            foreach (var column in obj.Columns.Where(c => !ReferenceEquals(c, objKey)))
            {
                foreach (var other in catalog.TablesKeyedBy(column.Name).Where(o => !scopeObjects.Contains(o)))
                {
                    if (NameMatchAllowed(catalog, other, other.SingleKey!, obj, column))
                    {
                        Add(1, other, AliasGenerator.Create(other.Name, taken), table, [(other.SingleKey!.Name, column.Name)], "same column name");
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
                        Add(1, other, AliasGenerator.Create(other.Name, taken), table, [(match.Name, objKey.Name)], "same column name");
                    }
                }
            }
        }

        return results.OrderBy(r => r.Rank).ThenBy(r => r.Label, StringComparer.OrdinalIgnoreCase)
            .Take(MaxSuggestions).Select((r, n) => r.Item with { SortText = $"0{n:D3}" });

        void Add(int rank, CatalogObject other, string alias, ScopeTable existing, IEnumerable<(string New, string Existing)> pairs, string detail)
        {
            var condition = string.Join(" and ", pairs.Select(p => $"{QuoteName(alias)}.{QuoteName(p.New)} = {QuoteName(existing.Qualifier)}.{QuoteName(p.Existing)}"));
            var text = $"{ObjectName(other, catalog)} {QuoteName(alias)} on {condition}";
            if (seen.Add(text))
            {
                results.Add((rank, text, new CompletionItemInfo(text, CompletionKinds.Join, detail, text, "0")));
            }
        }
    }

    private static IEnumerable<CompletionItemInfo> OnItems(SqlScopeInfo scope, ScopeTable joined, CatalogSnapshot catalog)
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

            foreach (var column in joinedObject.Columns)
            {
                if (otherObject.Column(column.Name) is { } match && NameMatchAllowed(catalog, joinedObject, column, otherObject, match))
                {
                    Add(1, [(column.Name, match.Name)], other, "same column name");
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

    /// <summary>
    /// Whether two same-named columns (no foreign key between their tables) are suggested as a join. One side must be
    /// a table's single-column key; the other side may be a non-key column (Units.BID → Buildings.BID), or a part of a
    /// composite key only when no other table is keyed by that name (OrderLines.OrderId → Orders.OrderId). Refused:
    /// single key = single key, composite part = composite part, non-key = non-key, and a composite part matched to a
    /// key name many tables share (Id, LineNo) — those pair unrelated rows.
    /// </summary>
    internal static bool NameMatchAllowed(CatalogSnapshot catalog, CatalogObject a, CatalogColumn ca, CatalogObject b, CatalogColumn cb)
    {
        static int Kind(CatalogObject o, CatalogColumn c) => ReferenceEquals(c, o.SingleKey) ? 2 : c.IsKey ? 1 : 0;
        var (ka, kb) = (Kind(a, ca), Kind(b, cb));
        if (ka < kb)
        {
            (ka, kb) = (kb, ka);
        }

        return (ka, kb) switch
        {
            (2, 0) => true,
            (2, 1) => catalog.TablesKeyedBy(ca.Name).Count == 1,
            _ => false,
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
