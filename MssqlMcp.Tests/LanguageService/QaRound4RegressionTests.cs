using Mssql.McpServer.LanguageService;

namespace MssqlMcp.Tests.LanguageService;

/// <summary>Regression tests for the fourth QA review: name-matched joins decided by identity and key position.</summary>
public sealed class QaRound4RegressionTests
{
    private static SqlScopeInfo Scope(string textWithCaret)
    {
        var caret = textWithCaret.IndexOf('|', StringComparison.Ordinal);
        return SqlScope.Analyze(textWithCaret.Remove(caret, 1), 1, caret + 1);
    }

    private static CatalogColumn Key(string name, int ordinal = 1, bool identity = false) => new(name, "int", false, true, identity, ordinal);

    private static CatalogColumn Col(string name, bool identity = false) => new(name, "int", true, false, identity);

    private static List<string> Joins(CatalogSnapshot catalog, string text) =>
        [.. EnhancedCompletion.Create(Scope(text), catalog).Select(i => i.InsertText)];

    // NET-021: one table keyed by plain Id next to tenant keys (CompanyId, Id).
    [Fact]
    public void A_single_id_keyed_table_beside_tenant_keys_gets_no_id_matches()
    {
        var catalog = new CatalogSnapshot(
        [
            new CatalogObject("dbo", "Companies", "U", [Key("Id", identity: true), Col("Name")]),
            new CatalogObject("dbo", "Customers", "U", [Key("CompanyId", 1), Key("Id", 2, identity: true)]),
            new CatalogObject("dbo", "Orders", "U", [Key("CompanyId", 1), Key("Id", 2, identity: true), Col("CustomerId")]),
        ], []);
        foreach (var text in new[]
        {
            "select * from Orders o join |", "select * from Companies c join |",
            "select * from Orders o join Companies c on |", "select * from Companies c join Orders o on |",
        })
        {
            Assert.DoesNotContain(Joins(catalog, text), j => j.Contains(".Id =", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Notes_keyed_by_parent_and_id_do_not_match_the_parents_id()
    {
        var catalog = new CatalogSnapshot(
        [
            new CatalogObject("dbo", "Customers", "U", [Key("Id", identity: true)]),
            new CatalogObject("dbo", "CustomerNotes", "U", [Key("CustomerId", 1), Key("Id", 2)]),
            new CatalogObject("dbo", "OrderNotes", "U", [Key("OrderId", 1), Key("Id", 2)]),
        ], []);
        Assert.DoesNotContain(Joins(catalog, "select * from CustomerNotes n join |"), j => j.Contains("c.Id = n.Id", StringComparison.Ordinal));
    }

    // NET-023: a keyless table with an Id column.
    [Fact]
    public void A_keyless_table_with_an_id_column_is_not_a_join_partner()
    {
        var catalog = new CatalogSnapshot(
        [
            new CatalogObject("dbo", "Customers", "U", [Key("Id", identity: true)]),
            new CatalogObject("dbo", "Orders", "U", [Key("Id", identity: true), Col("CustomerId")]),
            new CatalogObject("dbo", "ImportLog", "U", [Col("Id", identity: true), Col("Msg")]),
        ], []);
        Assert.Empty(Joins(catalog, "select * from Customers c join |"));
        Assert.Empty(Joins(catalog, "select * from ImportLog l join |"));
        Assert.Empty(Joins(catalog, "select * from ImportLog l join Customers c on |"));
    }

    [Fact]
    public void An_identity_column_is_never_the_referencing_side()
    {
        var catalog = new CatalogSnapshot(
        [
            new CatalogObject("dbo", "Buildings", "U", [Key("BID")]),
            new CatalogObject("dbo", "Audit", "U", [Key("AuditId", identity: true), Col("BID", identity: true)]),
        ], []);
        Assert.Empty(Joins(catalog, "select * from Buildings b join |"));
    }

    // NET-022: an archive copy or a second schema keyed the same way no longer hides the header-to-lines join.
    [Theory]
    [InlineData("dbo", "OrdersArchive")]
    [InlineData("t2", "Orders")]
    public void Header_to_lines_survives_other_tables_keyed_by_the_same_name(string schema, string name)
    {
        var catalog = new CatalogSnapshot(
        [
            new CatalogObject("dbo", "Orders", "U", [Key("OrderId", identity: true)]),
            new CatalogObject(schema, name, "U", [Key("OrderId")]),
            new CatalogObject("dbo", "OrderLines", "U", [Key("OrderId", 1), Key("LineNo", 2)]),
        ], []);
        Assert.Contains(Joins(catalog, "select * from dbo.Orders o join |"), j => j == "OrderLines ol on ol.OrderId = o.OrderId");
        Assert.Contains(Joins(catalog, "select * from OrderLines ol join |"), j => j == "Orders o on o.OrderId = ol.OrderId");
        Assert.Contains(Joins(catalog, "select * from dbo.Orders o join OrderLines ol on |"), j => j == "ol.OrderId = o.OrderId");
        Assert.Contains(Joins(catalog, "select * from OrderLines ol join dbo.Orders o on |"), j => j == "o.OrderId = ol.OrderId");
    }

    [Fact]
    public void The_last_part_of_a_composite_key_is_not_a_reference()
    {
        var catalog = new CatalogSnapshot(
        [
            new CatalogObject("dbo", "Batches", "U", [Key("BatchNo")]),
            new CatalogObject("dbo", "Items", "U", [Key("ItemGroup", 1), Key("BatchNo", 2)]),
        ], []);
        Assert.Empty(Joins(catalog, "select * from Batches b join |"));
    }
}
