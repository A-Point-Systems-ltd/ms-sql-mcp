using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Babel;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.SqlServer.Management.SqlParser.Intellisense;
using Mssql.McpServer.Connections;

namespace Mssql.McpServer.LanguageService;

/// <summary>
/// SqlParser IntelliSense for the extension's language_service tool. Keeps one SMO metadata provider per
/// (connection name, database, connection-string generation), built lazily in the background and shared by every
/// document. Each entry has one lock, so SqlParser (not thread-safe) only ever runs one operation per entry at a time:
/// the idea of sqltoolsservice's binding queue, written for this server. A request waits for binding at most
/// <see cref="CompletionTimeout"/>; after that completion falls back to keywords (<c>loading</c>) while the build
/// carries on. Entries are dropped on connection close/removal/replacement (<see cref="ConnectionRegistry.Changed"/>),
/// on refresh, and after <see cref="IdleTimeout"/> unused. Metadata access is read-only; logs carry the profile name,
/// counts and timings, never SQL text or connection strings.
/// </summary>
public sealed class LanguageServiceCache : IDisposable
{
    internal static readonly TimeSpan DefaultCompletionTimeout = TimeSpan.FromMilliseconds(2000);
    internal static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(30);

    /// <summary>A catalog snapshot (enhanced completions) older than this is read again in the background.</summary>
    internal static readonly TimeSpan CatalogTtl = TimeSpan.FromMinutes(10);

    /// <summary>How long <c>scope</c> waits for a catalog snapshot that is still loading.</summary>
    internal static readonly TimeSpan ScopeTimeout = TimeSpan.FromSeconds(15);

    // A failed build is retried after this long, so a broken connection is not reopened on every keystroke.
    private static readonly TimeSpan FailedRetryDelay = TimeSpan.FromSeconds(30);

    private readonly ConnectionRegistry _registry;
    private readonly ILogger<LanguageServiceCache> _logger;
    private readonly TimeProvider _time;
    private readonly ITimer _idleTimer;
    private readonly Lock _gate = new();
    private readonly ConcurrentDictionary<Key, Entry> _entries = new();
    private readonly ConcurrentDictionary<Key, CatalogEntry> _catalogs = new();
    private int _skippedOperations;

    public LanguageServiceCache(ConnectionRegistry registry, ILogger<LanguageServiceCache> logger, TimeProvider? time = null)
    {
        _registry = registry;
        _logger = logger;
        _time = time ?? TimeProvider.System;
        _registry.Changed += OnConnectionChanged;
        _idleTimer = TimeProvider.System.CreateTimer(_ => EvictIdle(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    /// <summary>How long a request waits for binding (including a cold build) before falling back.</summary>
    internal TimeSpan CompletionTimeout { get; set; } = DefaultCompletionTimeout;

    internal int EntryCount => _entries.Count;

    /// <summary>Queued operations skipped because their request had already returned (timeout or cancellation).</summary>
    internal int SkippedOperations => Volatile.Read(ref _skippedOperations);

    /// <summary>
    /// The dedicated SMO session's connection string: the profile's, unpooled, with <c>Persist Security Info=true</c>.
    /// That setting only matters for SQL-authentication passwords, in case SMO copies the string; it is defensive (on
    /// LocalDB, SMO reopens the same SqlConnection object and SQL auth works without it), and Entra ID is untested.
    /// It stays in process memory: never log it or put it in an error.
    /// </summary>
    internal static string MetadataConnectionString(ConnectionProfile profile) =>
        new SqlConnectionStringBuilder(profile.ConnectionString) { Pooling = false, PersistSecurityInfo = true }.ConnectionString;

    /// <summary>
    /// SQL error numbers a metadata build retries once: 596 (session in the kill state), 10053 / 10054 (connection
    /// aborted / reset), 64 (network name no longer available). 233 (no process on the other end of
    /// the pipe) is left out: the server also reports it when it rejects a login, and login failures are not retried.
    /// </summary>
    internal static readonly IReadOnlySet<int> TransientSqlErrors = new HashSet<int> { 596, 10053, 10054, 64 };

    /// <summary>
    /// The transient SQL error number found on <paramref name="ex"/> or anywhere in its InnerException chain (SMO wraps
    /// SqlException), or null when there is none. <paramref name="numberOf"/> reads an exception's SQL error number; the
    /// default reads SqlException.Number and every error in SqlException.Errors.
    /// </summary>
    internal static int? TransientSqlNumber(Exception ex, Func<Exception, int?>? numberOf = null)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (numberOf is not null)
            {
                if (numberOf(current) is { } n && TransientSqlErrors.Contains(n))
                {
                    return n;
                }
            }
            else if (current is SqlException sql)
            {
                if (TransientSqlErrors.Contains(sql.Number))
                {
                    return sql.Number;
                }

                foreach (SqlError error in sql.Errors)
                {
                    if (TransientSqlErrors.Contains(error.Number))
                    {
                        return error.Number;
                    }
                }
            }
        }

        return null;
    }

    /// <summary>Completion at a 1-based line and column.</summary>
    public Task<CompletionList> CompleteAsync(ConnectionProfile profile, string text, int line, int column, CancellationToken cancellationToken) =>
        CompleteAsync(profile, text, line, column, enhanced: false, cancellationToken);

    /// <summary>
    /// Completion at a 1-based line and column. <paramref name="enhanced"/> adds JOIN / ON suggestions, generated table
    /// aliases and the column-picker entry (the editor's Enhanced Completions switch).
    /// </summary>
    public async Task<CompletionList> CompleteAsync(ConnectionProfile profile, string text, int line, int column, bool enhanced, CancellationToken cancellationToken)
    {
        if (!enhanced)
        {
            return await CompleteBaseAsync(profile, text, line, column, cancellationToken).ConfigureAwait(false);
        }

        var completion = CompleteBaseAsync(profile, text, line, column, cancellationToken);
        SqlScopeInfo? scope = null;
        CatalogSnapshot? catalog = null;
        try
        {
            scope = SqlScope.Analyze(text, line, column);
            if (scope.Context is CaretContext.JoinTable or CaretContext.OnCondition)
            {
                catalog = await TryCatalogAsync(profile, CompletionTimeout, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug("language_service enhanced completion skipped: {Error}", ex.GetType().Name + ": " + ex.Message);
        }

        var list = await completion.ConfigureAwait(false);
        if (scope is null)
        {
            return list;
        }

        var extras = EnhancedCompletion.Create(scope, catalog);
        return list with { Items = [.. extras, .. EnhancedCompletion.WithAliases(list.Items, scope)] };
    }

    /// <summary>
    /// The table sources of the statement at the caret with their columns (column picker, * expansion). Waits up to
    /// <see cref="ScopeTimeout"/> for the catalog; <c>loading</c> with no columns when it is still not ready.
    /// </summary>
    public async Task<ScopeResult> ScopeAsync(ConnectionProfile profile, string text, int line, int column, CancellationToken cancellationToken)
    {
        var scope = SqlScope.Analyze(text, line, column);
        var catalog = await TryCatalogAsync(profile, ScopeTimeout, cancellationToken).ConfigureAwait(false);
        var tables = scope.Tables.Select(t =>
        {
            var o = t.IsVariable ? null : catalog?.Find(t.Schema, t.Name);
            return new ScopeTableInfo(t.Alias, o?.Schema ?? t.Schema, o?.Name ?? t.Name, o?.Kind, o?.Columns ?? []);
        }).ToList();
        return new ScopeResult(tables, catalog is null ? CacheStates.Loading : CacheStates.Warm);
    }

    /// <summary>The object <paramref name="name"/> resolves to on the profile's database (Go to Object Definition, Select Top Rows).</summary>
    public async Task<ObjectInfo> ObjectInfoAsync(ConnectionProfile profile, string name, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(profile.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await ObjectInfoReader.ReadAsync(connection, name, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The profile's catalog snapshot when it is ready within <paramref name="wait"/>, else null (it keeps loading).
    /// While an expired snapshot is read again, the previous one is served.
    /// </summary>
    internal async Task<CatalogSnapshot?> TryCatalogAsync(ConnectionProfile profile, TimeSpan wait, CancellationToken cancellationToken)
    {
        var entry = CatalogEntryFor(profile);
        if (entry is null)
        {
            return null;
        }

        if (entry.Load.IsCompletedSuccessfully)
        {
            return entry.Load.Result;
        }

        if (entry.Previous is { IsCompletedSuccessfully: true } previous)
        {
            return previous.Result;
        }

        try
        {
            return await entry.Load.WaitAsync(wait, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Timed out (still loading) or failed (logged by the load).
            return null;
        }
    }

    private CatalogEntry? CatalogEntryFor(ConnectionProfile profile)
    {
        var key = KeyOf(profile);
        lock (_gate)
        {
            if (!_registry.IsOpen(profile.Name) || _registry.Find(profile.Name) != profile)
            {
                return null;
            }

            var now = _time.GetUtcNow();
            if (_catalogs.TryGetValue(key, out var existing))
            {
                var age = now - existing.Created;
                var expired = existing.Load.IsCompletedSuccessfully && age > CatalogTtl;
                var retry = existing.Load.IsFaulted && age > FailedRetryDelay;
                if (!expired && !retry)
                {
                    existing.LastUsed = now;
                    return existing;
                }
            }

            var load = Task.Run(() => LoadCatalogAsync(profile));
            _ = load.ContinueWith(static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            var entry = new CatalogEntry(load, existing?.Load is { IsCompletedSuccessfully: true } old ? old : null, now);
            _catalogs[key] = entry;
            return entry;
        }
    }

    private async Task<CatalogSnapshot> LoadCatalogAsync(ConnectionProfile profile)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            await using var connection = new SqlConnection(profile.ConnectionString);
            await connection.OpenAsync(CancellationToken.None).ConfigureAwait(false);
            var snapshot = await CatalogSnapshot.LoadAsync(connection, CancellationToken.None).ConfigureAwait(false);
            _logger.LogDebug("language_service catalog for '{Connection}': {Objects} objects, {Keys} foreign keys in {Elapsed} ms",
                profile.Name, snapshot.ObjectCount, snapshot.ForeignKeyCount, watch.ElapsedMilliseconds);
            return snapshot;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("language_service could not read the catalog for '{Connection}': {Error}", profile.Name, ex.GetType().Name + ": " + ex.Message);
            throw;
        }
    }

    private async Task<CompletionList> CompleteBaseAsync(ConnectionProfile profile, string text, int line, int column, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var watch = Stopwatch.StartNew();
        try
        {
            var entry = GetOrStart(profile) ?? throw new StaleProfileException();
            var items = await RunAsync(entry, ctx => Complete(ctx, text, line, column), cancellationToken).ConfigureAwait(false);
            _logger.LogDebug("language_service completion: {Count} items in {Elapsed} ms", items.Count, watch.ElapsedMilliseconds);
            return new CompletionList(items, IsIncomplete: false, CacheStates.Warm);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (ex is not (TimeoutException or StaleProfileException))
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
        RunOrNullAsync(
            profile,
            ctx =>
            {
                var parsed = ctx.ParseAndBind(text);
                CodeObjectQuickInfo? quickInfo;
                try
                {
                    quickInfo = Resolver.GetQuickInfo(parsed, line, column, ctx.DisplayInfoProvider);
                }
                catch (NullReferenceException)
                {
                    LogParserFault(ctx, "hover");
                    return null;
                }

                return CompletionConverter.ToHover(quickInfo);
            },
            cancellationToken);

    /// <summary>Signature help at a 1-based line and column; null when the caret is in no call or binding is not ready.</summary>
    public Task<SignatureHelpInfo?> SignatureHelpAsync(ConnectionProfile profile, string text, int line, int column, CancellationToken cancellationToken) =>
        RunOrNullAsync(
            profile,
            ctx =>
            {
                var parsed = ctx.ParseAndBind(text);
                List<MethodHelpText>? methods;
                MethodNameAndParamLocations? locations;
                try
                {
                    methods = Resolver.FindMethods(parsed, line, column, ctx.DisplayInfoProvider);
                    locations = Resolver.GetMethodNameAndParams(parsed, line, column, ctx.DisplayInfoProvider);
                }
                catch (NullReferenceException)
                {
                    LogParserFault(ctx, "signatureHelp");
                    return null;
                }

                return CompletionConverter.ToSignatureHelp(methods, locations, line, column);
            },
            cancellationToken);

    /// <summary>
    /// SqlParser 180.9.0 throws a NullReferenceException inside its Resolver for some scalar UDF calls
    /// (SqlScalarFunctionCallExpression.GetMyMethodHelpText). Known and harmless for the binder: the request returns
    /// nothing, logged once per cache entry at Debug instead of a Warning per keystroke.
    /// </summary>
    private void LogParserFault(BindingContext ctx, string action)
    {
        if (ctx.FirstParserFault())
        {
            _logger.LogDebug("language_service {Action}: SqlParser has no result at this position (NullReferenceException); further ones are not logged", action);
        }
    }

    /// <summary>Starts building the entry in the background if needed; returns at once with the current state.</summary>
    public string Warm(ConnectionProfile profile) => StateOf(GetOrStart(profile));

    /// <summary>Drops the entry for the profile's connection and database, then starts building it again.</summary>
    public string Refresh(ConnectionProfile profile)
    {
        Remove(KeyOf(profile));
        _ = _catalogs.TryRemove(KeyOf(profile), out _);
        return StateOf(GetOrStart(profile));
    }

    /// <summary>Drops entries unused for <see cref="IdleTimeout"/>. Runs every minute; internal for tests.</summary>
    internal void EvictIdle()
    {
        var now = _time.GetUtcNow();
        foreach (var pair in _catalogs)
        {
            if (now - pair.Value.LastUsed > IdleTimeout)
            {
                _ = _catalogs.TryRemove(pair);
            }
        }

        foreach (var pair in _entries)
        {
            if (now - pair.Value.LastUsed <= IdleTimeout)
            {
                continue;
            }

            // Remove only the exact entry checked: a fresh one published under the same key in between stays.
            bool removed;
            lock (_gate)
            {
                removed = _entries.TryRemove(pair);
            }

            if (removed)
            {
                _ = DisposeEntryAsync(pair.Value);
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

        _catalogs.Clear();
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
        var items = ProcedureParameterCompletion.Create(text, line, column, () => Resolver.FindMethods(parsed, line, column, ctx.DisplayInfoProvider));
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
            var entry = GetOrStart(profile);
            return entry is null ? null : await RunAsync(entry, operation, cancellationToken).ConfigureAwait(false);
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
    /// When the wait ends first (timeout or cancellation) the request is marked abandoned: its queued operation is skipped
    /// when it reaches the lock, while the metadata build itself (a separate task) carries on.
    /// </summary>
    private async Task<T> RunAsync<T>(Entry entry, Func<BindingContext, T> operation, CancellationToken cancellationToken)
    {
        var request = new RequestState();
        var work = Task.Run(
            async () =>
            {
                var ctx = await entry.Ready.ConfigureAwait(false);
                if (request.Abandoned || cancellationToken.IsCancellationRequested)
                {
                    throw Skip();
                }

                try
                {
                    await entry.Lock.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw Skip();
                }

                try
                {
                    if (request.Abandoned || cancellationToken.IsCancellationRequested)
                    {
                        throw Skip();
                    }

                    ObjectDisposedException.ThrowIf(entry.Disposed, typeof(BindingContext));
                    return operation(ctx);
                }
                finally
                {
                    entry.Lock.Release();
                }
            },
            CancellationToken.None);

        // Abandoned work must not raise unobserved task exceptions.
        _ = work.ContinueWith(static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        try
        {
            return await work.WaitAsync(CompletionTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            request.Abandoned = true;
            throw;
        }
    }

    private OperationCanceledException Skip()
    {
        _ = Interlocked.Increment(ref _skippedOperations);
        return new OperationCanceledException("language_service request already returned.");
    }

    /// <summary>
    /// The entry for the profile, started if needed; null when the profile is stale (closed, removed or replaced since
    /// the request resolved it), so an old request can never publish an entry for a connection that changed.
    /// </summary>
    private Entry? GetOrStart(ConnectionProfile profile)
    {
        var key = KeyOf(profile);
        Entry? stale = null;
        Entry entry;
        lock (_gate)
        {
            // Checked under the gate that OnConnectionChanged also takes, so a close cannot slip in between check and publish.
            if (!_registry.IsOpen(profile.Name) || _registry.Find(profile.Name) != profile)
            {
                return null;
            }

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
        for (var attempt = 1; ; attempt++)
        {
            var connection = new SqlConnection(MetadataConnectionString(profile));
            try
            {
                // Outside the pool. SMO closes and reopens this SqlConnection as it needs (observed: no open session between
                // requests); Persist Security Info keeps the credentials for that and for any copy SMO makes of the string.
                await connection.OpenAsync(CancellationToken.None).ConfigureAwait(false);
                var context = BindingContext.Create(connection);
                _logger.LogDebug("language_service metadata provider for '{Connection}' built in {Elapsed} ms", profile.Name, watch.ElapsedMilliseconds);
                return context;
            }
            catch (Exception ex) when (attempt == 1 && TransientSqlNumber(ex) is { } number)
            {
                // A killed session (596, for example by an ALTER DATABASE ... ROLLBACK IMMEDIATE elsewhere on the instance
                // while SMO reads catalog data) or a dropped transport: one immediate retry. Login, permission and
                // database errors are never retried.
                await connection.DisposeAsync().ConfigureAwait(false);
                _logger.LogWarning("language_service metadata build for '{Connection}' hit transient SQL error {Number}; retrying once", profile.Name, number);
            }
            catch (Exception ex)
            {
                await connection.DisposeAsync().ConfigureAwait(false);

                // SqlException messages name the server and login at most, never the password; the string itself is never logged.
                _logger.LogWarning("language_service could not build metadata for '{Connection}': {Error}", profile.Name, ex.GetType().Name + ": " + ex.Message);
                throw;
            }
        }
    });

    private void OnConnectionChanged(object? sender, ConnectionChangedEventArgs e)
    {
        List<Entry> removed = [];
        lock (_gate)
        {
            foreach (var key in _entries.Keys.Where(k => string.Equals(k.Connection, e.Name, StringComparison.OrdinalIgnoreCase)))
            {
                if (_entries.TryRemove(key, out var entry))
                {
                    removed.Add(entry);
                }
            }

            foreach (var key in _catalogs.Keys.Where(k => string.Equals(k.Connection, e.Name, StringComparison.OrdinalIgnoreCase)))
            {
                _ = _catalogs.TryRemove(key, out _);
            }
        }

        foreach (var entry in removed)
        {
            _ = DisposeEntryAsync(entry);
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

    private static string StateOf(Entry? entry) => entry?.Ready.IsCompletedSuccessfully == true ? CacheStates.Warm : CacheStates.Loading;

    /// <summary>
    /// The profile's database is its Initial Catalog (empty: the login's default database); a leading USE in the text is
    /// not followed. The generation is a hash of the connection string, kept in memory only, so a re-registered profile
    /// with another string never shares an entry with the old one.
    /// </summary>
    private static Key KeyOf(ConnectionProfile profile)
    {
        string database;
        try
        {
            database = new SqlConnectionStringBuilder(profile.ConnectionString).InitialCatalog ?? string.Empty;
        }
        catch (ArgumentException)
        {
            database = string.Empty;
        }

        var generation = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(profile.ConnectionString)), 0, 16);
        return new Key(profile.Name.ToUpperInvariant(), database.ToUpperInvariant(), generation);
    }

    private readonly record struct Key(string Connection, string Database, string Generation)
    {
        // Never print the generation hash.
        public override string ToString() => $"{Connection}/{Database}";
    }

    private sealed class RequestState
    {
        private volatile bool _abandoned;

        public bool Abandoned
        {
            get => _abandoned;
            set => _abandoned = value;
        }
    }

    /// <summary>A catalog snapshot load; <see cref="Previous"/> is the expired snapshot served while it runs.</summary>
    private sealed class CatalogEntry(Task<CatalogSnapshot> load, Task<CatalogSnapshot>? previous, DateTimeOffset created)
    {
        private long _lastUsedTicks = created.UtcTicks;

        public Task<CatalogSnapshot> Load { get; } = load;

        public Task<CatalogSnapshot>? Previous { get; } = previous;

        public DateTimeOffset Created { get; } = created;

        public DateTimeOffset LastUsed
        {
            get => new(Interlocked.Read(ref _lastUsedTicks), TimeSpan.Zero);
            set => Interlocked.Exchange(ref _lastUsedTicks, value.UtcTicks);
        }
    }

    private sealed class StaleProfileException() : InvalidOperationException("The connection changed after this request resolved it.");

    private sealed class Entry(Task<BindingContext> ready, DateTimeOffset created)
    {
        private long _lastUsedTicks = created.UtcTicks;

        public Task<BindingContext> Ready { get; } = ready;

        public DateTimeOffset Created { get; } = created;

        public SemaphoreSlim Lock { get; } = new(1, 1);

        public DateTimeOffset LastUsed
        {
            get => new(Interlocked.Read(ref _lastUsedTicks), TimeSpan.Zero);
            set => Interlocked.Exchange(ref _lastUsedTicks, value.UtcTicks);
        }

        public bool Disposed { get; set; }
    }
}
