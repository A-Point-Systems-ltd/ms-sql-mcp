using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Mssql.McpServer.Serialization.Toon;

/// <summary>
/// TOON (Token-Oriented Object Notation, spec v4) encoder over <see cref="JsonElement"/>, ported from the chatbot API
/// (ChatbotApi.Services.Toon). Works on the JSON the SDK already produced, so value conversion (dates, GUIDs, binary)
/// is identical to the JSON path, and numbers are copied from their raw JSON text - never through double - so
/// decimals, money and bigints stay exact. No key folding; comma delimiter; 2-space indent.
/// </summary>
internal static partial class ToonEncoder
{
    internal const int MaxDepth = 64;
    private const string Delimiter = ",";
    private const int Indent = 2;

    public static string Encode(JsonElement value)
    {
        var lines = new List<string>();
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                EncodeObject(value, lines, 0);
                break;
            case JsonValueKind.Array:
                EncodeArray(key: null, value, lines, 0);
                break;
            default:
                lines.Add(EncodePrimitive(value));
                break;
        }

        return string.Join('\n', lines);
    }

    /// <summary>Uniform array of 1+ non-empty objects with identical key sets and only primitive values.</summary>
    public static bool IsTabular(JsonElement array)
    {
        if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() == 0)
        {
            return false;
        }

        HashSet<string>? keys = null;
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var rowKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in item.EnumerateObject())
            {
                if (!IsPrimitive(p.Value) || !rowKeys.Add(p.Name))
                {
                    return false;
                }
            }

            if (rowKeys.Count == 0)
            {
                return false;
            }

            if (keys is null)
            {
                keys = rowKeys;
            }
            else if (!keys.SetEquals(rowKeys))
            {
                return false;
            }
        }

        return true;
    }

    public static bool IsArrayOfPrimitives(JsonElement array) =>
        array.ValueKind == JsonValueKind.Array && array.EnumerateArray().All(IsPrimitive);

    private static bool IsPrimitive(JsonElement e) =>
        e.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null;

    private static void Push(List<string> lines, int depth, string text) => lines.Add(new string(' ', depth * Indent) + text);

    private static void Guard(int depth)
    {
        if (depth >= MaxDepth)
        {
            throw new InvalidOperationException($"TOON: maximum depth of {MaxDepth} exceeded.");
        }
    }

    private static void EncodeObject(JsonElement obj, List<string> lines, int depth)
    {
        Guard(depth);
        var any = false;
        foreach (var p in obj.EnumerateObject())
        {
            any = true;
            EncodeField(p.Name, p.Value, lines, depth);
        }

        if (!any)
        {
            Push(lines, depth, "{}");
        }
    }

    private static void EncodeField(string name, JsonElement value, List<string> lines, int depth)
    {
        var key = EncodeKey(name);
        switch (value.ValueKind)
        {
            case JsonValueKind.Array:
                EncodeArray(name, value, lines, depth);
                break;
            case JsonValueKind.Object:
                Push(lines, depth, key + ":");
                if (value.EnumerateObject().Any())
                {
                    EncodeObject(value, lines, depth + 1);
                }

                break;
            default:
                Push(lines, depth, $"{key}: {EncodePrimitive(value)}");
                break;
        }
    }

    /// <summary>Writes an array, keyed (<c>key[N]...</c>) or bare (<c>[N]...</c>), choosing inline, tabular or list form.</summary>
    private static void EncodeArray(string? key, JsonElement array, List<string> lines, int depth, string prefix = "")
    {
        Guard(depth);
        var count = array.GetArrayLength();
        if (count == 0)
        {
            Push(lines, depth, prefix + (key is null ? "[]" : EncodeKey(key) + ": []"));
            return;
        }

        if (IsArrayOfPrimitives(array))
        {
            Push(lines, depth, $"{prefix}{Header(count, key, null)} {string.Join(Delimiter, array.EnumerateArray().Select(EncodePrimitive))}");
            return;
        }

        if (IsTabular(array))
        {
            var fields = array[0].EnumerateObject().Select(p => p.Name).ToList();
            Push(lines, depth, prefix + Header(count, key, fields));
            // A list-item header ("- key[N]{...}:") sits one level deeper than its own depth.
            var rowDepth = depth + (prefix.Length > 0 ? 2 : 1);
            foreach (var row in array.EnumerateArray())
            {
                Push(lines, rowDepth, string.Join(Delimiter, fields.Select(f => EncodePrimitive(row.GetProperty(f)))));
            }

            return;
        }

        Push(lines, depth, prefix + Header(count, key, null));
        var itemDepth = depth + (prefix.Length > 0 ? 2 : 1);
        foreach (var item in array.EnumerateArray())
        {
            EncodeListItem(item, lines, itemDepth);
        }
    }

    private static void EncodeListItem(JsonElement item, List<string> lines, int depth)
    {
        Guard(depth);
        switch (item.ValueKind)
        {
            case JsonValueKind.Array:
                EncodeArray(key: null, item, lines, depth, prefix: "- ");
                break;
            case JsonValueKind.Object:
                var props = item.EnumerateObject().ToList();
                if (props.Count == 0)
                {
                    Push(lines, depth, "-");
                    break;
                }

                // First field on the "- " line; the rest one level deeper.
                var first = props[0];
                switch (first.Value.ValueKind)
                {
                    case JsonValueKind.Array:
                        EncodeArray(first.Name, first.Value, lines, depth, prefix: "- ");
                        break;
                    case JsonValueKind.Object:
                        Push(lines, depth, $"- {EncodeKey(first.Name)}:");
                        if (first.Value.EnumerateObject().Any())
                        {
                            EncodeObject(first.Value, lines, depth + 2);
                        }

                        break;
                    default:
                        Push(lines, depth, $"- {EncodeKey(first.Name)}: {EncodePrimitive(first.Value)}");
                        break;
                }

                foreach (var p in props.Skip(1))
                {
                    EncodeField(p.Name, p.Value, lines, depth + 1);
                }

                break;
            default:
                Push(lines, depth, "- " + EncodePrimitive(item));
                break;
        }
    }

    private static string Header(int count, string? key, IReadOnlyList<string>? fields)
    {
        var sb = new StringBuilder();
        if (key is not null)
        {
            sb.Append(EncodeKey(key));
        }

        sb.Append('[').Append(count.ToString(CultureInfo.InvariantCulture)).Append(']');
        if (fields is { Count: > 0 })
        {
            sb.Append('{').Append(string.Join(Delimiter, fields.Select(EncodeKey))).Append('}');
        }

        return sb.Append(':').ToString();
    }

    internal static string EncodePrimitive(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => "null",
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Number => value.GetRawText(),
        JsonValueKind.String => EncodeString(value.GetString()!),
        _ => throw new InvalidOperationException($"TOON: {value.ValueKind} is not a primitive."),
    };

    internal static string EncodeString(string value) =>
        IsSafeUnquoted(value) ? value : "\"" + Escape(value) + "\"";

    /// <summary>Keys stay bare when ASCII identifier-like (dots allowed, TOON v4 §7.3); otherwise quoted.</summary>
    internal static string EncodeKey(string key) =>
        UnquotedKey().IsMatch(key) ? key : "\"" + Escape(key) + "\"";

    /// <summary>TOON v4 §7.2: a value is bare only when it cannot be mistaken for structure, a literal or a number.</summary>
    internal static bool IsSafeUnquoted(string value)
    {
        if (value.Length == 0 || value != value.Trim())
        {
            return false;
        }

        if (value is "true" or "false" or "null" || NumericLike().IsMatch(value) || LeadingZero().IsMatch(value))
        {
            return false;
        }

        if (value[0] is '#' or '-')
        {
            return false;
        }

        foreach (var c in value)
        {
            if (c is ':' or '[' or ']' or '{' or '}' or '"' or '\\' or ',' || c <= '\u001F')
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Short escapes for \\ \" \n \r \t; other controls U+0000-U+001F as \uXXXX.</summary>
    internal static string Escape(string value)
    {
        var sb = new StringBuilder(value.Length + 8);
        foreach (var c in value)
        {
            _ = c switch
            {
                '\\' => sb.Append("\\\\"),
                '"' => sb.Append("\\\""),
                '\n' => sb.Append("\\n"),
                '\r' => sb.Append("\\r"),
                '\t' => sb.Append("\\t"),
                <= '\u001F' => sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture)),
                _ => sb.Append(c),
            };
        }

        return sb.ToString();
    }

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_.]*$")]
    private static partial Regex UnquotedKey();

    [GeneratedRegex(@"^[+-]?\d+(\.\d+)?(e[+-]?\d+)?$", RegexOptions.IgnoreCase)]
    private static partial Regex NumericLike();

    [GeneratedRegex(@"^0\d+$")]
    private static partial Regex LeadingZero();
}
