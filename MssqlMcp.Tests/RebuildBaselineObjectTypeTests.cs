// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.

using Mssql.McpServer;

namespace MssqlMcp.Tests;

public sealed class RebuildBaselineObjectTypeTests
{
    [Theory]
    [InlineData("Table", "Table")]
    [InlineData("tables", "Table")]
    [InlineData("  VIEW  ", "View")]
    [InlineData("StoredProcedure", "Procedure")]
    [InlineData("proc", "Procedure")]
    [InlineData("scalar_function", "Function")]
    [InlineData("table-function", "Function")]
    public void Supported_types_map_to_scan_labels(string input, string expected)
    {
        Assert.Equal(expected, Tools.NormalizeBaselineObjectType(input));
    }

    [Theory]
    [InlineData("Trigger")]
    [InlineData("SysObject")]
    [InlineData("NotARealType")]
    public void Unsupported_types_return_null(string input)
    {
        Assert.Null(Tools.NormalizeBaselineObjectType(input));
    }
}
