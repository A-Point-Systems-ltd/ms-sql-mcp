using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace Mssql.McpServer.Connections.Managed;

/// <summary>A managed entry as the connections view sees it. Never carries a password or a connection string with one.</summary>
public sealed record ManagedConnectionView(
    string Name, string Auth, string Server, string Database, string? User, bool HasPassword,
    string Encrypt, bool TrustServerCertificate, bool ReadOnly, bool Insights, string? RawConnectionString,
    bool IsOpen, string? Error);

/// <summary>A connection from another source (VS Code extension file, env): shown, not editable here.</summary>
public sealed record OtherConnectionView(string Name, string Source, string DataSource, string? Database, bool ReadOnly, bool IsOpen);

public sealed record ManagedConnectionList(
    IReadOnlyList<ManagedConnectionView> Managed, IReadOnlyList<OtherConnectionView> Others, string? FileError);

public sealed record ManagedSaveResult(bool Success, IReadOnlyDictionary<string, string> Errors, string? Message = null);

/// <param name="Databases">List databases: every database of the server with its state.</param>
/// <param name="DatabaseState">Test: the form's database state when the connection failed and it is not ONLINE (e.g. OFFLINE).</param>
public sealed record ManagedProbeResult(
    bool Success, string Message, IReadOnlyList<Connections.DatabaseState>? Databases = null, string? DatabaseState = null);

/// <summary>
/// Keeps the <see cref="ConnectionRegistry"/>'s <see cref="ConnectionSource.Managed"/> profiles equal to the managed
/// connections file, and implements the connections view's add / edit / remove / test. Other processes may edit the
/// file too, so <see cref="EnsureCurrent"/> re-syncs whenever the file's write time changes.
/// </summary>
public sealed class ManagedConnectionService(
    ManagedConnectionStore store, ConnectionRegistry registry, ISecretProtector protector, ILogger<ManagedConnectionService> logger)
{
    /// <summary>Probe timeout; Entra interactive waits for a browser sign-in, so it gets longer.</summary>
    internal const int ProbeTimeoutSeconds = 15;
    internal const int InteractiveProbeTimeoutSeconds = 120;

    private readonly Lock _syncGate = new();
    private DateTime? _syncedStamp;
    private bool _synced;
    private string? _fileError;
    private Dictionary<string, string> _broken = new(StringComparer.OrdinalIgnoreCase);

    public string FilePath => store.Path;

    /// <summary>Re-syncs when the file changed since the last sync (cheap: one file stat).</summary>
    public void EnsureCurrent()
    {
        var stamp = store.LastWriteUtc;
        lock (_syncGate)
        {
            if (_synced && stamp == _syncedStamp)
            {
                return;
            }
        }

        Sync();
    }

    /// <summary>Loads the file and makes the registry's managed profiles match it.</summary>
    public void Sync()
    {
        lock (_syncGate)
        {
            var stamp = store.LastWriteUtc;
            IReadOnlyList<ManagedConnection> entries;
            try
            {
                entries = store.Load();
                _fileError = null;
            }
            catch (ManagedConnectionFileException ex)
            {
                // Keep what is registered: a half-written or hand-broken file must not drop working connections.
                _fileError = ex.Message;
                _syncedStamp = stamp;
                _synced = true;
                logger.LogWarning("Managed connections file {Path}: {Error}", store.Path, ex.Message);
                return;
            }

            var broken = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in entries)
            {
                if (!wanted.Add(entry.Name))
                {
                    broken[entry.Name] = "Duplicate name in the managed connections file.";
                    continue;
                }

                var problem = Problem(entry, out var connectionString);
                if (problem is null && !registry.UpsertManaged(new ConnectionProfile(entry.Name, connectionString!, entry.ReadOnly, entry.Insights, ConnectionSource.Managed)))
                {
                    problem = "The name is already used by a connection configured elsewhere (VS Code extension or environment).";
                }

                if (problem is not null)
                {
                    broken[entry.Name] = problem;
                    _ = registry.RemoveManaged(entry.Name);
                }
            }

            foreach (var name in registry.ManagedNames().Where(n => !wanted.Contains(n)))
            {
                _ = registry.RemoveManaged(name);
            }

            _broken = broken;
            _syncedStamp = stamp;
            _synced = true;
        }
    }

    public ManagedConnectionList List()
    {
        EnsureCurrent();
        IReadOnlyList<ManagedConnection> entries;
        string? fileError;
        Dictionary<string, string> broken;
        lock (_syncGate)
        {
            fileError = _fileError;
            broken = _broken;
        }

        try
        {
            entries = store.Load();
        }
        catch (ManagedConnectionFileException ex)
        {
            entries = [];
            fileError = ex.Message;
        }

        var statuses = registry.List();
        var managed = entries
            .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .Select(e => new ManagedConnectionView(
                e.Name, e.Auth, e.Server, e.Database, e.User, !string.IsNullOrEmpty(e.PasswordProtected),
                e.Encrypt, e.TrustServerCertificate, e.ReadOnly, e.Insights, e.RawConnectionString,
                statuses.Any(s => s.Source == ConnectionSource.Managed && s.IsOpen && string.Equals(s.Name, e.Name, StringComparison.OrdinalIgnoreCase)),
                broken.TryGetValue(e.Name, out var err) ? err : null))
            .ToList();
        var others = statuses
            .Where(s => s.Source != ConnectionSource.Managed)
            .Select(s => new OtherConnectionView(s.Name, s.Source.ToString(), s.DataSource, s.Database, s.ReadOnly, s.IsOpen))
            .ToList();
        return new ManagedConnectionList(managed, others, fileError);
    }

    /// <summary>Adds (<paramref name="isNew"/>) or edits a managed connection, then applies it to the registry.</summary>
    public ManagedSaveResult Save(ManagedConnectionInput input, bool isNew)
    {
        EnsureCurrent();
        input = input with { Name = input.Name?.Trim() ?? "" };
        var result = store.Mutate(entries =>
        {
            var index = entries.FindIndex(e => string.Equals(e.Name, input.Name, StringComparison.OrdinalIgnoreCase));
            var existing = index >= 0 ? entries[index] : null;
            var hasSavedPassword = existing is { Auth: ManagedAuth.Sql, PasswordProtected: { Length: > 0 } };
            var errors = ManagedConnectionRules.Validate(input, hasSavedPassword: !isNew && hasSavedPassword);
            if (isNew && !errors.ContainsKey("name") && (existing is not null || registry.Find(input.Name) is not null))
            {
                errors["name"] = "A connection with this name already exists.";
            }

            if (!isNew && existing is null)
            {
                errors["name"] = "This connection no longer exists (it may have been removed in another window).";
            }

            if (errors.Count > 0)
            {
                return (false, new ManagedSaveResult(false, errors));
            }

            string? passwordProtected = null;
            if (input.Auth == ManagedAuth.Sql)
            {
                passwordProtected = string.IsNullOrEmpty(input.Password) ? existing!.PasswordProtected : protector.Protect(input.Password);
            }

            var entry = ManagedConnectionRules.ToEntry(input, passwordProtected) with { Name = existing?.Name ?? input.Name };
            if (existing is null)
            {
                entries.Add(entry);
            }
            else
            {
                entries[index] = entry;
            }

            return (true, new ManagedSaveResult(true, new Dictionary<string, string>()));
        });

        if (result.Success)
        {
            Sync();
            logger.LogInformation("Managed connection '{Name}' {Action}.", input.Name, isNew ? "added" : "updated");
        }

        return result;
    }

    public ManagedSaveResult Remove(string name)
    {
        EnsureCurrent();
        var removed = store.Mutate(entries =>
        {
            var n = entries.RemoveAll(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
            return (n > 0, n > 0);
        });

        if (!removed)
        {
            return new ManagedSaveResult(false, new Dictionary<string, string>(), $"'{name}' is not a managed connection.");
        }

        Sync();
        logger.LogInformation("Managed connection '{Name}' removed.", name);
        return new ManagedSaveResult(true, new Dictionary<string, string>());
    }

    /// <summary>
    /// Connects with the form's values without saving. An empty password on an edit uses the saved one. When the
    /// connection fails because the form's database is not ONLINE, the result carries that state so the view can
    /// offer to bring it online.
    /// </summary>
    public async Task<ManagedProbeResult> TestAsync(ManagedConnectionInput input, bool isNew, CancellationToken ct)
    {
        var (result, databaseUnavailable) = await ProbeCoreAsync(input, isNew, listDatabases: false, ct).ConfigureAwait(false);
        // Only a "cannot open database" failure is worth a state lookup; after a failed login (wrong password) a second
        // login to master would just count twice toward a lockout policy.
        if (result.Success || !databaseUnavailable || ProbeConnectionString(input, isNew, out _) is not { } cs)
        {
            return result;
        }

        // From the built string, so a raw connection string's Initial Catalog counts too.
        var database = new SqlConnectionStringBuilder(cs).InitialCatalog;
        if (string.IsNullOrWhiteSpace(database))
        {
            return result;
        }

        try
        {
            var state = await DatabaseStateOps.GetStateAsync(cs, database, ct).ConfigureAwait(false);
            return state is not null && !string.Equals(state, Connections.DatabaseState.Online, StringComparison.OrdinalIgnoreCase)
                ? result with { Message = $"Database '{database}' is {state}. {result.Message}", DatabaseState = state }
                : result;
        }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException)
        {
            return result; // master is unreachable too: the original error says why
        }
    }

    /// <summary>Lists every database of the form's server with its state (connects to master).</summary>
    public Task<ManagedProbeResult> ListDatabasesAsync(ManagedConnectionInput input, bool isNew, CancellationToken ct) =>
        ProbeAsync(input, isNew, listDatabases: true, ct);

    /// <summary>
    /// ALTER DATABASE ... SET ONLINE for the form's database when it is OFFLINE. Human-initiated only (app-only tool,
    /// after an explicit confirmation in the view); allowed on read-only connections, whose flag restricts the model.
    /// </summary>
    public async Task<ManagedProbeResult> BringOnlineAsync(ManagedConnectionInput input, bool isNew, CancellationToken ct)
    {
        if (ProbeConnectionString(input, isNew, out var error) is not { } cs)
        {
            return new ManagedProbeResult(false, error!);
        }

        try
        {
            var database = new SqlConnectionStringBuilder(cs).InitialCatalog;
            var r = await DatabaseStateOps.BringOnlineAsync(cs, database, logger, ct).ConfigureAwait(false);
            return new ManagedProbeResult(r.Success, r.Message, DatabaseState: r.State);
        }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException)
        {
            return new ManagedProbeResult(false, ex.Message);
        }
    }

    /// <summary>
    /// The probe connection string for the form's values (password from the form or, on an edit, the saved one),
    /// or null with the validation error.
    /// </summary>
    private string? ProbeConnectionString(ManagedConnectionInput input, bool isNew, out string? error, bool listDatabases = false)
    {
        // The name is irrelevant to a probe; the database is not needed to list databases.
        var probeInput = input with { Name = "probe", Database = listDatabases && string.IsNullOrWhiteSpace(input.Database) ? "master" : input.Database };
        string? savedPassword = null;
        if (!isNew && input.Auth == ManagedAuth.Sql && string.IsNullOrEmpty(input.Password))
        {
            var saved = SafeLoad().FirstOrDefault(e => string.Equals(e.Name, input.Name?.Trim(), StringComparison.OrdinalIgnoreCase));
            savedPassword = saved?.PasswordProtected is { Length: > 0 } blob ? protector.TryUnprotect(blob) : null;
        }

        var errors = ManagedConnectionRules.Validate(probeInput, hasSavedPassword: savedPassword is not null);
        if (errors.Count > 0)
        {
            error = string.Join(" ", errors.Values);
            return null;
        }

        error = null;
        var entry = ManagedConnectionRules.ToEntry(probeInput, passwordProtected: null);
        return new SqlConnectionStringBuilder(ManagedConnectionRules.BuildConnectionString(
            entry, string.IsNullOrEmpty(input.Password) ? savedPassword : input.Password, listDatabases ? "master" : null))
        {
            ConnectTimeout = entry.Auth == ManagedAuth.EntraInteractive ? InteractiveProbeTimeoutSeconds : ProbeTimeoutSeconds,
            // One-off connections. A pool would also replay a failed open's error for seconds (blocking period), so
            // Test right after Bring online would still report the database as unavailable.
            Pooling = false,
        }.ConnectionString;
    }

    private async Task<ManagedProbeResult> ProbeAsync(ManagedConnectionInput input, bool isNew, bool listDatabases, CancellationToken ct) =>
        (await ProbeCoreAsync(input, isNew, listDatabases, ct).ConfigureAwait(false)).Result;

    /// <returns>The result, and whether it failed because the database itself could not be opened (offline, restoring...).</returns>
    private async Task<(ManagedProbeResult Result, bool DatabaseUnavailable)> ProbeCoreAsync(
        ManagedConnectionInput input, bool isNew, bool listDatabases, CancellationToken ct)
    {
        if (ProbeConnectionString(input, isNew, out var error, listDatabases) is not { } connectionString)
        {
            return (new ManagedProbeResult(false, error!), false);
        }

        var builder = new SqlConnectionStringBuilder(connectionString);
        try
        {
            if (listDatabases)
            {
                var databases = await DatabaseStateOps.ListAsync(connectionString, ct).ConfigureAwait(false);
                var notOnline = databases.Count(d => !string.Equals(d.State, Connections.DatabaseState.Online, StringComparison.OrdinalIgnoreCase));
                return (new ManagedProbeResult(
                    true, notOnline > 0 ? $"{databases.Count} databases ({notOnline} not online)." : $"{databases.Count} databases.", databases), false);
            }

            await using var conn = new SqlConnection(connectionString);
            await conn.OpenAsync(ct).ConfigureAwait(false);

            await using var info = new SqlCommand("SELECT CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(128)), DB_NAME()", conn);
            await using var r = await info.ExecuteReaderAsync(ct).ConfigureAwait(false);
            _ = await r.ReadAsync(ct).ConfigureAwait(false);
            return (new ManagedProbeResult(true, $"Connected to {conn.DataSource} / {r.GetString(1)} (SQL Server {r.GetString(0)})."), false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Human-initiated from the view (app-only tool), so the full reason is shown, unlike ad-hoc probes.
            logger.LogWarning("Managed connection test failed ({ConnectionString}): {Error}",
                ConnectionStringMasker.Mask(builder.ConnectionString), ex.Message);
            return (new ManagedProbeResult(false, ex.Message), ex is SqlException sql && DatabaseStateOps.IsDatabaseUnavailable(sql));
        }
    }

    private IReadOnlyList<ManagedConnection> SafeLoad()
    {
        try
        {
            return store.Load();
        }
        catch (ManagedConnectionFileException)
        {
            return [];
        }
    }

    /// <summary>Why an entry cannot be registered, or null with its connection string.</summary>
    private string? Problem(ManagedConnection entry, out string? connectionString)
    {
        connectionString = null;
        var input = new ManagedConnectionInput
        {
            Name = entry.Name,
            Auth = entry.Auth,
            Server = entry.Server,
            Database = entry.Database,
            User = entry.User ?? "",
            RawConnectionString = entry.RawConnectionString ?? "",
            Encrypt = entry.Encrypt,
            TrustServerCertificate = entry.TrustServerCertificate,
            ReadOnly = entry.ReadOnly,
            Insights = entry.Insights,
        };
        var errors = ManagedConnectionRules.Validate(input, hasSavedPassword: !string.IsNullOrEmpty(entry.PasswordProtected));
        if (errors.Count > 0)
        {
            return "Invalid entry: " + string.Join(" ", errors.Values);
        }

        string? password = null;
        if (entry.Auth == ManagedAuth.Sql)
        {
            password = protector.TryUnprotect(entry.PasswordProtected!);
            if (password is null)
            {
                return "The saved password cannot be decrypted by this Windows account. Edit the connection and enter the password again.";
            }
        }

        connectionString = ManagedConnectionRules.BuildConnectionString(entry, password);
        return null;
    }
}
