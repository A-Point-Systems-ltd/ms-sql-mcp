using System.Collections.Concurrent;
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
    private readonly ConcurrentDictionary<string, ConnectionProfile> _profiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, bool> _open = new(StringComparer.OrdinalIgnoreCase);

    public ConnectionRegistry(IEnumerable<ConnectionProfile> initial)
    {
        foreach (var p in initial)
        {
            _profiles[p.Name] = p;
            _open[p.Name] = true;
        }
    }

    public int Count => _profiles.Count;

    public bool ConnectionArgumentRequired => Count > 1;

    public ConnectionProfile? Find(string name) => _profiles.TryGetValue(name, out var p) ? p : null;

    public bool IsOpen(string name) => _open.TryGetValue(name, out var open) && open;

    public ConnectionProfile? TryResolveSingle()
    {
        if (Count != 1)
        {
            return null;
        }

        var only = _profiles.Values.First();
        return IsOpen(only.Name) ? only : null;
    }

    public ConnectionProfile Resolve(string? name)
    {
        if (!string.IsNullOrWhiteSpace(name))
        {
            if (_profiles.TryGetValue(name, out var p))
            {
                return IsOpen(p.Name)
                    ? p
                    : throw new ConnectionResolutionException($"Connection '{p.Name}' is closed; call open_connection to reopen it. Connections:{Environment.NewLine}{DescribeAll()}");
            }

            throw new ConnectionResolutionException($"Connection '{name}' does not exist. Connections:{Environment.NewLine}{DescribeAll()}");
        }

        if (Count == 0)
        {
            throw new ConnectionResolutionException("No connection is configured. Call open_connection (requires MSSQL_ALLOW_ADHOC_CONNECTIONS=true) or configure MSSQL_CONNECTIONS.");
        }

        if (ConnectionArgumentRequired)
        {
            throw new ConnectionResolutionException(
                $"The 'connection' argument is required because this server has {Count} connections and no default. " +
                $"Pass one of these names:{Environment.NewLine}{DescribeAll()}");
        }

        return TryResolveSingle()
            ?? throw new ConnectionResolutionException($"The only connection '{_profiles.Values.First().Name}' is closed; call open_connection to reopen it.");
    }

    public IReadOnlyList<ConnectionStatus> List() =>
        _profiles.Values
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Select(p =>
            {
                var b = TryParse(p.ConnectionString);
                return new ConnectionStatus(p.Name, IsOpen(p.Name), p.ReadOnly, p.InsightsEnabled, p.Source, b?.DataSource ?? "?", b?.InitialCatalog);
            })
            .ToList();

    /// <summary>One line per connection: "name (server/database, read-only, closed)". Never includes credentials.</summary>
    public string DescribeAll() =>
        string.Join(Environment.NewLine, List().Select(s =>
            $"- {s.Name} ({s.DataSource}/{s.Database ?? "?"}{(s.ReadOnly ? ", read-only" : "")}{(s.IsOpen ? "" : ", closed")})"));

    /// <summary>Adds an ad-hoc profile or reopens a configured one.</summary>
    public ConnectionProfile Register(ConnectionProfile profile)
    {
        _profiles[profile.Name] = profile;
        _open[profile.Name] = true;
        return profile;
    }

    /// <summary>Closes a connection and drops its pooled sessions. Ad-hoc profiles are unregistered (the count drops).</summary>
    public bool Close(string name)
    {
        if (!_profiles.TryGetValue(name, out var p))
        {
            return false;
        }

        _open[p.Name] = false;
        if (p.Source == ConnectionSource.Adhoc)
        {
            _ = _profiles.TryRemove(p.Name, out _);
            _ = _open.TryRemove(p.Name, out _);
        }

        using var conn = new SqlConnection(p.ConnectionString);
        SqlConnection.ClearPool(conn);
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
