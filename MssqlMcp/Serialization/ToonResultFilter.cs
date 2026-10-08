using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Mssql.McpServer.Serialization.Toon;

namespace Mssql.McpServer.Serialization;

/// <summary>
/// The single place where row results are re-encoded as TOON to save LLM tokens. Runs after the tool (and the
/// connection routing filter): for a tool in <see cref="ToolNames.ToonTools"/> whose call did not pass
/// <c>toon=false</c>, a successful <see cref="DbOperationResult"/> whose <c>data</c> is a table of 2+ rows (or a list
/// of 2+ primitives) is rewritten from JSON to TOON. Anything else - errors, single rows, nested shapes, any failure
/// while encoding - is returned unchanged as JSON.
/// </summary>
internal static class ToonResultFilter
{
    public const string ArgumentName = "toon";
    internal const int MinRows = 2;

    /// <summary>Operator switch for the default when a call does not pass <c>toon</c>: on unless set to a falsey value.</summary>
    internal const string EnableVariable = "MSSQL_TOON";

    /// <summary>False when MSSQL_TOON is false, 0, no, off or disabled (case-insensitive); true otherwise, including unset.</summary>
    internal static bool IsDefaultOn(Func<string, string?> getEnvironmentVariable) =>
        getEnvironmentVariable(EnableVariable)?.Trim().ToLowerInvariant() is not ("false" or "0" or "no" or "off" or "disabled");

    public static McpRequestHandler<CallToolRequestParams, CallToolResult> Create(
        McpRequestHandler<CallToolRequestParams, CallToolResult> next, bool defaultOn = true) =>
        async (context, cancellationToken) =>
        {
            var result = await next(context, cancellationToken).ConfigureAwait(false);
            var toolName = context.Params?.Name ?? string.Empty;
            if (!ToolNames.ToonTools.Contains(toolName) || !IsRequested(context.Params?.Arguments, defaultOn))
            {
                return result;
            }

            if (result.IsError == true || result.Content is not { Count: 1 } || result.Content[0] is not TextContentBlock text)
            {
                return result;
            }

            var toon = TryConvert(text.Text);
            if (toon is not null)
            {
                result.Content = [new TextContentBlock { Text = toon }];
            }

            return result;
        };

    /// <summary>
    /// An explicit <c>true</c> / <c>false</c> (bool, or the strings "true" / "false") decides; absent, null or anything
    /// else falls back to <paramref name="defaultOn"/> (MSSQL_TOON).
    /// </summary>
    internal static bool IsRequested(IDictionary<string, JsonElement>? arguments, bool defaultOn = true)
    {
        if (arguments is null || !arguments.TryGetValue(ArgumentName, out var value))
        {
            return defaultOn;
        }

        return value.ValueKind switch
        {
            JsonValueKind.False => false,
            JsonValueKind.True => true,
            JsonValueKind.String when string.Equals(value.GetString(), "false", StringComparison.OrdinalIgnoreCase) => false,
            JsonValueKind.String when string.Equals(value.GetString(), "true", StringComparison.OrdinalIgnoreCase) => true,
            _ => defaultOn,
        };
    }

    /// <summary>The TOON form of a JSON tool result, or null when it should stay JSON.</summary>
    internal static string? TryConvert(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !TryGet(root, "success", out var success) || success.ValueKind != JsonValueKind.True
                || !TryGet(root, "data", out var data) || data.ValueKind != JsonValueKind.Array
                || data.GetArrayLength() < MinRows
                || !(ToonEncoder.IsTabular(data) || ToonEncoder.IsArrayOfPrimitives(data)))
            {
                return null;
            }

            // Envelope fields first (so truncated/maxRows are read before the rows), nulls dropped, data last.
            // Fields of the original document, in place: no second copy of up to 10,000 rows.
            var envelope = new List<KeyValuePair<string, JsonElement>>();
            string? dataName = null;
            foreach (var p in root.EnumerateObject())
            {
                if (string.Equals(p.Name, "data", StringComparison.OrdinalIgnoreCase))
                {
                    dataName = p.Name;
                }
                else if (p.Value.ValueKind != JsonValueKind.Null)
                {
                    envelope.Add(new(p.Name, p.Value));
                }
            }

            envelope.Add(new(dataName!, data));
            return ToonEncoder.EncodeFields(envelope);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>camelCase is what the SDK emits today; PascalCase is accepted in case its naming policy changes.</summary>
    private static bool TryGet(JsonElement root, string camelName, out JsonElement value) =>
        root.TryGetProperty(camelName, out value)
        || root.TryGetProperty(char.ToUpperInvariant(camelName[0]) + camelName[1..], out value);
}
