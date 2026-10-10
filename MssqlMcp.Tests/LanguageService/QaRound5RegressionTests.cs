using Mssql.McpServer.LanguageService;

namespace MssqlMcp.Tests.LanguageService;

/// <summary>Regression tests for the fifth QA review's P4 items: schema-aware ranking and natural-key names.</summary>
public sealed class QaRound5RegressionTests
{
    private static List<string> Joins(CatalogSnapshot catalog, string textWithCaret)
    {
        var caret = textWithCaret.IndexOf('|', StringComparison.Ordinal);
        var scope = SqlScope.Analyze(textWithCaret.Remove(caret, 1), 1, caret + 1);
        return [.. EnhancedCompletion.Create(scope, catalog).Select(i => i.InsertText)];
    }

    private static CatalogColumn Key(string name, int ordinal = 1) => new(name, "int", false, true, false, ordinal);

    private static CatalogColumn Col(string name) => new(name, "int", true, false);

    // NET-025: one schema per tenant.
    [Fact]
    public void Same_schema_partners_come_first_and_survive_the_cap()
    {
        var objects = new List<CatalogObject>();
        for (var t = 1; t <= 60; t++)
        {
            var schema = $"c{t:D3}";
            objects.Add(new CatalogObject(schema, "Orders", "U", [Key("OrderId")]));
            objects.Add(new CatalogObject(schema, "OrderLines", "U", [Key("OrderId", 1), Key("LineNo", 2)]));
        }

        var catalog = new CatalogSnapshot(objects, []);
        Assert.Equal("c055.OrderLines ol on ol.OrderId = o.OrderId", Joins(catalog, "select * from c055.Orders o join |")[0]);
        Assert.Equal("c055.Orders o on o.OrderId = ol.OrderId", Joins(catalog, "select * from c055.OrderLines ol join |")[0]);
    }

    // NET-026: lookup tables keyed by a bare Code or Name.
    [Fact]
    public void Natural_key_names_join_only_through_foreign_keys()
    {
        var countries = new CatalogObject("dbo", "Countries", "U", [new CatalogColumn("Code", "char(2)", false, true, false, 1)]);
        var settings = new CatalogObject("dbo", "Settings", "U", [new CatalogColumn("Name", "nvarchar(50)", false, true, false, 1)]);
        var products = new CatalogObject("dbo", "Products", "U", [Key("ProductId"), Col("Code"), Col("Name"), Col("CountryCode")]);
        var withoutFk = new CatalogSnapshot([countries, settings, products], []);
        Assert.Empty(Joins(withoutFk, "select * from Products p join |"));
        Assert.Empty(Joins(withoutFk, "select * from Countries c join |"));
        Assert.Empty(Joins(withoutFk, "select * from Products p join Countries c on |"));

        var withFk = new CatalogSnapshot([countries, settings, products], [("FK_P_C", "dbo", "Products", "dbo", "Countries", "CountryCode", "Code")]);
        Assert.Contains("Countries c on c.Code = p.CountryCode", Joins(withFk, "select * from Products p join |"));
    }
}
