using Microsoft.Extensions.Logging.Abstractions;
using Mssql.McpServer;
using Mssql.McpServer.Connections;
using Mssql.McpServer.LanguageService;

namespace MssqlMcp.Tests.LanguageService;

/// <summary>One LocalDB scratch database with a small schema, shared by the language service tests and dropped at the end.</summary>
public sealed class LanguageServiceDatabase : IAsyncLifetime
{
    private ScratchDatabases? _scratch;

    public string? ConnectionString { get; private set; }

    public string? SkipReason { get; private set; }

    public async Task InitializeAsync()
    {
        try
        {
            _scratch = await ScratchDatabases.CreateAsync(1);
        }
        catch (SkipException ex)
        {
            SkipReason = ex.Message;
            return;
        }

        ConnectionString = _scratch.ConnectionStrings[0];
        await ScratchDatabases.ExecAsync(ConnectionString, "CREATE TABLE dbo.T (a int, b nvarchar(10));");
        await ScratchDatabases.ExecAsync(ConnectionString, "EXEC(N'CREATE SCHEMA sales');");
        await ScratchDatabases.ExecAsync(ConnectionString, "CREATE TABLE sales.Orders (id int, total money);");
        await ScratchDatabases.ExecAsync(ConnectionString, "EXEC(N'CREATE PROCEDURE dbo.p @x int AS SELECT @x;');");
        await ScratchDatabases.ExecAsync(ConnectionString, "EXEC(N'CREATE PROCEDURE dbo.p2 @x int, @y nvarchar(10) OUTPUT AS SELECT @x;');");
    }

    public async Task DisposeAsync()
    {
        if (_scratch is not null)
        {
            await _scratch.DisposeAsync();
        }
    }
}

/// <summary>
/// language_service against a LocalDB scratch database: completion contexts, hover, signature help, refresh,
/// the binding timeout, read-only profiles and cache invalidation.
/// </summary>
public sealed class LanguageServiceScratchDbTests(LanguageServiceDatabase db) : IClassFixture<LanguageServiceDatabase>
{
    private (LanguageServiceCache Cache, ConnectionRegistry Registry, ConnectionProfile Main) Create(bool readOnly = false, TimeSpan? timeout = null, TimeProvider? time = null)
    {
        Skip.If(db.SkipReason is not null, db.SkipReason);
        var main = new ConnectionProfile("main", db.ConnectionString!, readOnly, false, ConnectionSource.Configured);
        var other = new ConnectionProfile("other", db.ConnectionString!, true, false, ConnectionSource.Configured);
        var registry = new ConnectionRegistry([main, other]);
        var cache = new LanguageServiceCache(new SqlConnectionFactory(registry), registry, NullLogger<LanguageServiceCache>.Instance, time)
        {
            // Generous by default so a cold LocalDB bind cannot make a context test flaky; the timeout test sets its own.
            CompletionTimeout = timeout ?? TimeSpan.FromSeconds(60),
        };
        return (cache, registry, main);
    }

    private static async Task<CompletionList> CompleteAsync(LanguageServiceCache cache, ConnectionProfile profile, string text, int line, int column)
    {
        var result = await cache.CompleteAsync(profile, text, line, column, CancellationToken.None);
        Assert.Equal("warm", result.CacheState);
        return result;
    }

    private static CompletionItemInfo Item(CompletionList list, string label, string kind) =>
        Assert.Single(list.Items, i => i.Label == label && i.Kind == kind);

    [SkippableFact]
    public async Task Tables_are_offered_after_FROM_and_JOIN()
    {
        var (cache, _, main) = Create();
        using var _ = cache;

        Item(await CompleteAsync(cache, main, "SELECT * FROM ", 1, 15), "T", "table");
        Item(await CompleteAsync(cache, main, "SELECT * FROM dbo.T t JOIN ", 1, 28), "T", "table");
    }

    [SkippableFact]
    public async Task Alias_columns_are_offered_after_the_alias_dot()
    {
        var (cache, _, main) = Create();
        using var _ = cache;

        var list = await CompleteAsync(cache, main, "SELECT t. FROM dbo.T t", 1, 10);
        Item(list, "a", "column");
        Item(list, "b", "column");
    }

    [SkippableFact]
    public async Task Schema_qualified_names_offer_only_that_schemas_objects()
    {
        var (cache, _, main) = Create();
        using var _ = cache;

        var list = await CompleteAsync(cache, main, "SELECT * FROM sales.", 1, 21);
        Item(list, "Orders", "table");
        Assert.DoesNotContain(list.Items, i => i.Label == "T");
    }

    [SkippableFact]
    public async Task Cte_and_derived_table_columns_are_offered()
    {
        var (cache, _, main) = Create();
        using var _ = cache;

        Item(await CompleteAsync(cache, main, "WITH c AS (SELECT a AS ca FROM dbo.T) SELECT c. FROM c", 1, 48), "ca", "column");
        Item(await CompleteAsync(cache, main, "SELECT d. FROM (SELECT b AS db2 FROM dbo.T) d", 1, 10), "db2", "column");
    }

    [SkippableFact]
    public async Task Keywords_are_offered_at_the_start_of_a_statement()
    {
        var (cache, _, main) = Create();
        using var _ = cache;

        var list = await CompleteAsync(cache, main, "", 1, 1);
        Item(list, "SELECT", "keyword");
        Item(list, "INSERT", "keyword");
    }

    [SkippableTheory]
    [InlineData("EXEC dbo.p ")]
    [InlineData("EXECUTE dbo.p ")]
    [InlineData("EXEC p ")]
    [InlineData("exec dbo.p\n  ")]
    public async Task Exec_offers_the_procedure_parameters_ahead_of_the_global_variables(string text)
    {
        var (cache, _, main) = Create();
        using var _ = cache;
        var lines = text.Split('\n');

        var list = await CompleteAsync(cache, main, text, lines.Length, lines[^1].Length + 1);

        var x = Item(list, "@x", "parameter");
        Assert.Equal("@x = ", x.InsertText);
        Assert.Contains("int", x.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.All(
            list.Items.Where(i => i.Label.StartsWith("@@", StringComparison.Ordinal)),
            g => Assert.True(string.CompareOrdinal(x.SortText, g.SortText) < 0, $"{x.SortText} !< {g.SortText}"));
    }

    [SkippableFact]
    public async Task Exec_offers_the_remaining_parameters_and_marks_OUTPUT()
    {
        var (cache, _, main) = Create();
        using var _ = cache;

        var list = await CompleteAsync(cache, main, "EXEC dbo.p2 @x = 1, ", 1, 21);
        var y = Item(list, "@y", "parameter");
        Assert.Contains("OUTPUT", y.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(list.Items, i => i.Label == "@x");

        // A positional first argument also uses @x up.
        var positional = await CompleteAsync(cache, main, "EXEC dbo.p2 1, ", 1, 16);
        Item(positional, "@y", "parameter");
        Assert.DoesNotContain(positional.Items, i => i.Label == "@x");
    }

    [SkippableFact]
    public async Task Hover_on_a_column_shows_its_type()
    {
        var (cache, _, main) = Create();
        using var _ = cache;

        var hover = await cache.HoverAsync(main, "SELECT a FROM dbo.T", 1, 9, CancellationToken.None);

        Assert.NotNull(hover);
        Assert.Contains("int", hover.Contents, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(hover.Range);
        Assert.Equal(1, hover.Range.StartLine);
    }

    [SkippableFact]
    public async Task Signature_help_covers_a_builtin_and_a_user_procedure()
    {
        var (cache, _, main) = Create();
        using var _ = cache;

        var dateadd = await cache.SignatureHelpAsync(main, "SELECT DATEADD(", 1, 16, CancellationToken.None);
        Assert.NotNull(dateadd);
        var sig = dateadd.Signatures[dateadd.ActiveSignature];
        Assert.Contains("DATEADD", sig.Label, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(3, sig.Parameters.Count);
        Assert.Equal(0, dateadd.ActiveParameter);

        var proc = await cache.SignatureHelpAsync(main, "EXEC dbo.p2 1, ", 1, 16, CancellationToken.None);
        Assert.NotNull(proc);
        var p2 = proc.Signatures[proc.ActiveSignature];
        Assert.Contains("p2", p2.Label, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, p2.Parameters.Count);
        Assert.Equal(1, proc.ActiveParameter);
    }

    [SkippableFact]
    public async Task Refresh_sees_a_newly_created_table()
    {
        var (cache, _, main) = Create();
        using var _ = cache;
        var table = "New_" + Guid.NewGuid().ToString("N")[..8];

        Item(await CompleteAsync(cache, main, "SELECT * FROM ", 1, 15), "T", "table");
        await ScratchDatabases.ExecAsync(db.ConnectionString!, $"CREATE TABLE dbo.{table} (id int);");
        try
        {
            Assert.Equal("loading", cache.Refresh(main));

            Item(await CompleteAsync(cache, main, "SELECT * FROM ", 1, 15), table, "table");
        }
        finally
        {
            await ScratchDatabases.ExecAsync(db.ConnectionString!, $"DROP TABLE dbo.{table};");
        }
    }

    [SkippableFact]
    public async Task A_binding_timeout_returns_keywords_marked_loading_and_binding_carries_on()
    {
        var (cache, _, main) = Create(timeout: TimeSpan.FromMilliseconds(1));
        using var _ = cache;

        var cold = await cache.CompleteAsync(main, "SELECT * FROM ", 1, 15, CancellationToken.None);

        Assert.Equal("loading", cold.CacheState);
        Assert.True(cold.IsIncomplete);
        Assert.NotEmpty(cold.Items);
        Assert.All(cold.Items, i => Assert.Equal("keyword", i.Kind));

        // The background build keeps going; once it is done a later call is warm.
        cache.CompletionTimeout = TimeSpan.FromSeconds(60);
        var warm = await CompleteAsync(cache, main, "SELECT * FROM ", 1, 15);
        Assert.False(warm.IsIncomplete);
        Item(warm, "T", "table");
    }

    [SkippableFact]
    public async Task Warm_returns_at_once_and_builds_in_the_background()
    {
        var (cache, _, main) = Create();
        using var _ = cache;

        Assert.Equal("loading", cache.Warm(main));
        Item(await CompleteAsync(cache, main, "SELECT * FROM ", 1, 15), "T", "table");
        Assert.Equal("warm", cache.Warm(main));
    }

    [SkippableFact]
    public async Task Read_only_profiles_get_completions()
    {
        var (cache, _, main) = Create(readOnly: true);
        using var _ = cache;

        Item(await CompleteAsync(cache, main, "SELECT t. FROM dbo.T t", 1, 10), "a", "column");
    }

    [SkippableFact]
    public async Task Closing_the_connection_drops_its_cache_entry()
    {
        var (cache, registry, main) = Create();
        using var _ = cache;

        await CompleteAsync(cache, main, "SELECT * FROM ", 1, 15);
        Assert.Equal(1, cache.EntryCount);

        Assert.True(registry.Close("main"));

        Assert.Equal(0, cache.EntryCount);
    }

    [SkippableFact]
    public async Task Entries_idle_for_30_minutes_are_dropped()
    {
        var time = new ManualTimeProvider();
        var (cache, _, main) = Create(time: time);
        using var _ = cache;

        await CompleteAsync(cache, main, "SELECT * FROM ", 1, 15);
        time.Advance(TimeSpan.FromMinutes(29));
        cache.EvictIdle();
        Assert.Equal(1, cache.EntryCount);

        time.Advance(TimeSpan.FromMinutes(2));
        cache.EvictIdle();
        Assert.Equal(0, cache.EntryCount);
    }

    [SkippableFact]
    public async Task A_cancelled_request_throws_OperationCanceledException()
    {
        var (cache, _, main) = Create();
        using var _ = cache;
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => cache.CompleteAsync(main, "SELECT * FROM ", 1, 15, cts.Token));
    }

    [SkippableFact]
    public async Task The_tool_validates_arguments_and_wraps_the_completion_list()
    {
        var (cache, registry, main) = Create();
        using var _ = cache;
        var tools = new ScriptRunnerTools(new SqlConnectionFactory(registry), NullLogger<ScriptRunnerTools>.Instance, cache);

        using (CurrentConnection.Use(main))
        {
            var bad = await tools.LanguageService("format", "SELECT 1", 1, 1);
            Assert.False(bad.Success);
            Assert.Contains("action must be", bad.Error);

            var badPos = await tools.LanguageService("completion", "SELECT 1", 0, 1);
            Assert.False(badPos.Success);

            var ok = await tools.LanguageService("completion", "SELECT t. FROM dbo.T t", 1, 10);
            Assert.True(ok.Success, ok.Error);
            var list = Assert.IsType<CompletionList>(ok.Data);
            Assert.Contains(list.Items, i => i.Label == "a");
        }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
