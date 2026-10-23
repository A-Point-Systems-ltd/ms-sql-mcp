namespace Mssql.McpServer.LanguageService;

/// <summary>Completion items for objects outside the default schema, and whether the client should ask again.</summary>
public sealed record OtherSchemaItems(IReadOnlyList<CompletionItemInfo> Items, bool Incomplete)
{
    public static readonly OtherSchemaItems None = new([], false);
}

/// <summary>
/// SqlParser offers unqualified names only from the default schema (like SSMS), so typing <c>FROM Ord</c> never finds
/// <c>sales.Orders</c>. Where a table goes and an unqualified name is typed, this adds the catalog's tables, views and
/// table-valued functions of the other schemas whose name or schema starts with what was typed, inserted schema-qualified
/// (<c>sales.Orders</c>). Nothing is added before a character is typed; the list is marked incomplete instead, so the
/// client asks again with the first character.
/// </summary>
public static class OtherSchemaObjects
{
    /// <summary>Most items added per request; more matches mark the list incomplete (the next keystroke narrows it).</summary>
    internal const int MaxItems = 200;

    public static OtherSchemaItems Create(SqlScopeInfo scope, CatalogSnapshot? catalog, bool tableAliases)
    {
        var prefix = scope.ObjectPrefix;
        if (prefix is null)
        {
            return OtherSchemaItems.None;
        }

        if (catalog is null)
        {
            // Still loading: ask again on the next keystroke.
            return new([], true);
        }

        var others = catalog.Objects.Where(o => !string.Equals(o.Schema, catalog.DefaultSchema, StringComparison.OrdinalIgnoreCase));
        if (prefix.Length == 0)
        {
            return new([], others.Any());
        }

        var matches = others
            .Where(o => o.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || o.Schema.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .OrderBy(o => o.Schema, StringComparer.OrdinalIgnoreCase)
            .ThenBy(o => o.Name, StringComparer.OrdinalIgnoreCase)
            .Take(MaxItems + 1)
            .ToList();
        var aliases = tableAliases && scope.Context is (CaretContext.FromTable or CaretContext.JoinTable) && !scope.AliasFollows;
        var taken = new HashSet<string>(scope.Tables.Select(t => t.Qualifier), StringComparer.OrdinalIgnoreCase);
        var items = matches.Take(MaxItems).Select(o =>
        {
            var label = EnhancedCompletion.QuoteName(o.Schema) + "." + EnhancedCompletion.QuoteName(o.Name);
            // Like the parser's items (EnhancedCompletion.WithAliases): tables and views only, a function needs its arguments first.
            var insert = aliases && o.Kind is ("U" or "V") ? $"{label} {AliasGenerator.Create(o.Name, taken)}" : label;
            // Matched against the typed word: the bare name for FROM Ord, the qualified name for FROM sal.
            var filter = o.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? EnhancedCompletion.QuoteName(o.Name) : label;
            return new CompletionItemInfo(label, KindOf(o), $"{o.Schema}.{o.Name}", insert, CompletionConverter.SortText(2, label), filter);
        }).ToList();
        return new(items, matches.Count > MaxItems);
    }

    private static string KindOf(CatalogObject o) => o.Kind switch
    {
        "U" => CompletionKinds.Table,
        "V" => CompletionKinds.View,
        _ => CompletionKinds.Function,
    };
}
