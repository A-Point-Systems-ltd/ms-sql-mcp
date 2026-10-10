using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Mssql.McpServer.Connections;
using Mssql.McpServer.LanguageService;
using Mssql.McpServer.LanguageService.Formatting;

namespace MssqlMcp.Tests.LanguageService;

/// <summary>Regression tests for the first QA review of the SQL editor enhancements (no database needed).</summary>
public sealed class QaRound1RegressionTests
{
    private static SqlScopeInfo Scope(string textWithCaret)
    {
        var caret = textWithCaret.IndexOf('|', StringComparison.Ordinal);
        var text = textWithCaret.Remove(caret, 1);
        var before = text[..caret];
        return SqlScope.Analyze(text, before.Count(c => c == '\n') + 1, caret - (before.LastIndexOf('\n') + 1) + 1);
    }

    // NET-001: the fallback for a document that does not parse never formats inside a literal or comment.
    [Fact]
    public void Range_fallback_never_edits_inside_a_string_literal_of_a_broken_document()
    {
        const string text = "insert into dbo.Templates (Body) values (N'\nSELECT   Name ,  Total\nFROM     Orders\n')\nselec broken\n";
        var result = SqlFormatter.Format(text, range: new TextRange(2, 1, 3, 16));
        Assert.True(!result.Success || result.Edits.Count == 0);
    }

    [Fact]
    public void Range_fallback_never_edits_inside_a_block_comment_of_a_broken_document()
    {
        const string text = "/*\nSELECT   a FROM t\n*/\nselec broken\n";
        var result = SqlFormatter.Format(text, range: new TextRange(2, 1, 2, 19));
        Assert.True(!result.Success || result.Edits.Count == 0);
    }

    [Fact]
    public void Range_fallback_uses_the_documents_crlf_line_breaks()
    {
        const string text = "selec broken\r\nSELECT a FROM t WHERE a=1 AND b=2\r\n";
        var result = SqlFormatter.Format(text, range: new TextRange(2, 1, 2, 10));
        Assert.True(result.Success, result.Error);
        Assert.NotEmpty(result.Edits);
        Assert.All(result.Edits.Where(e => e.NewText.Contains('\n', StringComparison.Ordinal)), e => Assert.Contains("\r\n", e.NewText, StringComparison.Ordinal));
        Assert.DoesNotContain(result.Edits, e => e.NewText.Replace("\r\n", string.Empty, StringComparison.Ordinal).Contains('\n', StringComparison.Ordinal));
    }

    // NET-003: DELETE FROM and BULK INSERT ... FROM take no alias.
    [Theory]
    [InlineData("delete from Ord|", CaretContext.Other)]
    [InlineData("delete from dbo.Ord|", CaretContext.Other)]
    [InlineData("delete o from Ord|", CaretContext.FromTable)]
    [InlineData("bulk insert dbo.T from |", CaretContext.Other)]
    [InlineData("select * from Ord|", CaretContext.FromTable)]
    public void Alias_context_skips_delete_from_and_bulk_insert(string text, CaretContext expected) =>
        Assert.Equal(expected, Scope(text).Context);

    // NET-008: key-to-key name matches (Id = Id) are not suggested.
    [Fact]
    public void Id_convention_catalogs_get_no_id_equals_id_joins()
    {
        static CatalogObject T(string name, params string[] extra) =>
            new("dbo", name, "U", [new CatalogColumn("Id", "int", false, true), .. extra.Select(c => new CatalogColumn(c, "int", true, false))]);
        var catalog = new CatalogSnapshot([T("Customers"), T("Orders", "CustomerId"), T("Invoices"), T("Buildings")], []);
        var join = EnhancedCompletion.Create(Scope("select * from Customers c join |"), catalog);
        Assert.DoesNotContain(join, i => i.InsertText.Contains(".Id = c.Id", StringComparison.Ordinal));

        var on = EnhancedCompletion.Create(Scope("select * from Customers c join Orders o on |"), catalog);
        Assert.DoesNotContain(on, i => i.InsertText == "o.Id = c.Id");
    }

    [Fact]
    public void Key_to_non_key_name_matches_are_still_suggested()
    {
        var buildings = new CatalogObject("dbo", "Buildings", "U", [new CatalogColumn("BID", "int", false, true)]);
        var cmb = new CatalogObject("dbo", "CC_Meshulam_Buildings", "U", [new CatalogColumn("Id", "int", false, true), new CatalogColumn("BID", "int", true, false)]);
        var catalog = new CatalogSnapshot([buildings, cmb], []);
        var items = EnhancedCompletion.Create(Scope("select * from CC_Meshulam_Buildings CMB join |"), catalog);
        Assert.Contains(items, i => i.InsertText == "Buildings b on b.BID = CMB.BID");
        var reverse = EnhancedCompletion.Create(Scope("select * from Buildings B join |"), catalog);
        Assert.Contains(reverse, i => i.InsertText == "CC_Meshulam_Buildings cmb on cmb.BID = B.BID");
    }

    // NET-009: the scope reads only the batch around the caret, and JOIN suggestions do not scan the catalog.
    [Fact]
    public void Scope_reads_only_the_go_batch_around_the_caret()
    {
        const string text = "select * from A a\ngo\nselect * from B b join \ngo\nselect * from C c";
        var caret = text.IndexOf("join ", StringComparison.Ordinal) + 5;
        var (start, end) = SqlScope.Window(text, caret);
        Assert.Equal("select * from B b join \n", text[start..end]);
        var scope = SqlScope.Analyze(text, 3, 24);
        Assert.Equal("B", Assert.Single(scope.Tables).Name);
    }

    [Fact]
    public void Enhanced_completion_stays_fast_on_a_large_document_and_catalog()
    {
        var tables = Enumerable.Range(0, 20_000).Select(i =>
            new CatalogObject("dbo", $"T{i}", "U", [new CatalogColumn($"T{i}Id", "int", false, true), new CatalogColumn("Code", "int", true, false)])).ToList();
        var catalog = new CatalogSnapshot(tables, []);
        var filler = string.Concat(Enumerable.Repeat("select a, b, c from dbo.Filler f where f.a = 1\n", 40_000));
        var text = filler + "select * from T1 a join T2 b on b.T2Id = a.Code join ";
        var lines = text.Split('\n');
        var watch = Stopwatch.StartNew();
        var scope = SqlScope.Analyze(text, lines.Length, lines[^1].Length + 1);
        var items = EnhancedCompletion.Create(scope, catalog);
        watch.Stop();
        Assert.Equal(CaretContext.JoinTable, scope.Context);
        // Generous for slow CI machines; locally this is a few milliseconds.
        Assert.True(watch.ElapsedMilliseconds < 2000, $"{watch.ElapsedMilliseconds} ms");
        Assert.True(items.Count <= EnhancedCompletion.MaxSuggestions);
    }

    // NET-010: a failed reload keeps serving the last good catalog, also after the retry.
    [Fact]
    public async Task A_failed_catalog_reload_keeps_the_last_good_snapshot()
    {
        var profile = new ConnectionProfile("main", "Server=(localdb)\\none;Database=x", false, false, ConnectionSource.Configured);
        var registry = new ConnectionRegistry([profile]);
        var time = new ManualTime();
        using var cache = new LanguageServiceCache(registry, NullLogger<LanguageServiceCache>.Instance, time);
        var good = new CatalogSnapshot([new CatalogObject("dbo", "T", "U", [])], []);
        var fail = false;
        cache.CatalogLoader = _ => fail ? Task.FromException<CatalogSnapshot>(new InvalidOperationException("down")) : Task.FromResult(good);

        Assert.Same(good, await cache.TryCatalogAsync(profile, TimeSpan.FromSeconds(5), CancellationToken.None));
        fail = true;
        time.Advance(LanguageServiceCache.CatalogTtl + TimeSpan.FromSeconds(1));
        Assert.Same(good, await cache.TryCatalogAsync(profile, TimeSpan.FromSeconds(5), CancellationToken.None));
        await Task.Delay(50);
        time.Advance(TimeSpan.FromMinutes(1));
        Assert.Same(good, await cache.TryCatalogAsync(profile, TimeSpan.FromSeconds(5), CancellationToken.None));
    }

    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
