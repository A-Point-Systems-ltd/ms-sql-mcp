using Microsoft.Data.SqlClient;

namespace Mssql.McpServer.Connections;

public sealed class ConnectionResolutionException(string message) : InvalidOperationException(message);

public enum CloseResult { NotFound, NotOpen, LastOpen, Closed }

public sealed record ConnectionStatus(
    string Name, bool IsOpen, bool ReadOnly, bool InsightsEnabled,
    ConnectionSource Source, string DataSource, string? Database);

/// <summary>
/// Named connection profiles and their open/closed state. There is no default connection: with exactly one
/// registered connection a tool call may omit its name; with more than one (open or closed), it must name it.
/// </summary>
public sealed class ConnectionRegistry
{
    // One lock guards each profile together with its open flag: tools run concurrently, so they must change atomically.
    private readonly Lock _gate = new();
    private readonly Dictionary<string, (ConnectionProfile Profile, bool Open)> _entries = new(StringComparer.OrdinalIgnoreCase);

    public ConnectionRegistry(IEnumerable<ConnectionProfile> initial)
    {
        foreach (var p in initial)
        {
            _entries[p.Name] = (p, true);
        }
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    public bool ConnectionArgumentRequired => Count > 1;

    public ConnectionProfile? Find(string name)
    {
        lock (_gate)
        {
            return _entries.TryGetValue(name, out var e) ? e.Profile : null;
        }
    }

    public bool IsOpen(string name)
    {
        lock (_gate)
        {
            return _entries.TryGetValue(name, out var e) && e.Open;
        }
    }

    public ConnectionProfile? TryResolveSingle()
    {
        lock (_gate)
        {
            if (_entries.Count != 1)
            {
                return null;
            }

            var only = _entries.Values.Single();
            return only.Open ? only.Profile : null;
        }
    }

    public ConnectionProfile Resolve(string? name)
    {
        (ConnectionProfile Profile, bool Open)[] snapshot;
        (ConnectionProfile Profile, bool Open)? hit = null;
        lock (_gate)
        {
            if (!string.IsNullOrWhiteSpace(name) && _entries.TryGetValue(name, out var e))
            {
                if (e.Open)
                {
                    return e.Profile;
                }

                hit = e;
            }
            else if (string.IsNullOrWhiteSpace(name) && _entries.Count == 1)
            {
                var only = _entries.Values.Single();
                if (only.Open)
                {
                    return only.Profile;
                }

                hit = only;
            }

            snapshot = [.. _entries.Values];
        }

        // Messages parse connection strings, so they are built from the snapshot, outside the lock.
        string failure;
        if (hit is { } closed)
        {
            failure = string.IsNullOrWhiteSpace(name)
                ? $"The only connection '{closed.Profile.Name}' is closed; call {ToolNames.OpenConnection} to reopen it."
                : $"Connection '{closed.Profile.Name}' is closed; call {ToolNames.OpenConnection} to reopen it. Connections:{Environment.NewLine}{Describe(snapshot)}";
        }
        else if (!string.IsNullOrWhiteSpace(name))
        {
            failure = $"Connection '{name}' does not exist. Connections:{Environment.NewLine}{Describe(snapshot)}";
        }
        else if (snapshot.Length == 0)
        {
            failure = $"No connection is configured. Call {ToolNames.OpenConnection} (requires MSSQL_ALLOW_ADHOC_CONNECTIONS=true) or configure MSSQL_CONNECTIONS.";
        }
        else
        {
            failure = $"The 'connection' argument is required because this server has {snapshot.Length} connections and no default. " +
                      $"Pass one of these names:{Environment.NewLine}{Describe(snapshot)}";
        }

        throw new ConnectionResolutionException(failure);
    }

    public IReadOnlyList<ConnectionStatus> List() => ToStatuses(TakeSnapshot());

    /// <summary>One line per connection: "name (server/database, read-only, closed)". Never includes credentials.</summary>
    public string DescribeAll() => Describe(TakeSnapshot());

    private (ConnectionProfile Profile, bool Open)[] TakeSnapshot()
    {
        lock (_gate)
        {
            return [.. _entries.Values];
        }
    }

    private static ConnectionStatus ToStatus(ConnectionProfile profile, bool open)
    {
        var b = TryParse(profile.ConnectionString);
        return new ConnectionStatus(profile.Name, open, profile.ReadOnly, profile.InsightsEnabled, profile.Source, b?.DataSource ?? "?", b?.InitialCatalog);
    }

    private static List<ConnectionStatus> ToStatuses(IEnumerable<(ConnectionProfile Profile, bool Open)> entries) =>
        entries.OrderBy(e => e.Profile.Name, StringComparer.OrdinalIgnoreCase).Select(e => ToStatus(e.Profile, e.Open)).ToList();

    private static string Describe(IEnumerable<(ConnectionProfile Profile, bool Open)> entries) =>
        string.Join(Environment.NewLine, ToStatuses(entries).Select(s =>
            $"- {s.Name} ({s.DataSource}/{s.Database ?? "?"}{(s.ReadOnly ? ", read-only" : "")}{(s.IsOpen ? "" : ", closed")})"));

    /// <summary>
    /// Adds an ad-hoc profile or reopens a registered one. A configured or legacy profile is only reopened:
    /// it is never replaced and its <see cref="ConnectionProfile.Source"/> never changes. Returns the status of the stored profile.
    /// </summary>
    public ConnectionStatus Register(ConnectionProfile profile)
    {
        ConnectionProfile stored;
        lock (_gate)
        {
            if (_entries.TryGetValue(profile.Name, out var existing) && existing.Profile.Source != ConnectionSource.Adhoc)
            {
                stored = existing.Profile;
            }
            else
            {
                stored = profile;
            }

            _entries[stored.Name] = (stored, true);
        }

        return ToStatus(stored, open: true);
    }

    /// <summary>
    /// Closes a connection and drops its pooled sessions. Ad-hoc profiles are unregistered (the count drops).
    /// Has no last-open guard: for tests and internal use only; tools must call <see cref="TryClose"/>.
    /// </summary>
    internal bool Close(string name) => CloseCore(name, requireAnotherOpen: false) != CloseResult.NotFound;

    /// <summary>Like <see cref="Close"/> but refuses, atomically, to close the last open connection.</summary>
    public CloseResult TryClose(string name) => CloseCore(name, requireAnotherOpen: true);

    private CloseResult CloseCore(string name, bool requireAnotherOpen)
    {
        ConnectionProfile profile;
        lock (_gate)
        {
            if (!_entries.TryGetValue(name, out var e))
            {
                return CloseResult.NotFound;
            }

            if (requireAnotherOpen)
            {
                if (!e.Open)
                {
                    return CloseResult.NotOpen;
                }

                if (_entries.Values.Count(x => x.Open) == 1)
                {
                    return CloseResult.LastOpen;
                }
            }

            profile = e.Profile;
            if (profile.Source == ConnectionSource.Adhoc)
            {
                _ = _entries.Remove(profile.Name);
            }
            else
            {
                _entries[profile.Name] = (profile, false);
            }
        }

        try
        {
            using var conn = new SqlConnection(profile.ConnectionString);
            SqlConnection.ClearPool(conn);
        }
        catch (Exception)
        {
            // A malformed string has no pool to clear; the state change above already happened.
        }

        return CloseResult.Closed;
    }

    private static SqlConnectionStringBuilder? TryParse(string cs)
    {
        try
        {
            return new SqlConnectionStringBuilder(cs);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
