using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace Mssql.McpServer.Connections.Managed;

/// <summary>What the connections view submits. <see cref="Password"/> is write-only: never returned or logged.</summary>
public sealed record ManagedConnectionInput
{
    public string Name { get; init; } = "";
    public string Auth { get; init; } = ManagedAuth.Windows;
    public string Server { get; init; } = "";
    public string Database { get; init; } = "";
    public string User { get; init; } = "";

    /// <summary>sql only. Empty when editing keeps the saved password.</summary>
    public string Password { get; init; } = "";

    public string RawConnectionString { get; init; } = "";
    public string Encrypt { get; init; } = ManagedEncrypt.Optional;
    public bool TrustServerCertificate { get; init; } = true;
    public bool ReadOnly { get; init; } = true;
    public bool Insights { get; init; } = true;
}

/// <summary>
/// Validation and connection-string building for managed connections. Mirrors the VS Code extension
/// (vscode-extension/src/connections/profile.ts and connectionFormModel.ts) so both forms accept the same input.
/// Errors are keyed by input field name so the view can show them inline.
/// </summary>
public static partial class ManagedConnectionRules
{
    public const string ApplicationName = "APoint-ms-sql";

    /// <summary>
    /// Validates <paramref name="input"/>. <paramref name="hasSavedPassword"/> lets an edit leave the password empty.
    /// The name is checked for format only; uniqueness is the caller's job (it needs the registry).
    /// </summary>
    public static Dictionary<string, string> Validate(ManagedConnectionInput input, bool hasSavedPassword)
    {
        var errors = new Dictionary<string, string>(StringComparer.Ordinal);
        var name = input.Name?.Trim() ?? "";
        if (!ConnectionConfigLoader.IsValidName(name))
        {
            errors["name"] = "Name must be 1-64 letters, digits, \"-\", \"_\" or \".\".";
        }

        if (!ManagedAuth.All.Contains(input.Auth))
        {
            errors["auth"] = "Unknown authentication type.";
            return errors;
        }

        if (!ManagedEncrypt.All.Contains(input.Encrypt))
        {
            errors["encrypt"] = "Unknown encryption option.";
        }

        if (input.Auth == ManagedAuth.Raw)
        {
            var raw = input.RawConnectionString?.Trim() ?? "";
            if (raw.Length == 0)
            {
                errors["rawConnectionString"] = "Connection string is required.";
            }
            else if (EnvPlaceholder().IsMatch(raw))
            {
                errors["rawConnectionString"] = EnvPlaceholderError;
            }
            else if (RawPassword().IsMatch(raw))
            {
                errors["rawConnectionString"] = "Remove the password - use SQL login so it can be stored encrypted.";
            }
            else if (!TryParse(raw))
            {
                errors["rawConnectionString"] = "This is not a valid SQL Server connection string.";
            }

            return errors;
        }

        Required("server", input.Server, true, "Server");
        Required("database", input.Database, true, "Database");
        Required("user", input.User, ManagedAuth.UsesUser(input.Auth), "User");
        if (input.Auth == ManagedAuth.Sql && string.IsNullOrEmpty(input.Password) && !hasSavedPassword)
        {
            errors["password"] = "Password is required.";
        }

        return errors;

        void Required(string key, string? value, bool required, string label)
        {
            var v = value?.Trim() ?? "";
            if (v.Length == 0)
            {
                if (required)
                {
                    errors[key] = $"{label} is required.";
                }
            }
            else if (EnvPlaceholder().IsMatch(v))
            {
                // The server expands ${env:...} only in configured files; refuse it here so input can never read env vars.
                errors[key] = EnvPlaceholderError;
            }
        }
    }

    /// <summary>The stored entry for a validated input (password handled by the caller).</summary>
    public static ManagedConnection ToEntry(ManagedConnectionInput input, string? passwordProtected)
    {
        var raw = input.Auth == ManagedAuth.Raw;
        return new ManagedConnection
        {
            Name = input.Name.Trim(),
            Auth = input.Auth,
            Server = raw ? "" : input.Server.Trim(),
            Database = raw ? "" : input.Database.Trim(),
            User = !raw && ManagedAuth.UsesUser(input.Auth) ? input.User.Trim() : null,
            PasswordProtected = input.Auth == ManagedAuth.Sql ? passwordProtected : null,
            Encrypt = input.Encrypt,
            TrustServerCertificate = input.TrustServerCertificate,
            ReadOnly = input.ReadOnly,
            Insights = input.Insights,
            RawConnectionString = raw ? input.RawConnectionString.Trim() : null,
        };
    }

    /// <summary>Builds the SqlClient connection string. <paramref name="password"/> is used for sql auth only.</summary>
    public static string BuildConnectionString(ManagedConnection entry, string? password, string? databaseOverride = null)
    {
        if (entry.Auth == ManagedAuth.Raw)
        {
            if (databaseOverride is null)
            {
                return entry.RawConnectionString ?? "";
            }

            return new SqlConnectionStringBuilder(entry.RawConnectionString) { InitialCatalog = databaseOverride }.ConnectionString;
        }

        var b = new SqlConnectionStringBuilder
        {
            DataSource = entry.Server,
            InitialCatalog = databaseOverride ?? entry.Database,
            Encrypt = entry.Encrypt switch
            {
                ManagedEncrypt.Mandatory => SqlConnectionEncryptOption.Mandatory,
                ManagedEncrypt.Strict => SqlConnectionEncryptOption.Strict,
                _ => SqlConnectionEncryptOption.Optional,
            },
            TrustServerCertificate = entry.TrustServerCertificate,
            ApplicationName = ApplicationName,
        };
        switch (entry.Auth)
        {
            case ManagedAuth.Windows:
                b.IntegratedSecurity = true;
                break;
            case ManagedAuth.Sql:
                b.UserID = entry.User ?? "";
                b.Password = password ?? "";
                break;
            case ManagedAuth.EntraInteractive:
                b.Authentication = SqlAuthenticationMethod.ActiveDirectoryInteractive;
                b.UserID = entry.User ?? "";
                break;
            case ManagedAuth.EntraDefault:
                b.Authentication = SqlAuthenticationMethod.ActiveDirectoryDefault;
                break;
        }

        if (entry.ReadOnly)
        {
            b.ApplicationIntent = ApplicationIntent.ReadOnly;
        }

        return b.ConnectionString;
    }

    private const string EnvPlaceholderError = "The text \"${env:\" is not allowed here.";

    private static bool TryParse(string cs)
    {
        try
        {
            _ = new SqlConnectionStringBuilder(cs);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or KeyNotFoundException)
        {
            return false;
        }
    }

    [GeneratedRegex(@"\$\{env:", RegexOptions.IgnoreCase)]
    private static partial Regex EnvPlaceholder();

    [GeneratedRegex(@"\b(password|pwd)\s*=", RegexOptions.IgnoreCase)]
    private static partial Regex RawPassword();
}
