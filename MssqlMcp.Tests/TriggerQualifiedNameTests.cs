// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.

using Mssql.McpServer;

namespace MssqlMcp.Tests;

public sealed class TriggerQualifiedNameTests
{
    [Theory]
    [InlineData("trgAudit", "trgAudit", null, null, "trgAudit")]
    [InlineData("dbo.trgAudit", "trgAudit", "dbo", null, "dbo.trgAudit")]
    [InlineData("dbo.Orders.trgAudit", "trgAudit", "dbo", "Orders", "dbo.Orders.trgAudit")]
    [InlineData("  dbo.Orders.trgAudit  ", "trgAudit", "dbo", "Orders", "dbo.Orders.trgAudit")]
    public void Parse_supports_one_two_and_three_part_names(
        string input,
        string expectedName,
        string? expectedSchema,
        string? expectedTable,
        string expectedDisplay)
    {
        var parsed = TriggerQualifiedName.Parse(input);
        Assert.Equal(expectedName, parsed.Name);
        Assert.Equal(expectedSchema, parsed.Schema);
        Assert.Equal(expectedTable, parsed.TableName);
        Assert.Equal(expectedDisplay, parsed.DisplayName);
    }
}
