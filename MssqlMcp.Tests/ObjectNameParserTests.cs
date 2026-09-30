// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.

using Mssql.McpServer;

namespace MssqlMcp.Tests;

public sealed class ObjectNameParserTests
{
    [Theory]
    [InlineData("Orders", null, null, "Orders")]
    [InlineData("  Orders  ", null, null, "Orders")]
    [InlineData("dbo.Orders", null, "dbo", "Orders")]
    [InlineData(" dbo . Orders ", null, "dbo", "Orders")]
    [InlineData("[dbo].[Orders]", null, "dbo", "Orders")]
    [InlineData("[dbo].[My.Table]", null, "dbo", "My.Table")]
    [InlineData("[s2].Dup", null, "s2", "Dup")]
    [InlineData("\"x\"", null, null, "x")]
    [InlineData("\"sales\".\"Order \"\"Q\"\"\"", null, "sales", "Order \"Q\"")]
    [InlineData("[a]]b]", null, null, "a]b")]
    [InlineData("[ spaced name ]", null, null, " spaced name ")]
    [InlineData("db.dbo.t", "db", "dbo", "t")]
    [InlineData("[My DB].[dbo].[t]", "My DB", "dbo", "t")]
    public void TryParse_accepts_valid_names(string input, string? expectedDb, string? expectedSchema, string expectedName)
    {
        var ok = ObjectNameParser.TryParse(input, out var database, out var schema, out var name, out var error);

        Assert.True(ok, error);
        Assert.Null(error);
        Assert.Equal(expectedDb, database);
        Assert.Equal(expectedSchema, schema);
        Assert.Equal(expectedName, name);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a..b")]
    [InlineData(".a")]
    [InlineData("a.")]
    [InlineData("a.b.c.d")]
    [InlineData("[a].[b].[c].[d]")]
    [InlineData("[unterminated")]
    [InlineData("\"unterminated")]
    [InlineData("[a]x")]
    [InlineData("a]b")]
    [InlineData("[]")]
    [InlineData("dbo.[]")]
    public void TryParse_rejects_invalid_names(string input)
    {
        var ok = ObjectNameParser.TryParse(input, out _, out _, out _, out var error);

        Assert.False(ok);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void TryParse_rejects_null()
    {
        Assert.False(ObjectNameParser.TryParse(null, out _, out _, out _, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void TryParse_rejects_part_longer_than_sysname()
    {
        Assert.False(ObjectNameParser.TryParse(new string('x', 129), out _, out _, out _, out _));
        Assert.True(ObjectNameParser.TryParse(new string('x', 128), out _, out _, out _, out _));
    }

    [Theory]
    [InlineData("trgAudit", null, null, "trgAudit")]
    [InlineData("dbo.trgAudit", "dbo", null, "trgAudit")]
    [InlineData("dbo.Orders.trgAudit", "dbo", "Orders", "trgAudit")]
    [InlineData("[dbo].[Order.Lines].[trg.Audit]", "dbo", "Order.Lines", "trg.Audit")]
    public void TryParseTrigger_reads_three_parts_as_schema_table_name(string input, string? expectedSchema, string? expectedTable, string expectedName)
    {
        var ok = ObjectNameParser.TryParseTrigger(input, out var parts, out var error);

        Assert.True(ok, error);
        Assert.Null(parts.Database);
        Assert.Equal(expectedSchema, parts.Schema);
        Assert.Equal(expectedTable, parts.ParentName);
        Assert.Equal(expectedName, parts.Name);
    }

    [Theory]
    [InlineData("a.b.c.d")]
    [InlineData("dbo..trg")]
    public void TryParseTrigger_rejects_invalid_names(string input)
    {
        Assert.False(ObjectNameParser.TryParseTrigger(input, out _, out var error));
        Assert.NotNull(error);
    }
}
