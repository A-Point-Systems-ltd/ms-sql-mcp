using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Mssql.McpServer.Connections;
using Mssql.McpServer.InsightsLayer;

namespace MssqlMcp.Tests.Connections;

public sealed class InsightsQueueRoutingTests
{
    [Fact]
    public async Task Each_connection_that_wrote_is_processed_under_its_own_scope()
    {
        var reg = new ConnectionRegistry(
        [
            new("a", "Server=a", false, true, ConnectionSource.Configured),
            new("b", "Server=b", false, true, ConnectionSource.Configured),
        ]);
        var seen = new System.Collections.Concurrent.ConcurrentQueue<string?>();
        var svc = new Mock<IInsightsLayerService>();
        svc.SetupGet(s => s.IsEnabled).Returns(true);
        svc.Setup(s => s.ProcessDdlChangesAsync(It.IsAny<CancellationToken>()))
            .Callback(() => seen.Enqueue(CurrentConnection.Value?.Name))
            .ReturnsAsync(true);
        var queue = new InsightDdlProcessingQueue(svc.Object, NullLogger<InsightDdlProcessingQueue>.Instance, reg);
        await queue.StartAsync(CancellationToken.None);

        using (CurrentConnection.Use(reg.Find("b")!)) { queue.RequestProcessing(); }
        using (CurrentConnection.Use(reg.Find("a")!)) { queue.RequestProcessing(); }
        queue.RequestProcessing(); // no scope + 2 connections: must be ignored, never guessed

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (seen.Count < 2 && DateTime.UtcNow < deadline) { await Task.Delay(20); }
        await Task.Delay(200);
        await queue.StopAsync(CancellationToken.None);

        Assert.Equal(new[] { "a", "b" }, seen.OrderBy(n => n).ToArray());
    }

    [Fact]
    public async Task Unscoped_request_with_a_single_connection_resolves_to_it()
    {
        var reg = new ConnectionRegistry([new("only", "Server=a", false, true, ConnectionSource.Configured)]);
        var seen = new System.Collections.Concurrent.ConcurrentQueue<string?>();
        var svc = new Mock<IInsightsLayerService>();
        svc.SetupGet(s => s.IsEnabled).Returns(true);
        svc.Setup(s => s.ProcessDdlChangesAsync(It.IsAny<CancellationToken>()))
            .Callback(() => seen.Enqueue(CurrentConnection.Value?.Name))
            .ReturnsAsync(true);
        var queue = new InsightDdlProcessingQueue(svc.Object, NullLogger<InsightDdlProcessingQueue>.Instance, reg);
        await queue.StartAsync(CancellationToken.None);

        queue.RequestProcessing();

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (seen.IsEmpty && DateTime.UtcNow < deadline) { await Task.Delay(20); }
        await queue.StopAsync(CancellationToken.None);

        Assert.Equal(new[] { "only" }, seen.ToArray());
    }

    [Fact]
    public async Task Connection_with_insights_disabled_is_not_processed()
    {
        var reg = new ConnectionRegistry(
        [
            new("off", "Server=a", false, false, ConnectionSource.Configured),
            new("on", "Server=b", false, true, ConnectionSource.Configured),
        ]);
        var seen = new System.Collections.Concurrent.ConcurrentQueue<string?>();
        var svc = new Mock<IInsightsLayerService>();
        svc.SetupGet(s => s.IsEnabled).Returns(true);
        svc.Setup(s => s.ProcessDdlChangesAsync(It.IsAny<CancellationToken>()))
            .Callback(() => seen.Enqueue(CurrentConnection.Value?.Name))
            .ReturnsAsync(true);
        var queue = new InsightDdlProcessingQueue(svc.Object, NullLogger<InsightDdlProcessingQueue>.Instance, reg);
        await queue.StartAsync(CancellationToken.None);

        using (CurrentConnection.Use(reg.Find("off")!)) { queue.RequestProcessing(); }
        using (CurrentConnection.Use(reg.Find("on")!)) { queue.RequestProcessing(); }

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (seen.IsEmpty && DateTime.UtcNow < deadline) { await Task.Delay(20); }
        await Task.Delay(200);
        await queue.StopAsync(CancellationToken.None);

        Assert.Equal(new[] { "on" }, seen.ToArray());
    }
}
