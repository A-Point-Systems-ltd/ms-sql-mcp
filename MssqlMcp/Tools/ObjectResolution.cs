// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.

using System.Data;
using System.Text;
using Microsoft.Data.SqlClient;

namespace Mssql.McpServer;

/// <summary>
/// A parsed multi-part object name. <see cref="ParentName"/> is only set for trigger names
/// ('schema.table.trigger'); <see cref="Database"/> is only set for 'database.schema.name'.
/// </summary>
internal readonly record struct ObjectNameParts(string? Database, string? Schema, string Name, string? ParentName = null);

/// <summary>An object resolved against sys.objects in the connected database.</summary>
internal readonly record struct ResolvedObject(int ObjectId, string Schema, string Name);

/// <summary>
/// Parses T-SQL multi-part identifiers: 1-3 parts separated by '.', each part plain, [bracketed]
/// (with ']]' as an escaped ']') or "double-quoted" (with '""' as an escaped '"').
/// </summary>
internal static class ObjectNameParser
{
    internal const int MaxParts = 3;
    private const int MaxPartLength = 128; // sysname

    /// <summary>Parses 'name', 'schema.name' or 'database.schema.name'.</summary>
    public static bool TryParse(string? input, out string? database, out string? schema, out string name, out string? error)
    {
        var ok = TryParse(input, out var parts, out error);
        database = parts.Database;
        schema = parts.Schema;
        name = parts.Name;
        return ok;
    }

    /// <summary>Parses 'name', 'schema.name' or 'database.schema.name'.</summary>
    public static bool TryParse(string? input, out ObjectNameParts parts, out string? error)
    {
        parts = new ObjectNameParts(null, null, string.Empty);
        if (!TryParseParts(input, out var list, out error))
        {
            return false;
        }

        parts = list.Count switch
        {
            1 => new ObjectNameParts(null, null, list[0]),
            2 => new ObjectNameParts(null, list[0], list[1]),
            _ => new ObjectNameParts(list[0], list[1], list[2]),
        };
        return true;
    }

    /// <summary>
    /// Parses a trigger name: 'trigger', 'schema.trigger' or 'schema.table.trigger'
    /// (three parts are schema.table.name here, not database.schema.name).
    /// </summary>
    public static bool TryParseTrigger(string? input, out ObjectNameParts parts, out string? error)
    {
        parts = new ObjectNameParts(null, null, string.Empty);
        if (!TryParseParts(input, out var list, out error))
        {
            return false;
        }

        parts = list.Count switch
        {
            1 => new ObjectNameParts(null, null, list[0]),
            2 => new ObjectNameParts(null, list[0], list[1]),
            _ => new ObjectNameParts(null, list[0], list[2], ParentName: list[1]),
        };
        return true;
    }

    /// <summary>Splits an identifier into its unquoted parts (1..3). Rejects empty parts and more than 3 parts.</summary>
    public static bool TryParseParts(string? input, out IReadOnlyList<string> parts, out string? error)
    {
        parts = Array.Empty<string>();
        if (string.IsNullOrWhiteSpace(input))
        {
            error = "Object name is required.";
            return false;
        }

        var s = input;
        var list = new List<string>(MaxParts);
        var sb = new StringBuilder();
        var i = 0;
        while (true)
        {
            while (i < s.Length && char.IsWhiteSpace(s[i]))
            {
                i++;
            }

            sb.Clear();
            string part;
            if (i < s.Length && s[i] is '[' or '"')
            {
                var close = s[i] == '[' ? ']' : '"';
                i++;
                var closed = false;
                while (i < s.Length)
                {
                    if (s[i] == close)
                    {
                        if (i + 1 < s.Length && s[i + 1] == close)
                        {
                            sb.Append(close);
                            i += 2;
                            continue;
                        }

                        i++;
                        closed = true;
                        break;
                    }

                    sb.Append(s[i]);
                    i++;
                }

                if (!closed)
                {
                    error = $"Invalid object name '{input}': unterminated {(close == ']' ? "[bracketed]" : "\"quoted\"")} identifier.";
                    return false;
                }

                while (i < s.Length && char.IsWhiteSpace(s[i]))
                {
                    i++;
                }

                if (i < s.Length && s[i] != '.')
                {
                    error = $"Invalid object name '{input}': unexpected character '{s[i]}' after a quoted identifier.";
                    return false;
                }

                // Quoted identifiers keep their inner whitespace verbatim.
                part = sb.ToString();
            }
            else
            {
                var start = i;
                while (i < s.Length && s[i] != '.')
                {
                    if (s[i] is '[' or ']' or '"')
                    {
                        error = $"Invalid object name '{input}': unexpected '{s[i]}' inside an unquoted identifier.";
                        return false;
                    }

                    i++;
                }

                part = s[start..i].Trim();
            }

            if (string.IsNullOrWhiteSpace(part))
            {
                error = $"Invalid object name '{input}': empty name part.";
                return false;
            }

            if (part.Length > MaxPartLength)
            {
                error = $"Invalid object name '{input}': a name part exceeds {MaxPartLength} characters.";
                return false;
            }

            list.Add(part);
            if (list.Count > MaxParts)
            {
                error = $"Invalid object name '{input}': at most {MaxParts} parts are supported.";
                return false;
            }

            if (i >= s.Length)
            {
                break;
            }

            i++; // skip '.'
        }

        parts = list;
        error = null;
        return true;
    }
}

public partial class Tools
{
    // Fixed sys.objects type codes per logical kind. ResolveObjectAsync only accepts codes from this whitelist.
    internal static readonly string[] TableObjectTypes = ["U"];
    internal static readonly string[] ViewObjectTypes = ["V"];
    internal static readonly string[] ProcedureObjectTypes = ["P", "PC", "X", "RF"]; // same set as sys.procedures
    internal static readonly string[] FunctionObjectTypes = ["FN", "TF", "IF", "FT"];
    internal static readonly string[] TriggerObjectTypes = ["TR", "TA"];
    internal static readonly string[] TableFunctionObjectTypes = ["IF", "TF", "FT"];
    internal static readonly string[] ScalarFunctionObjectTypes = ["FN", "FS"];
    internal static readonly string[] ForeignKeyObjectTypes = ["F"];

    private static readonly HashSet<string> ResolvableObjectTypes = new(
        [.. TableObjectTypes, .. ViewObjectTypes, .. ProcedureObjectTypes, .. FunctionObjectTypes, .. TriggerObjectTypes,
         .. TableFunctionObjectTypes, .. ScalarFunctionObjectTypes, .. ForeignKeyObjectTypes],
        StringComparer.Ordinal);

    /// <summary>
    /// Resolves a parsed name to exactly one object in the connected database with a single query.
    /// When the schema is omitted and the name exists in several schemas, dbo wins, then the first schema by name.
    /// A database part must equal DB_NAME(); cross-database lookups return null.
    /// </summary>
    internal static async Task<ResolvedObject?> ResolveObjectAsync(
        SqlConnection conn,
        ObjectNameParts parts,
        string[] sysObjectTypes,
        CancellationToken cancellationToken)
    {
        if (sysObjectTypes.Length == 0)
        {
            throw new ArgumentException("At least one sys.objects type code is required.", nameof(sysObjectTypes));
        }

        var typeList = new StringBuilder();
        await using var cmd = new SqlCommand { Connection = conn };
        for (var i = 0; i < sysObjectTypes.Length; i++)
        {
            var code = sysObjectTypes[i];
            if (!ResolvableObjectTypes.Contains(code))
            {
                throw new ArgumentException($"Unsupported sys.objects type code '{code}'.", nameof(sysObjectTypes));
            }

            var paramName = "@T" + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            typeList.Append(i == 0 ? string.Empty : ", ").Append(paramName);
            cmd.Parameters.Add(paramName, SqlDbType.Char, 2).Value = code;
        }

        cmd.CommandText = $"""
            SELECT TOP (1) o.object_id, s.name, o.name
            FROM sys.objects o
            INNER JOIN sys.schemas s ON o.schema_id = s.schema_id
            WHERE o.name = @Name
              AND (@Schema IS NULL OR s.name = @Schema)
              AND (@Db IS NULL OR @Db = DB_NAME())
              AND (@Parent IS NULL OR EXISTS (
                    SELECT 1 FROM sys.objects p
                    WHERE p.object_id = o.parent_object_id AND p.name = @Parent))
              AND o.type IN ({typeList})
            ORDER BY CASE WHEN s.name = N'dbo' THEN 0 ELSE 1 END, s.name;
            """;
        AddSysnameParameter(cmd, "@Name", parts.Name);
        AddSysnameParameter(cmd, "@Schema", parts.Schema);
        AddSysnameParameter(cmd, "@Db", parts.Database);
        AddSysnameParameter(cmd, "@Parent", parts.ParentName);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new ResolvedObject(reader.GetInt32(0), reader.GetString(1), reader.GetString(2));
    }

    private static void AddSysnameParameter(SqlCommand cmd, string name, string? value) =>
        cmd.Parameters.Add(name, SqlDbType.NVarChar, 128).Value = value is null ? DBNull.Value : value;

    private static void AddObjectIdParameter(SqlCommand cmd, int objectId) =>
        cmd.Parameters.Add("@ObjectId", SqlDbType.Int).Value = objectId;

    /// <summary>Returns OBJECT_DEFINITION for the object, or null when it is encrypted or not visible.</summary>
    private static async Task<object?> ReadObjectDefinitionAsync(SqlConnection conn, int objectId, CancellationToken cancellationToken)
    {
        await using var cmd = new SqlCommand("SELECT OBJECT_DEFINITION(@ObjectId) AS definition", conn);
        AddObjectIdParameter(cmd, objectId);
        var value = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is null or DBNull ? null : value;
    }

    /// <summary>Error text for a name that parsed but matched nothing.</summary>
    private static string ObjectNotFoundMessage(string kind, string input, ObjectNameParts parts) =>
        parts.Database is null
            ? $"{kind} '{input.Trim()}' not found."
            : $"{kind} '{input.Trim()}' not found. A database-qualified name must refer to the connected database.";
}
