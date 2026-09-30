using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Mssql.McpServer.Connections;

/// <summary>
/// Builds the connection profile list from, in order: CONNECTION_STRING (profile "default"),
/// MSSQL_CONNECTIONS (JSON array) and MSSQL_CONNECTIONS_FILE (path to a file holding the same JSON array).
/// </summary>
internal static partial class ConnectionConfigLoader
{
    public const string LegacyName = "default";

    private sealed record ProfileDto(string? Name, string? ConnectionString, bool? ReadOnly, bool? Insights, JsonElement? Default);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static IReadOnlyList<ConnectionProfile> Load(Func<string, string?> getEnv, Func<string, string> readFile)
    {
        var profiles = new List<ConnectionProfile>();
        var legacy = getEnv("CONNECTION_STRING");
        if (!string.IsNullOrWhiteSpace(legacy))
        {
            profiles.Add(new ConnectionProfile(LegacyName, legacy, ReadOnly: false, InsightsEnabled: true, ConnectionSource.Legacy));
        }

        var json = getEnv("MSSQL_CONNECTIONS");
        if (!string.IsNullOrWhiteSpace(json))
        {
            profiles.AddRange(Parse(json, "MSSQL_CONNECTIONS", getEnv));
        }

        var file = getEnv("MSSQL_CONNECTIONS_FILE");
        if (!string.IsNullOrWhiteSpace(file))
        {
            string content;
            try
            {
                content = readFile(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new InvalidOperationException($"MSSQL_CONNECTIONS_FILE ({file}) cannot be read: {ex.Message}");
            }

            profiles.AddRange(Parse(content, $"MSSQL_CONNECTIONS_FILE ({file})", getEnv));
        }

        var duplicate = profiles.GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidOperationException($"Connection name '{duplicate.Key}' is defined more than once (duplicate names are compared case-insensitively).");
        }

        return profiles;
    }

    private static IEnumerable<ConnectionProfile> Parse(string json, string source, Func<string, string?> getEnv)
    {
        ProfileDto[]? dtos;
        try
        {
            dtos = JsonSerializer.Deserialize<ProfileDto[]>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"{source} must be a JSON array of {{name, connectionString, readOnly?, insights?}}: {ex.Message}");
        }

        foreach (var dto in dtos ?? [])
        {
            if (dto is null)
            {
                throw new InvalidOperationException($"{source}: the array contains a null entry; every entry must be an object {{name, connectionString, readOnly?, insights?}}.");
            }

            if (string.IsNullOrWhiteSpace(dto.Name) || !NameRegex().IsMatch(dto.Name))
            {
                throw new InvalidOperationException($"{source}: connection name '{dto.Name}' is invalid. Use 1-64 letters, digits, '-', '_' or '.'.");
            }

            if (dto.Default is not null)
            {
                throw new InvalidOperationException($"{source}: connection '{dto.Name}' sets \"default\", but there is no default connection: with more than one connection every tool call must name its connection. Remove the \"default\" property.");
            }

            if (string.IsNullOrWhiteSpace(dto.ConnectionString))
            {
                throw new InvalidOperationException($"{source}: connection '{dto.Name}' has an empty connectionString.");
            }

            yield return new ConnectionProfile(
                dto.Name,
                ExpandEnv(dto.ConnectionString, dto.Name, getEnv),
                dto.ReadOnly ?? false,
                dto.Insights ?? true,
                ConnectionSource.Configured);
        }
    }

    /// <summary>
    /// Substitutes ${env:NAME}. A placeholder enclosed in double quotes ("${env:NAME}") sits inside a quoted
    /// connection-string value, so any '"' in the substituted text is doubled (SqlConnectionStringBuilder rule);
    /// otherwise the value is substituted as is.
    /// </summary>
    private static string ExpandEnv(string value, string profileName, Func<string, string?> getEnv) =>
        EnvPlaceholderRegex().Replace(value, m =>
        {
            var v = getEnv(m.Groups[1].Value)
                ?? throw new InvalidOperationException($"Connection '{profileName}' references ${{env:{m.Groups[1].Value}}} but that environment variable is not set.");
            var quoted = m.Index > 0 && value[m.Index - 1] == '"' && m.Index + m.Length < value.Length && value[m.Index + m.Length] == '"';
            return quoted ? v.Replace("\"", "\"\"") : v;
        });

    [GeneratedRegex(@"^[A-Za-z0-9_.\-]{1,64}\z")]
    private static partial Regex NameRegex();

    public static bool IsValidName(string? name) => name is not null && NameRegex().IsMatch(name);

    [GeneratedRegex(@"\$\{env:([A-Za-z_][A-Za-z0-9_]*)\}")]
    private static partial Regex EnvPlaceholderRegex();
}
