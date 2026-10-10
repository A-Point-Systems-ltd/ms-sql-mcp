using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using Mssql.McpServer.LanguageService.Formatting;

namespace Mssql.McpServer.LanguageService;

/// <summary>
/// Resolves a name the way SQL Server does (OBJECT_ID: default schema, brackets; a synonym is followed to its base
/// object) for Go to Object Definition and Select Top Rows. Parameter defaults are read from the module's own
/// definition with ScriptDom, since sys.parameters does not record defaults of T-SQL modules. Read-only.
/// </summary>
internal static class ObjectInfoReader
{
    private const string Query = """
        DECLARE @id int = OBJECT_ID(@name);
        IF @id IS NOT NULL AND EXISTS (SELECT 1 FROM sys.synonyms WHERE object_id = @id)
            SELECT @id = OBJECT_ID(base_object_name) FROM sys.synonyms WHERE object_id = @id;
        SELECT s.name, o.name, RTRIM(o.type)
        FROM sys.objects o INNER JOIN sys.schemas s ON s.schema_id = o.schema_id
        WHERE o.object_id = @id;
        SELECT p.name, TYPE_NAME(p.user_type_id), p.max_length, p.precision, p.scale, p.is_output
        FROM sys.parameters p
        WHERE p.object_id = @id AND p.parameter_id > 0
        ORDER BY p.parameter_id;
        SELECT OBJECT_DEFINITION(@id);
        """;

    /// <summary>The object explorer's script type for a sys.objects type, or null when it has none here.</summary>
    internal static string? ScriptTypeOf(string type) => type switch
    {
        "U" => "Table",
        "V" => "View",
        "P" => "StoredProcedure",
        "IF" or "TF" => "TableFunction",
        "FN" => "ScalarFunction",
        _ => null,
    };

    public static async Task<ObjectInfo> ReadAsync(SqlConnection connection, string name, CancellationToken cancellationToken)
    {
        await using var cmd = new SqlCommand(Query, connection) { CommandTimeout = 30 };
        cmd.Parameters.Add(new SqlParameter("@name", SqlDbType.NVarChar, 4000) { Value = name });
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return new ObjectInfo(false, null, null, null, null, []);
        }

        var schema = reader.GetString(0);
        var objectName = reader.GetString(1);
        var type = reader.GetString(2);
        var parameters = new List<(string Name, string Type, bool IsOutput)>();
        if (await reader.NextResultAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                parameters.Add((reader.GetString(0),
                    CatalogSnapshot.TypeText(reader.GetString(1), reader.GetInt16(2), reader.GetByte(3), reader.GetByte(4)),
                    reader.GetBoolean(5)));
            }
        }

        string? definition = null;
        if (await reader.NextResultAsync(cancellationToken).ConfigureAwait(false) && await reader.ReadAsync(cancellationToken).ConfigureAwait(false) && !reader.IsDBNull(0))
        {
            definition = reader.GetString(0);
        }

        var defaults = definition is null ? new Dictionary<string, string>() : ParameterDefaults(definition);
        return new ObjectInfo(true, schema, objectName, type, ScriptTypeOf(type),
            [.. parameters.Select(p => new ObjectParameterInfo(p.Name, p.Type, defaults.GetValueOrDefault(p.Name), p.IsOutput))]);
    }

    /// <summary>Parameter name → default value text, from a CREATE PROCEDURE / FUNCTION definition (empty when it does not parse).</summary>
    internal static Dictionary<string, string> ParameterDefaults(string definition)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var parser = new TSql170Parser(initialQuotedIdentifiers: true);
        using var text = new StringReader(definition);
        var fragment = parser.Parse(text, out var errors);
        if (errors.Count > 0)
        {
            return result;
        }

        fragment.Accept(new ParameterVisitor(result));
        return result;
    }

    private sealed class ParameterVisitor(Dictionary<string, string> defaults) : TSqlFragmentVisitor
    {
        public override void Visit(ProcedureParameter node)
        {
            if (node.VariableName?.Value is { } name && node.Value is { } value)
            {
                var tokens = value.ScriptTokenStream;
                var parts = new List<string>();
                for (var i = value.FirstTokenIndex; i <= value.LastTokenIndex; i++)
                {
                    parts.Add(tokens[i].Text);
                }

                defaults[name] = string.Concat(parts).Trim();
            }
        }
    }
}
