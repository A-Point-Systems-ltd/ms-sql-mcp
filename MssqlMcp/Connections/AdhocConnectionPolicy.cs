using Microsoft.Data.SqlClient;

namespace Mssql.McpServer.Connections;

/// <summary>
/// Operator-controlled limits for ad-hoc connection strings supplied by the agent through open_connection.
/// Every keyword comes from the agent (and so from anything that can prompt-inject it), so risky keywords are
/// refused unless the operator opts in with an environment variable.
/// </summary>
internal static class AdhocConnectionPolicy
{
    public const string AllowIntegratedAuthVariable = "MSSQL_ADHOC_ALLOW_INTEGRATED_AUTH";
    public const string AllowWriteVariable = "MSSQL_ADHOC_ALLOW_WRITE";
    public const string AllowedHostsVariable = "MSSQL_ADHOC_ALLOWED_HOSTS";

    /// <summary>Returns null when the connection string may be probed and registered, otherwise the refusal message.</summary>
    public static string? Validate(string connectionString, bool readOnly, Func<string, string?> getEnv)
    {
        SqlConnectionStringBuilder builder;
        try
        {
            builder = new SqlConnectionStringBuilder(connectionString);
        }
        catch (Exception)
        {
            // Never echo the input: it may contain a password.
            return "The ad-hoc connection string could not be parsed.";
        }

        if (!string.IsNullOrWhiteSpace(builder.AttachDBFilename))
        {
            return "AttachDBFilename is not allowed in ad-hoc connections.";
        }

        if (builder.UserInstance)
        {
            return "User Instance is not allowed in ad-hoc connections.";
        }

        // A failover partner is a second target host that the host allowlist (Data Source only) would not see.
        if (!string.IsNullOrWhiteSpace(builder.FailoverPartner))
        {
            return "Failover Partner is not allowed in ad-hoc connections.";
        }

        if (UsesServerIdentity(builder) && !IsTrue(getEnv(AllowIntegratedAuthVariable)))
        {
            return "Ad-hoc connections may not use Integrated Security / Trusted_Connection or Active Directory authentication " +
                   $"(it would send the server's own identity to the target host). Use SQL authentication, or the operator must set {AllowIntegratedAuthVariable}=true.";
        }

        if (!readOnly && !IsTrue(getEnv(AllowWriteVariable)))
        {
            return $"Writable ad-hoc connections are disabled. Open it with readOnly=true, or the operator must set {AllowWriteVariable}=true.";
        }

        var allowed = ParseAllowedHosts(getEnv(AllowedHostsVariable));
        if (allowed.Count > 0 && !allowed.Contains(HostOf(builder.DataSource)))
        {
            return $"The host is not in the operator's allowlist ({AllowedHostsVariable}).";
        }

        return null;
    }

    /// <summary>Integrated Security (any synonym) or any Active Directory authentication method.</summary>
    internal static bool UsesServerIdentity(SqlConnectionStringBuilder builder) =>
        builder.IntegratedSecurity
        || builder.Authentication.ToString().StartsWith("ActiveDirectory", StringComparison.OrdinalIgnoreCase);

    /// <summary>Host part of a Data Source: protocol prefix (tcp:, np:, lpc:, admin:) removed, instance and port dropped.</summary>
    internal static string HostOf(string? dataSource)
    {
        var s = (dataSource ?? string.Empty).Trim();
        var colon = s.IndexOf(':', StringComparison.Ordinal);
        if (colon > 0 && s[..colon] is var prefix
            && (prefix.Equals("tcp", StringComparison.OrdinalIgnoreCase) || prefix.Equals("np", StringComparison.OrdinalIgnoreCase)
                || prefix.Equals("lpc", StringComparison.OrdinalIgnoreCase) || prefix.Equals("admin", StringComparison.OrdinalIgnoreCase)))
        {
            s = s[(colon + 1)..];
        }

        // Named-pipe form: \\host\pipe\...
        s = s.TrimStart('\\');
        var end = s.IndexOfAny(['\\', ',']);
        return (end >= 0 ? s[..end] : s).Trim();
    }

    private static HashSet<string> ParseAllowedHosts(string? value) =>
        new((value ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), StringComparer.OrdinalIgnoreCase);

    private static bool IsTrue(string? value) => string.Equals(value?.Trim(), "true", StringComparison.OrdinalIgnoreCase);
}
