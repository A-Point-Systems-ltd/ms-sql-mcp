using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.SqlServer.Management.SqlParser.Intellisense;
using Mssql.McpServer.Connections;

namespace Mssql.McpServer.LanguageService;

/// <summary>
/// SqlParser IntelliSense for the extension's language_service tool. Keeps one SMO metadata provider per
/// (connection name, database), built lazily in the background and shared by every document. Each entry has one lock,
/// so SqlParser (not thread-safe) only ever runs one operation per entry at a time: the idea of sqltoolsservice's
/// binding queue, written for this server. A request waits for binding at most <see cref="CompletionTimeout"/>; after
/// that completion falls back to keywords (<c>loading</c>) while the build carries on.
/// Entries are dropped on connection close/removal (<see cref="ConnectionRegistry.Changed"/>), on refresh, and after
/// <see cref="IdleTimeout"/> unused. Metadata access is read-only; logs carry counts and timings, never SQL text.
/// </summary>
public sealed class LanguageServiceCache : IDisposable
{
    internal static readonly TimeSpan DefaultCompletionTimeout = TimeSpan.FromMilliseconds(2000);
    internal static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(30);

    // A failed build is retried after this long, so a broken connection is not reopened on every keystroke.
    private static readonly TimeSpan FailedRetryDelay = TimeSpan.FromSeconds(30);

    private readonly ISqlConnectionFactory _connectionFactory;
    private readonly ConnectionRegistry _registry;
    private readonly ILogger<LanguageServiceCache> _logger;
    private readonly TimeProvider _time;
    private readonly ITimer _idleTimer;
    private readonly Lock _gate = new();
    private readonly ConcurrentDictionary<Key, Entry> _entries = new();

    public LanguageServiceCache(
        ISqlConnectionFactory connectionFactory, ConnectionRegistry registry, ILogger<LanguageServiceCache> logger, TimeProvider? time = null)
    {
        _connectionFactory = connectionFactory;
        _registry = registry;
        _logger = logger;
        _time = time ?? TimeProvider.System;
        _registry.Changed += OnConnectionChanged;
        _idleTimer = TimeProvider.System.CreateTimer(_ => EvictIdle(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    /// <summary>How long a request waits for binding (including a cold build) before falling back.</summary>
    internal TimeSpan CompletionTimeout { get; set; } = DefaultCompletionTimeout;

    internal int EntryCount => _entries.Count;

    /// <summary>Completion at a 1-based line and column.</summary>
    public async Task<CompletionList> CompleteAsync(ConnectionProfile profile, string text, int line, int column, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var entry = GetOrStart(profile);
        var watch = Stopwatch.StartNew();
        try
        {
            var items = await RunAsync(entry, ctx => Complete(ctx, text, line, column), cancellationToken).ConfigureAwait(false);
            _logger.LogDebug("language_service completion: {Count} items in {Elapsed} ms", items.Count, watch.ElapsedMilliseconds);
            return new CompletionList(items, IsIncomplete: false, CacheStates.Warm);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (ex is not TimeoutException)
            {
                _logger.LogWarning("language_service completion fell back to keywords: {Error}", ex.GetType().Name + ": " + ex.Message);
            }

            var keywords = CompletionConverter.Keywords(WordBefore(text, line, column));
            _logger.LogDebug("language_service completion: binding not ready, {Count} keywords in {Elapsed} ms", keywords.Count, watch.ElapsedMilliseconds);
            return new CompletionList(keywords, IsIncomplete: true, CacheStates.Loading);
        }
    }

    /// <summary>Quick info at a 1-based line and column; null when there is none or binding is not ready.</summary>
    public Task<HoverInfo?> HoverAsync(ConnectionProfile profile, string text, int line, int column, CancellationToken cancellationToken) =>
        RunOrNullAsync(profile, ctx => CompletionConverter.ToHover(Resolver.GetQuickInfo(ctx.ParseAndBind(text), line, column, ctx.DisplayInfoProvider)), cancellationToken);

    /// <summary>Signature help at a 1-based line and column; null when the caret is in no call or binding is not ready.</summary>
    public Task<SignatureHelpInfo?> SignatureHelpAsync(ConnectionProfile profile, string text, int line, int column, CancellationToken cancellationToken) =>
        RunOrNullAsync(
            profile,
            ctx =>
            {
                var parsed = ctx.ParseAndBind(text);
                var methods = Resolver.FindMethods(parsed, line, column, ctx.DisplayInfoProvider);
                var locations = Resolver.GetMethodNameAndParams(parsed, line, column, ctx.DisplayInfoProvider);
                return CompletionConverter.ToSignatureHelp(methods, locations, line, column);
            },
            cancellationToken);

    /// <summary>Starts building the entry in the background if needed; returns at once with the current state.</summary>
    public string Warm(ConnectionProfile profile) => StateOf(GetOrStart(profile));

    /// <summary>Drops the entry for the profile's connection and database, then starts building it again.</summary>
    public string Refresh(ConnectionProfile profile)
    {
        Remove(KeyOf(profile));
        return StateOf(GetOrStart(profile));
    }

    /// <summary>Drops entries unused for <see cref="IdleTimeout"/>. Runs every minute; internal for tests.</summary>
    internal void EvictIdle()
    {
        var now = _time.GetUtcNow();
        foreach (var (key, entry) in _entries)
        {
            if (now - entry.LastUsed > IdleTimeout)
            {
                Remove(key);
            }
        }
    }

    public void Dispose()
    {
        _registry.Changed -= OnConnectionChanged;
        _idleTimer.Dispose();
        foreach (var key in _entries.Keys)
        {
            Remove(key);
        }
    }

    private static List<CompletionItemInfo> Complete(BindingContext ctx, string text, int line, int column)
    {
        var parsed = ctx.ParseAndBind(text);
        var token = CompletionConverter.TokenAt(parsed, line, column);
        if (CompletionConverter.IsComment(token))
        {
            return [];
        }

        var tokenText = token?.Text;
        var items = ProcedureParameterCompletion.Create(text, line, column, Resolver.FindMethods(parsed, line, column, ctx.DisplayInfoProvider));
        var seen = new HashSet<(string, string)>(items.Select(i => (i.Label, i.Kind)));
        foreach (var declaration in Resolver.FindCompletions(parsed, line, column, ctx.DisplayInfoProvider) ?? [])
        {
            if (string.IsNullOrEmpty(declaration.Title))
            {
                continue;
            }

            var item = CompletionConverter.FromDeclaration(declaration, tokenText);
            if (seen.Add((item.Label, item.Kind)))
            {
                items.Add(item);
            }
        }

        // Like sqltoolsservice: no parser completions means the default keyword list.
        return items.Count > 0 ? items : CompletionConverter.Keywords(WordBefore(text, line, column));
    }

    /// <summary>The identifier characters just before the caret (the word being typed), or null.</summary>
    private static string? WordBefore(string text, int line, int column)
    {
        var lines = text.Split('\n');
        if (line < 1 || line > lines.Length)
        {
            return null;
        }

        var current = lines[line - 1].TrimEnd('\r');
        var end = Math.Min(column - 1, current.Length);
        var start = end;
        while (start > 0 && (char.IsLetterOrDigit(current[start - 1]) || current[start - 1] is '_' or '@' or '#'))
        {
            start--;
        }

        return start < end ? current[start..end] : null;
    }

    private async Task<T?> RunOrNullAsync<T>(ConnectionProfile profile, Func<BindingContext, T?> operation, CancellationToken cancellationToken)
        where T : class
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return await RunAsync(GetOrStart(profile), operation, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (ex is not TimeoutException)
            {
                _logger.LogWarning("language_service request failed: {Error}", ex.GetType().Name + ": " + ex.Message);
            }

            return null;
        }
    }

    /// <summary>
    /// Runs <paramref name="operation"/> on the entry's context under its lock, waiting at most <see cref="CompletionTimeout"/>.
    /// On timeout the work keeps running (it finishes the build and warms SMO's lazy metadata) and a TimeoutException is
    /// thrown. On cancellation the wait ends and work that has not started yet is skipped.
    /// </summary>
    private async Task<T> RunAsync<T>(Entry entry, Func<BindingContext, T> operation, CancellationToken cancellationToken)
    {
        var work = Task.Run(
            async () =>
            {
                var ctx = await entry.Ready.ConfigureAwait(false);
                await entry.Lock.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    ObjectDisposedException.ThrowIf(entry.Disposed, typeof(BindingContext));
                    cancellationToken.ThrowIfCancellationRequested();
                    return operation(ctx);
                }
                finally
                {
                    entry.Lock.Release();
                }
            },
            CancellationToken.None);

        // Abandoned work (timeout or cancellation) must not raise unobserved task exceptions.
        _ = work.ContinueWith(static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        return await work.WaitAsync(CompletionTimeout, cancellationToken).ConfigureAwait(false);
    }

    private Entry GetOrStart(ConnectionProfile profile)
    {
        var key = KeyOf(profile);
        Entry? stale = null;
        Entry entry;
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out var existing)
                || (existing.Ready.IsFaulted && _time.GetUtcNow() - existing.Created > FailedRetryDelay))
            {
                stale = existing;
                existing = new Entry(StartBuild(profile), _time.GetUtcNow());
                _ = existing.Ready.ContinueWith(static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
                _entries[key] = existing;
            }

            entry = existing;
        }

        if (stale is not null)
        {
            _ = DisposeEntryAsync(stale);
        }

        entry.LastUsed = _time.GetUtcNow();
        return entry;
    }

    private Task<BindingContext> StartBuild(ConnectionProfile profile) => Task.Run(async () =>
    {
        var watch = Stopwatch.StartNew();
        Microsoft.Data.SqlClient.SqlConnection connection;

        // A dedicated session (outside the pool): SMO keeps it for the entry's lifetime.
        using (CurrentConnection.Use(profile))
        {
            connection = await _connectionFactory.GetOpenUnpooledConnectionAsync(CancellationToken.None).ConfigureAwait(false);
        }

        try
        {
            var context = BindingContext.Create(connection);
            _logger.LogDebug("language_service metadata provider for '{Connection}' built in {Elapsed} ms", profile.Name, watch.ElapsedMilliseconds);
            return context;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("language_service could not build metadata for '{Connection}': {Error}", profile.Name, ex.GetType().Name + ": " + ex.Message);
            throw;
        }
    });

    private void OnConnectionChanged(object? sender, ConnectionChangedEventArgs e)
    {
        foreach (var key in _entries.Keys.Where(k => string.Equals(k.Connection, e.Name, StringComparison.OrdinalIgnoreCase)))
        {
            Remove(key);
        }
    }

    private void Remove(Key key)
    {
        Entry? removed;
        lock (_gate)
        {
            _ = _entries.TryRemove(key, out removed);
        }

        if (removed is not null)
        {
            _ = DisposeEntryAsync(removed);
        }
    }

    /// <summary>Disposes the context once its build and any running operation are done; later operations see Disposed.</summary>
    private static async Task DisposeEntryAsync(Entry entry)
    {
        BindingContext context;
        try
        {
            context = await entry.Ready.ConfigureAwait(false);
        }
        catch (Exception)
        {
            return;
        }

        await entry.Lock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!entry.Disposed)
            {
                entry.Disposed = true;
                context.Dispose();
            }
        }
        finally
        {
            entry.Lock.Release();
        }
    }

    private static string StateOf(Entry entry) => entry.Ready.IsCompletedSuccessfully ? CacheStates.Warm : CacheStates.Loading;

    /// <summary>The profile's database is its Initial Catalog (empty: the login's default database). A leading USE in the text is not followed.</summary>
    private static Key KeyOf(ConnectionProfile profile)
    {
        string database;
        try
        {
            database = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(profile.ConnectionString).InitialCatalog ?? string.Empty;
        }
        catch (ArgumentException)
        {
            database = string.Empty;
        }

        return new Key(profile.Name.ToUpperInvariant(), database.ToUpperInvariant());
    }

    private readonly record struct Key(string Connection, string Database);

    private sealed class Entry(Task<BindingContext> ready, DateTimeOffset created)
    {
        public Task<BindingContext> Ready { get; } = ready;

        public DateTimeOffset Created { get; } = created;

        public SemaphoreSlim Lock { get; } = new(1, 1);

        private long _lastUsedTicks = created.UtcTicks;

        public DateTimeOffset LastUsed
        {
            get => new(Interlocked.Read(ref _lastUsedTicks), TimeSpan.Zero);
            set => Interlocked.Exchange(ref _lastUsedTicks, value.UtcTicks);
        }

        public bool Disposed { get; set; }
    }
}
