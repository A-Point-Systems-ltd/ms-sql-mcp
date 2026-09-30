using Microsoft.Data.SqlClient;

namespace Mssql.McpServer.Connections;

public sealed class ConnectionResolutionException(string message) : InvalidOperationException(message);

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
        string failure;
        lock (_gate)
        {
            if (!string.IsNullOrWhiteSpace(name))
            {
                if (_entries.TryGetValue(name, out var e))
                {
                    if (e.Open)
                    {
                        return e.Profile;
                    }

                    failure = $"Connection '{e.Profile.Name}' is closed; call {ToolNames.OpenConnection} to reopen it. Connections:{Environment.NewLine}{DescribeAll()}";
                }
                else
                {
                    failure = $"Connection '{name}' does not exist. Connections:{Environment.NewLine}{DescribeAll()}";
                }
            }
            else if (_entries.Count == 0)
            {
                failure = $"No connection is configured. Call {ToolNames.OpenConnection} (requires MSSQL_ALLOW_ADHOC_CONNECTIONS=true) or configure MSSQL_CONNECTIONS.";
            }
            else if (_entries.Count > 1)
            {
                failure = $"The 'connection' argument is required because this server has {_entries.Count} connections and no default. " +
                          $"Pass one of these names:{Environment.NewLine}{DescribeAll()}";
            }
            else
            {
                var only = _entries.Values.Single();
                if (only.Open)
                {
                    return only.Profile;
                }

                failure = $"The only connection '{only.Profile.Name}' is closed; call {ToolNames.OpenConnection} to reopen it.";
            }
        }

        throw new ConnectionResolutionException(failure);
    }

    public IReadOnlyList<ConnectionStatus> List()
    {
        (ConnectionProfile Profile, bool Open)[] snapshot;
        lock (_gate)
        {
            snapshot = [.. _entries.Values];
        }

        return snapshot
            .OrderBy(e => e.Profile.Name, StringComparer.OrdinalIgnoreCase)
            .Select(e =>
            {
                var b = TryParse(e.Profile.ConnectionString);
                return new ConnectionStatus(e.Profile.Name, e.Open, e.Profile.ReadOnly, e.Profile.InsightsEnabled, e.Profile.Source, b?.DataSource ?? "?", b?.InitialCatalog);
            })
            .ToList();
    }

    /// <summary>One line per connection: "name (server/database, read-only, closed)". Never includes credentials.</summary>
    public string DescribeAll() =>
        string.Join(Environment.NewLine, List().Select(s =>
            $"- {s.Name} ({s.DataSource}/{s.Database ?? "?"}{(s.ReadOnly ? ", read-only" : "")}{(s.IsOpen ? "" : ", closed")})"));

    /// <summary>
    /// Adds an ad-hoc profile or reopens a registered one. A configured or legacy profile is only reopened:
    /// it is never replaced and its <see cref="ConnectionProfile.Source"/> never changes. Returns the stored profile.
    /// </summary>
    public ConnectionProfile Register(ConnectionProfile profile)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(profile.Name, out var existing) && existing.Profile.Source != ConnectionSource.Adhoc)
            {
                _entries[existing.Profile.Name] = (existing.Profile, true);
                return existing.Profile;
            }

            _entries[profile.Name] = (profile, true);
            return profile;
        }
    }

    /// <summary>Closes a connection and drops its pooled sessions. Ad-hoc profiles are unregistered (the count drops).</summary>
    public bool Close(string name)
    {
        ConnectionProfile profile;
        lock (_gate)
        {
            if (!_entries.TryGetValue(name, out var e))
            {
                return false;
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

        return true;
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
