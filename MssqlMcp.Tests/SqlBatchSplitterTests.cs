// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.

using Mssql.McpServer.InsightsLayer;

namespace MssqlMcp.Tests;

public sealed class SqlBatchSplitterTests
{
    [Fact]
    public void SplitBatches_SplitsOnGo()
    {
        var sql = "SELECT 1\r\nGO\r\nSELECT 2";
        var batches = SqlBatchSplitter.SplitBatches(sql).ToList();
        Assert.Equal(2, batches.Count);
        Assert.Contains("SELECT 1", batches[0], StringComparison.Ordinal);
        Assert.Contains("SELECT 2", batches[1], StringComparison.Ordinal);
    }

    [Fact]
    public void SplitBatches_IgnoresEmptyTrailingGo()
    {
        var sql = "SELECT 1\r\nGO\r\n";
        var batches = SqlBatchSplitter.SplitBatches(sql).ToList();
        Assert.Single(batches);
    }
}
