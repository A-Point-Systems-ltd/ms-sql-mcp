using Microsoft.Extensions.Logging.Abstractions;
using Mssql.McpServer;
using Mssql.McpServer.Connections;
using Mssql.McpServer.LanguageService;

namespace MssqlMcp.Tests.LanguageService;

/// <summary>Enhanced completions, scope, objectInfo and format_sql through the cache and the tool, on the LocalDB scratch database.</summary>
[Collection(LanguageServiceCollection.Name)]
public sealed class EnhancedCompletionScratchDbTests(LanguageServiceDatabase db) : IClassFixture<LanguageServiceDatabase>
{
    private (LanguageServiceCache Cache, ConnectionRegistry Registry, ConnectionProfile Main) Create()
    {
        Skip.If(db.SkipReason is not null, db.SkipReason);
        var main = new ConnectionProfile("main", db.ConnectionString!, true, false, ConnectionSource.Configured);
        var registry = new ConnectionRegistry([main]);
        var cache = new LanguageServiceCache(registry, NullLogger<LanguageServiceCache>.Instance) { CompletionTimeout = TimeSpan.FromSeconds(60) };
        return (cache, registry, main);
    }

    [SkippableFact]
    public async Task Join_completion_offers_the_foreign_key_join_first()
    {
        var (cache, _, main) = Create();
        using var _ = cache;
        const string text = "SELECT * FROM dbo.Units u JOIN ";
        var list = await cache.CompleteAsync(main, text, 1, text.Length + 1, enhanced: true, CancellationToken.None);
        var first = list.Items[0];
        Assert.Equal(CompletionKinds.Join, first.Kind);
        Assert.Equal("Buildings b on b.BID = u.BID", first.InsertText);
        Assert.Equal("FK FK_Units_Buildings", first.Detail);
    }

    [SkippableFact]
    public async Task Without_the_switch_completion_is_unchanged()
    {
        var (cache, _, main) = Create();
        using var _ = cache;
        const string text = "SELECT * FROM dbo.Units u JOIN ";
        var list = await cache.CompleteAsync(main, text, 1, text.Length + 1, CancellationToken.None);
        Assert.DoesNotContain(list.Items, i => i.Kind == CompletionKinds.Join);
        Assert.DoesNotContain(list.Items, i => i.InsertText.Contains(' ', StringComparison.Ordinal) && i.Kind == CompletionKinds.Table);
    }

    [SkippableFact]
    public async Task On_completion_and_table_aliases_after_from()
    {
        var (cache, _, main) = Create();
        using var _ = cache;
        const string on = "SELECT * FROM dbo.Units u JOIN dbo.Buildings b ON ";
        var onList = await cache.CompleteAsync(main, on, 1, on.Length + 1, enhanced: true, CancellationToken.None);
        Assert.Equal("b.BID = u.BID", onList.Items[0].InsertText);

        const string from = "SELECT * FROM ";
        var fromList = await cache.CompleteAsync(main, from, 1, from.Length + 1, enhanced: true, CancellationToken.None);
        Assert.Contains(fromList.Items, i => i.Kind == CompletionKinds.Table && i.InsertText == "Buildings b");
    }

    [SkippableFact]
    public async Task Scope_returns_the_statement_tables_with_their_columns()
    {
        var (cache, _, main) = Create();
        using var _ = cache;
        const string text = "SELECT  FROM dbo.Units u JOIN Buildings b ON b.BID = u.BID";
        var scope = await cache.ScopeAsync(main, text, 1, 8, CancellationToken.None);
        Assert.Equal(CacheStates.Warm, scope.CacheState);
        Assert.Equal(["u", "b"], scope.Tables.Select(t => t.Alias));
        Assert.Equal(["UID", "BID", "Name"], scope.Tables[0].Columns.Select(c => c.Name));
        Assert.True(scope.Tables[0].Columns[0].IsKey);
        Assert.Equal("nvarchar(200)", scope.Tables[1].Columns[1].Type);
    }

    [SkippableFact]
    public async Task Object_info_resolves_tables_functions_with_defaults_synonyms_and_misses()
    {
        var (cache, _, main) = Create();
        using var _ = cache;
        var table = await cache.ObjectInfoAsync(main, "Buildings", CancellationToken.None);
        Assert.True(table.Found);
        Assert.Equal(("dbo", "Buildings", "Table"), (table.Schema, table.Name, table.ScriptType));

        var tvf = await cache.ObjectInfoAsync(main, "[dbo].[fnUnits]", CancellationToken.None);
        Assert.Equal("TableFunction", tvf.ScriptType);
        Assert.Equal(["@bid int", "@top int = 10"], tvf.Parameters.Select(p => $"{p.Name} {p.Type}{(p.Default is null ? "" : " = " + p.Default)}"));

        var synonym = await cache.ObjectInfoAsync(main, "dbo.Blds", CancellationToken.None);
        Assert.Equal("Buildings", synonym.Name);

        Assert.False((await cache.ObjectInfoAsync(main, "dbo.NoSuchThing", CancellationToken.None)).Found);
    }

    [SkippableFact]
    public async Task Object_info_never_resolves_objects_of_another_database()
    {
        var (cache, _, main) = Create();
        using var _ = cache;
        var current = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(db.ConnectionString).InitialCatalog;
        Assert.False((await cache.ObjectInfoAsync(main, "msdb.dbo.sysjobs", CancellationToken.None)).Found);
        Assert.False((await cache.ObjectInfoAsync(main, "dbo.ExtJobs", CancellationToken.None)).Found);
        Assert.False((await cache.ObjectInfoAsync(main, "srv.msdb.dbo.sysjobs", CancellationToken.None)).Found);
        Assert.True((await cache.ObjectInfoAsync(main, $"[{current}].dbo.Buildings", CancellationToken.None)).Found);
        Assert.Equal("Buildings", (await cache.ObjectInfoAsync(main, "dbo.Blds", CancellationToken.None)).Name);
    }

    [SkippableFact]
    public async Task Scope_columns_carry_bracketed_names_when_needed()
    {
        var (cache, _, main) = Create();
        using var _ = cache;
        var scope = await cache.ScopeAsync(main, "SELECT  FROM sales.Orders", 1, 8, CancellationToken.None);
        var table = Assert.Single(scope.Tables);
        Assert.Equal("Orders", table.QuotedQualifier);
        Assert.Equal(["id", "total"], table.Columns.Select(c => c.QuotedName));
    }

    [SkippableFact]
    public async Task The_tool_serves_scope_and_object_info_and_validates_the_name()
    {
        var (cache, registry, main) = Create();
        using var _ = cache;
        var tools = new ScriptRunnerTools(new SqlConnectionFactory(registry), NullLogger<ScriptRunnerTools>.Instance, cache);
        using (CurrentConnection.Use(main))
        {
            var info = await tools.LanguageService("objectInfo", name: "dbo.Units");
            Assert.True(info.Success, info.Error);
            var missing = await tools.LanguageService("objectInfo", name: " ");
            Assert.False(missing.Success);
            var scope = await tools.LanguageService("scope", "SELECT * FROM dbo.Units", 1, 8);
            Assert.True(scope.Success, scope.Error);
        }
    }
}
