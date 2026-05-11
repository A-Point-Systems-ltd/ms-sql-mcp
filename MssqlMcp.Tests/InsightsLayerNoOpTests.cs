// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.

using Mssql.McpServer.InsightsLayer;
using Mssql.McpServer.InsightsLayer.Models;

namespace MssqlMcp.Tests;

public sealed class InsightsLayerNoOpTests
{
    [Fact]
    public async Task NoOp_IsDisabled_AndInstallFails()
    {
        var svc = NoOpInsightsLayerService.Instance;
        Assert.False(svc.IsEnabled);
        var install = await svc.InstallLayerAsync();
        Assert.False(install.Success);
        Assert.NotNull(install.Error);
    }

    [Fact]
    public async Task NoOp_GetInsight_ReturnsLayerDisabled()
    {
        var svc = NoOpInsightsLayerService.Instance;
        var (insight, freshness) = await svc.GetInsightForObjectAsync("Table", "dbo", "X");
        Assert.Null(insight);
        Assert.Equal(InsightFreshness.LayerDisabled, freshness);
    }
}
