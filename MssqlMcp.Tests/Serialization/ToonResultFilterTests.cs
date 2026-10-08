using System.Text.Json;
using Mssql.McpServer;
using Mssql.McpServer.Serialization;

namespace MssqlMcp.Tests.Serialization;

public sealed class ToonResultFilterTests
{
    // The SDK's wire shape for DbOperationResult (camelCase, nulls present).
    private static string Result(object? data, bool success = true, bool? truncated = null, int? maxRows = null) =>
        JsonSerializer.Serialize(new { success, error = (string?)null, rowsAffected = (int?)null, data, truncated, maxRows });

    [Fact]
    public void Multi_row_table_becomes_toon_with_envelope_first_and_nulls_dropped()
    {
        var json = Result(new[] { new { id = 1, name = "Alice", amount = 12.50m }, new { id = 2, name = "Bob", amount = 3m } }, truncated: true, maxRows: 2);
        Assert.Equal("success: true\ntruncated: true\nmaxRows: 2\ndata[2]{id,name,amount}:\n  1,Alice,12.50\n  2,Bob,3", ToonResultFilter.TryConvert(json));
    }

    [Fact]
    public void String_list_becomes_an_inline_array()
    {
        Assert.Equal("success: true\ndata[2]: dbo.A,dbo.B", ToonResultFilter.TryConvert(Result(new[] { "dbo.A", "dbo.B" })));
    }

    [Theory]
    [InlineData("""{"success":true,"data":[{"a":1}]}""")]                       // one row: JSON is as cheap
    [InlineData("""{"success":true,"data":[]}""")]
    [InlineData("""{"success":false,"error":"boom","data":[{"a":1},{"a":2}]}""")] // errors stay JSON
    [InlineData("""{"success":true,"data":{"table":"t","columns":[]}}""")]          // describe-style object
    [InlineData("""{"success":true,"data":[{"a":{"n":1}},{"a":{"n":2}}]}""")]       // nested values
    [InlineData("""{"success":true,"data":[{"a":1},{"b":2}]}""")]                   // non-uniform rows
    [InlineData("""not json""")]
    [InlineData("""[1,2,3]""")]
    public void Other_shapes_stay_json(string json)
    {
        Assert.Null(ToonResultFilter.TryConvert(json));
    }

    [Fact]
    public void Toon_is_requested_unless_explicitly_false()
    {
        static IDictionary<string, JsonElement> Args(string json) =>
            JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;

        Assert.True(ToonResultFilter.IsRequested(null));
        Assert.True(ToonResultFilter.IsRequested(Args("""{"sql":"x"}""")));
        Assert.True(ToonResultFilter.IsRequested(Args("""{"toon":true}""")));
        Assert.True(ToonResultFilter.IsRequested(Args("""{"toon":null}""")));
        Assert.False(ToonResultFilter.IsRequested(Args("""{"toon":false}""")));
        Assert.False(ToonResultFilter.IsRequested(Args("""{"toon":"false"}""")));
    }

    [Fact]
    public void Mssql_toon_false_flips_only_the_default()
    {
        static IDictionary<string, JsonElement> Args(string json) =>
            JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;

        Assert.False(ToonResultFilter.IsRequested(null, defaultOn: false));
        Assert.False(ToonResultFilter.IsRequested(Args("""{"sql":"x"}"""), defaultOn: false));
        Assert.True(ToonResultFilter.IsRequested(Args("""{"toon":true}"""), defaultOn: false));
        Assert.True(ToonResultFilter.IsRequested(Args("""{"toon":"true"}"""), defaultOn: false));

        Assert.True(ToonResultFilter.IsDefaultOn(_ => null));
        Assert.True(ToonResultFilter.IsDefaultOn(_ => "true"));
        foreach (var off in new[] { "false", "FALSE", " 0 ", "no", "off", "disabled" })
        {
            Assert.False(ToonResultFilter.IsDefaultOn(_ => off), off);
        }
    }

    [Fact]
    public void Server_instructions_follow_the_default()
    {
        var registry = new Mssql.McpServer.Connections.ConnectionRegistry([]);
        Assert.Contains("toon=false", Mssql.McpServer.Connections.ServerInstructions.Build(registry, toonByDefault: true), StringComparison.Ordinal);
        Assert.Contains("JSON by default on this server; pass toon=true", Mssql.McpServer.Connections.ServerInstructions.Build(registry, toonByDefault: false), StringComparison.Ordinal);
    }

    [Fact]
    public void Toon_is_shorter_than_json_for_a_typical_result()
    {
        var rows = Enumerable.Range(1, 50).Select(i => new
        {
            InvoiceID = i, CustomerName = $"Customer {i}", Amount = 100.25m * i, IssuedAt = new DateTime(2026, 1, 1).AddDays(i), Paid = i % 2 == 0,
        }).ToArray();
        var json = Result(rows);
        var toon = ToonResultFilter.TryConvert(json);
        Assert.NotNull(toon);
        Assert.True(toon!.Length < json.Length * 0.6, $"TOON {toon.Length} chars vs JSON {json.Length}");
    }

    [Fact]
    public void Toon_tools_are_the_row_returning_tools()
    {
        Assert.Equal(
            new[] { ToolNames.GetInsightHistory, ToolNames.ListInsights, ToolNames.ListObjects, ToolNames.ReadData },
            ToolNames.ToonTools.OrderBy(t => t, StringComparer.Ordinal));
    }
}
