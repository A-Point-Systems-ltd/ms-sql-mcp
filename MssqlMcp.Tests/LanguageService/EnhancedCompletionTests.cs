using Mssql.McpServer.LanguageService;

namespace MssqlMcp.Tests.LanguageService;

/// <summary>The enhanced completions without a database: scope reading, JOIN / ON suggestions, aliases, catalog helpers.</summary>
public sealed class EnhancedCompletionTests
{
    private static SqlScopeInfo Scope(string textWithCaret)
    {
        var caret = textWithCaret.IndexOf('|', StringComparison.Ordinal);
        var text = textWithCaret.Remove(caret, 1);
        var before = text[..caret];
        var line = before.Count(c => c == '\n') + 1;
        var column = caret - (before.LastIndexOf('\n') + 1) + 1;
        return SqlScope.Analyze(text, line, column);
    }

    private static CatalogColumn Col(string name, bool key = false) => new(name, "int", !key, key);

    /// <summary>Buildings(BID pk) ← Units(UID pk, BID fk) ← Contacts(ContactID pk, UnitID fk); Payments(PayID pk, ContactID) by name only.</summary>
    private static CatalogSnapshot Catalog()
    {
        var buildings = new CatalogObject("dbo", "Buildings", "U", [Col("BID", true), new CatalogColumn("fullAddress", "nvarchar(200)", true, false)]);
        var units = new CatalogObject("dbo", "Units", "U", [Col("UID", true), Col("BID"), new CatalogColumn("Name", "nvarchar(50)", true, false)]);
        var contacts = new CatalogObject("dbo", "Contacts", "U", [Col("ContactID", true), Col("UnitID")]);
        var payments = new CatalogObject("billing", "Payments", "U", [Col("PayID", true), Col("ContactID")]);
        return new CatalogSnapshot(
            [buildings, units, contacts, payments],
            [
                ("FK_Units_Buildings", "dbo", "Units", "dbo", "Buildings", "BID", "BID"),
                ("FK_Contacts_Units", "dbo", "Contacts", "dbo", "Units", "UnitID", "UID"),
            ]);
    }

    [Theory]
    [InlineData("SELECT * FROM dbo.Units u JOIN |", CaretContext.JoinTable)]
    [InlineData("SELECT * FROM dbo.Units u LEFT JOIN Bu|", CaretContext.JoinTable)]
    [InlineData("SELECT * FROM dbo.Units u CROSS APPLY |", CaretContext.JoinTable)]
    [InlineData("SELECT * FROM |", CaretContext.FromTable)]
    [InlineData("SELECT * FROM dbo.Bu|", CaretContext.FromTable)]
    [InlineData("SELECT * FROM dbo.Units u JOIN dbo.Buildings b ON |", CaretContext.OnCondition)]
    [InlineData("SELECT | FROM dbo.Units", CaretContext.SelectList)]
    [InlineData("SELECT a, | FROM dbo.Units", CaretContext.SelectList)]
    [InlineData("SELECT DISTINCT | FROM dbo.Units", CaretContext.SelectList)]
    [InlineData("SELECT a FROM dbo.Units WHERE |", CaretContext.Other)]
    [InlineData("-- SELECT * FROM dbo.Units u JOIN |\n", CaretContext.Other)]
    [InlineData("SELECT 'FROM |' ", CaretContext.Other)]
    public void The_caret_context_is_recognised(string text, CaretContext expected) =>
        Assert.Equal(expected, Scope(text).Context);

    [Fact]
    public void Table_sources_with_aliases_are_read_from_the_statement_around_the_caret()
    {
        var scope = Scope("SELECT 1\nSELECT | FROM dbo.Units AS u INNER JOIN [dbo].[Buildings] b ON b.BID = u.BID, @t tv CROSS APPLY dbo.fn(u.UID) f\nSELECT * FROM Other o");
        Assert.Equal(
            ["dbo.Units u", "dbo.Buildings b", "@t tv", "dbo.fn f"],
            scope.Tables.Select(t => $"{(t.Schema is null ? "" : t.Schema + ".")}{t.Name} {t.Alias}"));
        Assert.True(scope.Tables[2].IsVariable);
    }

    [Fact]
    public void A_subquery_has_its_own_scope()
    {
        var scope = Scope("SELECT * FROM dbo.Units u WHERE u.BID IN (SELECT | FROM dbo.Buildings)");
        Assert.Equal("Buildings", Assert.Single(scope.Tables).Name);
    }

    [Fact]
    public void The_on_context_knows_the_table_just_joined()
    {
        var scope = Scope("SELECT * FROM dbo.Units u JOIN dbo.Buildings b ON |");
        Assert.Equal("b", scope.JoinedTable?.Alias);
    }

    [Fact]
    public void Join_suggestions_follow_foreign_keys_in_both_directions_with_generated_aliases()
    {
        var items = EnhancedCompletion.Create(Scope("SELECT * FROM dbo.Units u JOIN |"), Catalog());
        var labels = items.Select(i => i.Label).ToList();
        Assert.Contains("Buildings b on b.BID = u.BID", labels);
        Assert.Contains("Contacts c on c.UnitID = u.UID", labels);
        Assert.All(items, i => Assert.Equal(CompletionKinds.Join, i.Kind));
        // Foreign keys come before name matches.
        Assert.StartsWith("FK ", items[0].Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Join_suggestions_fall_back_to_matching_key_column_names_and_qualify_other_schemas()
    {
        var items = EnhancedCompletion.Create(Scope("SELECT * FROM dbo.Contacts c JOIN |"), Catalog());
        var match = Assert.Single(items, i => i.Label.StartsWith("billing.Payments", StringComparison.Ordinal));
        Assert.Equal("billing.Payments p on p.ContactID = c.ContactID", match.InsertText);
        Assert.Equal("same column name", match.Detail);
    }

    [Fact]
    public void Tables_already_in_the_statement_are_not_suggested_again()
    {
        var items = EnhancedCompletion.Create(Scope("SELECT * FROM dbo.Units u JOIN dbo.Buildings b ON b.BID = u.BID JOIN |"), Catalog());
        Assert.DoesNotContain(items, i => i.Label.StartsWith("Buildings", StringComparison.Ordinal));
        Assert.Contains(items, i => i.Label.StartsWith("Contacts", StringComparison.Ordinal));
    }

    [Fact]
    public void On_suggestions_put_the_joined_table_first()
    {
        var items = EnhancedCompletion.Create(Scope("SELECT * FROM dbo.Units u JOIN dbo.Buildings b ON |"), Catalog());
        Assert.Equal("b.BID = u.BID", items[0].Label);
    }

    [Fact]
    public void The_select_list_offers_the_column_picker()
    {
        var item = Assert.Single(EnhancedCompletion.Create(Scope("SELECT | FROM dbo.Units u"), Catalog()));
        Assert.Equal(CompletionKinds.Picker, item.Kind);
    }

    [Fact]
    public void Table_completions_after_from_get_an_alias_unless_one_is_written()
    {
        var items = new[] { new CompletionItemInfo("TableProblemsLastSub", CompletionKinds.Table, null, "TableProblemsLastSub", "2"), new CompletionItemInfo("x", CompletionKinds.Column, null, "x", "1") };
        var withAlias = EnhancedCompletion.WithAliases(items, Scope("SELECT * FROM |"));
        Assert.Equal("TableProblemsLastSub tpls", withAlias[0].InsertText);
        Assert.Equal("x", withAlias[1].InsertText);

        var aliasWritten = EnhancedCompletion.WithAliases(items, Scope("SELECT * FROM |Old o"));
        Assert.Equal("TableProblemsLastSub", aliasWritten[0].InsertText);
    }

    [Theory]
    [InlineData("TableProblemsLastSub", "tpls")]
    [InlineData("CC_Meshulam_Buildings", "cmb")]
    [InlineData("Buildings", "b")]
    [InlineData("XMLParser", "xp")]
    [InlineData("order_lines", "ol")]
    [InlineData("123", "t")]
    public void Aliases_are_the_lower_case_initials_of_the_name(string name, string alias) =>
        Assert.Equal(alias, AliasGenerator.Create(name, new HashSet<string>()));

    [Fact]
    public void A_taken_or_reserved_alias_gets_a_number()
    {
        Assert.Equal("b2", AliasGenerator.Create("Buildings", new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "B" }));
        // "OrderNames" → "on", a reserved word.
        Assert.Equal("on2", AliasGenerator.Create("OrderNames", new HashSet<string>()));
    }

    [Fact]
    public void Names_that_need_brackets_are_quoted()
    {
        Assert.Equal("Units", EnhancedCompletion.QuoteName("Units"));
        Assert.Equal("[Order Lines]", EnhancedCompletion.QuoteName("Order Lines"));
        Assert.Equal("[select]", EnhancedCompletion.QuoteName("select"));
        Assert.Equal("[a]]b]", EnhancedCompletion.QuoteName("a]b"));
    }

    [Theory]
    [InlineData("varchar", 20, 0, 0, "varchar(20)")]
    [InlineData("nvarchar", -1, 0, 0, "nvarchar(max)")]
    [InlineData("nvarchar", 100, 0, 0, "nvarchar(50)")]
    [InlineData("decimal", 9, 18, 2, "decimal(18,2)")]
    [InlineData("datetime2", 8, 27, 7, "datetime2(7)")]
    [InlineData("INT", 4, 10, 0, "int")]
    public void Column_types_are_written_as_in_tsql(string type, short maxLength, byte precision, byte scale, string expected) =>
        Assert.Equal(expected, CatalogSnapshot.TypeText(type, maxLength, precision, scale));

    [Fact]
    public void Parameter_defaults_are_read_from_the_module_definition()
    {
        var defaults = ObjectInfoReader.ParameterDefaults("CREATE FUNCTION dbo.f(@bid int, @top int = 10, @name nvarchar(10) = N'x') RETURNS TABLE AS RETURN SELECT 1 a");
        Assert.False(defaults.ContainsKey("@bid"));
        Assert.Equal("10", defaults["@top"]);
        Assert.Equal("N'x'", defaults["@name"]);
        Assert.Empty(ObjectInfoReader.ParameterDefaults("not sql at all ("));
    }

    [Theory]
    [InlineData("U", "Table")]
    [InlineData("V", "View")]
    [InlineData("P", "StoredProcedure")]
    [InlineData("IF", "TableFunction")]
    [InlineData("TF", "TableFunction")]
    [InlineData("FN", "ScalarFunction")]
    [InlineData("TR", null)]
    public void Object_types_map_to_explorer_script_types(string type, string? scriptType) =>
        Assert.Equal(scriptType, ObjectInfoReader.ScriptTypeOf(type));

    [Fact]
    public void The_catalog_finds_objects_by_default_schema_or_unique_name()
    {
        var catalog = Catalog();
        Assert.Equal("Units", catalog.Find(null, "units")?.Name);
        Assert.Equal("Payments", catalog.Find(null, "Payments")?.Name);
        Assert.Null(catalog.Find("dbo", "Payments"));
        Assert.Equal(2, catalog.ForeignKeysOf(catalog.Find(null, "Units")!).Count);
    }
}
