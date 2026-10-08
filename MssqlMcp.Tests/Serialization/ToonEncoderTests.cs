using System.Text.Json;
using Mssql.McpServer.Serialization.Toon;

namespace MssqlMcp.Tests.Serialization;

public sealed class ToonEncoderTests
{
    private static string Encode(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return ToonEncoder.Encode(doc.RootElement);
    }

    [Fact]
    public void Tabular_array_gets_a_header_and_one_line_per_row()
    {
        var toon = Encode("""{"orders":[{"id":1,"status":"shipped","qty":5},{"id":2,"status":"pending","qty":3}]}""");
        Assert.Equal("orders[2]{id,status,qty}:\n  1,shipped,5\n  2,pending,3", toon);
    }

    [Fact]
    public void Primitive_array_is_inline()
    {
        Assert.Equal("data[3]: dbo.A,dbo.B,sales.C", Encode("""{"data":["dbo.A","dbo.B","sales.C"]}"""));
    }

    [Fact]
    public void Envelope_fields_and_empty_values()
    {
        // TOON v4: a field holding an empty object is a bare "key:"; an empty array is "key: []".
        Assert.Equal("success: true\ntruncated: false\nempty: []\nobj:", Encode("""{"success":true,"truncated":false,"empty":[],"obj":{}}"""));
    }

    [Theory]
    [InlineData("12345678901234567.89")]
    [InlineData("9007199254740993")]
    [InlineData("1E+400")]
    [InlineData("-0.000000000000000000001")]
    [InlineData("100.50")]
    public void Numbers_are_copied_exactly(string number)
    {
        Assert.Equal($"n: {number}", Encode($$"""{"n":{{number}}}"""));
    }

    [Theory]
    [InlineData("hello world", "hello world")]
    [InlineData("שלום עולם", "שלום עולם")]
    [InlineData("", "\"\"")]
    [InlineData(" padded", "\" padded\"")]
    [InlineData("true", "\"true\"")]
    [InlineData("null", "\"null\"")]
    [InlineData("42", "\"42\"")]
    [InlineData("-3.5e2", "\"-3.5e2\"")]
    [InlineData("007", "\"007\"")]
    [InlineData("-dash", "\"-dash\"")]
    [InlineData("#tag", "\"#tag\"")]
    [InlineData("a,b", "\"a,b\"")]
    [InlineData("12:30", "\"12:30\"")]
    [InlineData("2026-10-08T10:00:00", "\"2026-10-08T10:00:00\"")]
    [InlineData("[x]", "\"[x]\"")]
    [InlineData("say \"hi\"", "\"say \\\"hi\\\"\"")]
    [InlineData("C:\\temp", "\"C:\\\\temp\"")]
    [InlineData("line1\nline2", "\"line1\\nline2\"")]
    [InlineData("tab\there", "\"tab\\there\"")]
    [InlineData("bell\u0007", "\"bell\\u0007\"")]
    public void String_quoting_and_escaping(string value, string expected)
    {
        Assert.Equal($"v: {expected}", Encode(JsonSerializer.Serialize(new { v = value })));
    }

    [Fact]
    public void Keys_are_quoted_when_not_identifiers()
    {
        var toon = Encode("""{"data":[{"שם":"a","order id":1,"":2,"x.y":3},{"שם":"b","order id":2,"":3,"x.y":4}]}""");
        Assert.Equal("data[2]{\"שם\",\"order id\",\"\",x.y}:\n  a,1,2,3\n  b,2,3,4", toon);
    }

    [Fact]
    public void Null_values_in_rows()
    {
        Assert.Equal("data[2]{a,b}:\n  1,null\n  null,x", Encode("""{"data":[{"a":1,"b":null},{"a":null,"b":"x"}]}"""));
    }

    [Fact]
    public void Rows_with_different_keys_are_not_tabular()
    {
        var toon = Encode("""{"data":[{"a":1},{"b":2}]}""");
        Assert.Equal("data[2]:\n  - a: 1\n  - b: 2", toon);
    }

    [Fact]
    public void Tabular_rows_follow_the_first_rows_key_order()
    {
        Assert.Equal("data[2]{a,b}:\n  1,2\n  3,4", Encode("""{"data":[{"a":1,"b":2},{"b":4,"a":3}]}"""));
    }

    [Fact]
    public void Nested_objects_and_list_items()
    {
        var toon = Encode("""{"meta":{"server":"s1","db":"d1"},"items":[{"name":"x","tags":["a","b"]},[1,2],"plain"]}""");
        Assert.Equal(
            "meta:\n  server: s1\n  db: d1\nitems[3]:\n  - name: x\n    tags[2]: a,b\n  - [2]: 1,2\n  - plain",
            toon);
    }

    [Fact]
    public void Tabular_array_as_first_field_of_a_list_item_indents_rows_under_the_item()
    {
        var toon = Encode("""{"sets":[{"rows":[{"a":1},{"a":2}],"count":2}]}""");
        Assert.Equal("sets[1]:\n  - rows[2]{a}:\n      1\n      2\n    count: 2", toon);
    }

    [Fact]
    public void Depth_guard_throws_instead_of_overflowing()
    {
        var deep = string.Concat(Enumerable.Repeat("{\"a\":", ToonEncoder.MaxDepth + 1)) + "1" + new string('}', ToonEncoder.MaxDepth + 1);
        // JsonDocument's own default limit is also 64; raise it so the encoder's guard is what trips.
        using var doc = JsonDocument.Parse(deep, new JsonDocumentOptions { MaxDepth = 256 });
        Assert.Throws<InvalidOperationException>(() => ToonEncoder.Encode(doc.RootElement));
    }

    [Fact]
    public void Is_tabular_requires_uniform_primitive_objects()
    {
        static bool Tab(string json)
        {
            using var doc = JsonDocument.Parse(json);
            return ToonEncoder.IsTabular(doc.RootElement);
        }

        Assert.True(Tab("""[{"a":1,"b":"x"},{"a":2,"b":"y"}]"""));
        Assert.False(Tab("""[]"""));
        Assert.False(Tab("""[{}]"""));
        Assert.False(Tab("""[{"a":{"n":1}}]"""));
        Assert.False(Tab("""[{"a":[1]}]"""));
        Assert.False(Tab("""[{"a":1},2]"""));
        Assert.False(Tab("""[{"a":1},{"a":1,"b":2}]"""));
    }
}
