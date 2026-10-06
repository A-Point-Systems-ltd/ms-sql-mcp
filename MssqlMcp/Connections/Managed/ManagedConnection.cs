namespace Mssql.McpServer.Connections.Managed;

/// <summary>
/// One entry of the managed connections file (MSSQL_MANAGED_CONNECTIONS_FILE). Kept structured, like the VS Code
/// extension's profile, so the connections view can edit it. The password is stored only as
/// <see cref="PasswordProtected"/> (DPAPI, current Windows user), never in clear text.
/// </summary>
public sealed record ManagedConnection
{
    public required string Name { get; init; }

    /// <summary>windows | sql | entraInteractive | entraDefault | raw (see <see cref="ManagedAuth"/>).</summary>
    public required string Auth { get; init; }

    public string Server { get; init; } = "";

    public string Database { get; init; } = "";

    /// <summary>Login for sql and entraInteractive.</summary>
    public string? User { get; init; }

    /// <summary>sql only: base64 DPAPI blob of the password.</summary>
    public string? PasswordProtected { get; init; }

    /// <summary>mandatory | optional | strict.</summary>
    public string Encrypt { get; init; } = ManagedEncrypt.Optional;

    public bool TrustServerCertificate { get; init; } = true;

    public bool ReadOnly { get; init; } = true;

    public bool Insights { get; init; } = true;

    /// <summary>raw only: a full connection string without a password.</summary>
    public string? RawConnectionString { get; init; }
}

/// <summary>The managed connections file: a versioned wrapper so the format can evolve.</summary>
public sealed record ManagedConnectionsFile
{
    public const int CurrentVersion = 1;

    public int Version { get; init; } = CurrentVersion;

    public List<ManagedConnection> Connections { get; init; } = [];
}

public static class ManagedAuth
{
    public const string Windows = "windows";
    public const string Sql = "sql";
    public const string EntraInteractive = "entraInteractive";
    public const string EntraDefault = "entraDefault";
    public const string Raw = "raw";

    public static readonly IReadOnlyList<string> All = [Windows, Sql, EntraInteractive, EntraDefault, Raw];

    public static bool UsesUser(string auth) => auth is Sql or EntraInteractive;
}

public static class ManagedEncrypt
{
    public const string Mandatory = "mandatory";
    public const string Optional = "optional";
    public const string Strict = "strict";

    public static readonly IReadOnlyList<string> All = [Mandatory, Optional, Strict];
}
