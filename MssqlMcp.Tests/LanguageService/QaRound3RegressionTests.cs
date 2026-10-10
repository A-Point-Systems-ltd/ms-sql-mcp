using Mssql.McpServer.LanguageService;

namespace MssqlMcp.Tests.LanguageService;

/// <summary>Regression tests for the third QA review: name-matched joins on composite keys with generic columns.</summary>
public sealed class QaRound3RegressionTests
{
    private static SqlScopeInfo Scope(string textWithCaret)
    {
        var caret = textWithCaret.IndexOf('|', StringComparison.Ordinal);
        var text = textWithCaret.Remove(caret, 1);
        return SqlScope.Analyze(text, 1, caret + 1);
    }

    private static CatalogColumn Key(string name) => new(name, "int", false, true);

    private static CatalogColumn Col(string name) => new(name, "int", true, false);

    private static List<string> Joins(CatalogSnapshot catalog, string text) =>
        [.. EnhancedCompletion.Create(Scope(text), catalog).Select(i => i.InsertText)];

    [Fact]
    public void Tenant_keys_do_not_suggest_id_equals_id()
    {
        var catalog = new CatalogSnapshot(
        [
            new CatalogObject("dbo", "Customers", "U", [Key("CompanyId"), Key("Id")]),
            new CatalogObject("dbo", "Orders", "U", [Key("CompanyId"), Key("Id"), Col("CustomerId")]),
        ], []);
        Assert.DoesNotContain(Joins(catalog, "select * from Customers c join Orders o on |"), j => j.Contains("Id = c.Id", StringComparison.Ordinal));
        Assert.DoesNotContain(Joins(catalog, "select * from Customers c join |"), j => j.Contains(".Id = c.Id", StringComparison.Ordinal));
    }

    [Fact]
    public void A_composite_part_named_like_many_single_keys_is_not_matched()
    {
        var catalog = new CatalogSnapshot(
        [
            new CatalogObject("dbo", "CustomerNotes", "U", [Key("CustomerId"), Key("Id")]),
            new CatalogObject("dbo", "Customers", "U", [Key("Id")]),
            new CatalogObject("dbo", "Settings", "U", [Key("Id")]),
        ], []);
        Assert.DoesNotContain(Joins(catalog, "select * from CustomerNotes n join |"), j => j.Contains(".Id = n.Id", StringComparison.Ordinal));
        Assert.DoesNotContain(Joins(catalog, "select * from Customers c join |"), j => j.Contains("cn.Id = c.Id", StringComparison.Ordinal));
        Assert.Empty(Joins(catalog, "select * from Customers c join CustomerNotes cn on |"));
    }

    [Fact]
    public void Two_line_tables_do_not_match_line_numbers()
    {
        var catalog = new CatalogSnapshot(
        [
            new CatalogObject("dbo", "OrderLines", "U", [Key("OrderId"), Key("LineNo")]),
            new CatalogObject("dbo", "InvoiceLines", "U", [Key("InvoiceId"), Key("LineNo")]),
        ], []);
        Assert.Empty(Joins(catalog, "select * from OrderLines ol join |"));
        Assert.Empty(Joins(catalog, "select * from OrderLines ol join InvoiceLines il on |"));
    }

    [Theory]
    [InlineData(2, 0, true)]
    [InlineData(2, 2, false)]
    [InlineData(1, 1, false)]
    [InlineData(0, 0, false)]
    [InlineData(1, 0, false)]
    public void The_name_match_rule_by_column_kind(int kindA, int kindB, bool allowed)
    {
        // kind: 2 = single-column key, 1 = composite-key part, 0 = non-key.
        CatalogObject Table(string name, int kind) => kind switch
        {
            2 => new CatalogObject("dbo", name, "U", [Key("X")]),
            1 => new CatalogObject("dbo", name, "U", [Key("X"), Key(name + "Line")]),
            _ => new CatalogObject("dbo", name, "U", [Key(name + "Id"), Col("X")]),
        };
        var a = Table("A", kindA);
        var b = Table("B", kindB);
        var catalog = new CatalogSnapshot([a, b], []);
        Assert.Equal(allowed, EnhancedCompletion.NameMatchAllowed(catalog, a, a.Column("X")!, b, b.Column("X")!));
    }
}
