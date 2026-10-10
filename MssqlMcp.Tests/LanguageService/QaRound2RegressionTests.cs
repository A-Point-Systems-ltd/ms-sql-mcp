using Mssql.McpServer.LanguageService;

namespace MssqlMcp.Tests.LanguageService;

/// <summary>Regression tests for the second QA review of the SQL editor enhancements (no database needed).</summary>
public sealed class QaRound2RegressionTests
{
    private static SqlScopeInfo Scope(string textWithCaret)
    {
        var caret = textWithCaret.IndexOf('|', StringComparison.Ordinal);
        var text = textWithCaret.Remove(caret, 1);
        var before = text[..caret];
        return SqlScope.Analyze(text, before.Count(c => c == '\n') + 1, caret - (before.LastIndexOf('\n') + 1) + 1);
    }

    // NET-015: composite-key parts still join by name; only single key = single key is excluded.
    private static CatalogSnapshot OrdersCatalog() => new(
        [
            new CatalogObject("dbo", "Orders", "U", [new CatalogColumn("OrderId", "int", false, true), new CatalogColumn("Total", "money", true, false)]),
            new CatalogObject("dbo", "OrderLines", "U", [new CatalogColumn("OrderId", "int", false, true), new CatalogColumn("LineNo", "int", false, true)]),
        ],
        []);

    [Fact]
    public void Header_to_lines_joins_by_a_composite_key_part_are_suggested_both_ways()
    {
        var catalog = OrdersCatalog();
        Assert.Contains(EnhancedCompletion.Create(Scope("select * from Orders o join |"), catalog), i => i.InsertText == "OrderLines ol on ol.OrderId = o.OrderId");
        Assert.Contains(EnhancedCompletion.Create(Scope("select * from OrderLines ol join |"), catalog), i => i.InsertText == "Orders o on o.OrderId = ol.OrderId");
        Assert.Contains(EnhancedCompletion.Create(Scope("select * from Orders o join OrderLines ol on |"), catalog), i => i.InsertText == "ol.OrderId = o.OrderId");
        Assert.Contains(EnhancedCompletion.Create(Scope("select * from OrderLines ol join Orders o on |"), catalog), i => i.InsertText == "o.OrderId = ol.OrderId");
    }

    // NET-014: the window never starts or ends inside a literal or comment, and GO lines work with CRLF.
    [Theory]
    [InlineData("string")]
    [InlineData("comment")]
    [InlineData("go-in-literal-lf")]
    [InlineData("go-in-literal-crlf")]
    public void Long_or_go_containing_literals_before_the_caret_keep_the_join_context(string kind)
    {
        var big = new string('x', SqlScope.MaxWindow + 1000);
        var prefix = kind switch
        {
            "string" => $"select N'{big}' as s\n",
            "comment" => $"/* it's {big} */\n",
            "go-in-literal-lf" => "select N'a\ngo\nb' as s\n",
            _ => "select N'a\r\ngo\r\nb' as s\r\n",
        };
        var scope = Scope(prefix + "select * from Customers c join |");
        Assert.Equal(CaretContext.JoinTable, scope.Context);
        Assert.Equal("Customers", Assert.Single(scope.Tables).Name);
    }

    [Fact]
    public void A_crlf_go_line_splits_batches()
    {
        const string text = "select * from A a\r\ngo\r\nselect * from B b join \r\nGO 2\r\nselect * from C c";
        var caret = text.IndexOf("join ", StringComparison.Ordinal) + 5;
        var (start, end) = SqlScope.Window(text, caret);
        Assert.Equal("select * from B b join \r\n", text[start..end]);
    }

    // NET-016: DELETE TOP (n) [PERCENT] FROM takes no alias either.
    [Theory]
    [InlineData("delete top (10) from Ord|", CaretContext.Other)]
    [InlineData("delete top (10) percent from dbo.Ord|", CaretContext.Other)]
    [InlineData("delete top (10) o from Ord|", CaretContext.FromTable)]
    [InlineData("select top (10) * from Ord|", CaretContext.FromTable)]
    public void Delete_top_from_takes_no_alias(string text, CaretContext expected) =>
        Assert.Equal(expected, Scope(text).Context);
}
