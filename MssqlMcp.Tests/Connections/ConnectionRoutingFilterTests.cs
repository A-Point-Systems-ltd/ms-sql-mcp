using Mssql.McpServer;
using Mssql.McpServer.Connections;

namespace MssqlMcp.Tests.Connections;

public sealed class ConnectionRoutingFilterTests
{
    private static ConnectionRegistry Multi() => new(
    [
        new("rw", "Server=a;Database=x;Trusted_Connection=True", false, true, ConnectionSource.Configured),
        new("ro", "Server=b;Database=y;Trusted_Connection=True", true, true, ConnectionSource.Configured),
    ]);

    [Fact]
    public void Single_connection_routes_without_argument()
    {
        var reg = new ConnectionRegistry([new("only", "Server=a;Trusted_Connection=True", false, true, ConnectionSource.Legacy)]);
        Assert.Null(ConnectionRoutingFilter.Route(reg, ToolNames.ReadData, null, out var p));
        Assert.Equal("only", p!.Name);
    }

    [Fact]
    public void Missing_argument_with_multiple_connections_is_an_error_listing_names()
    {
        var error = ConnectionRoutingFilter.Route(Multi(), ToolNames.ReadData, null, out var p);
        Assert.Null(p);
        Assert.Contains("'connection' argument is required", error);
        Assert.Contains("- ro (b/y, read-only)", error);
        Assert.Contains("- rw (a/x)", error);
    }

    [Theory]
    [InlineData(ToolNames.ExecuteSql)]
    [InlineData(ToolNames.InsertData)]
    [InlineData(ToolNames.UpdateData)]
    [InlineData(ToolNames.CreateTable)]
    [InlineData(ToolNames.DropTable)]
    [InlineData(ToolNames.UpsertInsight)]
    [InlineData(ToolNames.InstallInsightsLayer)]
    [InlineData(ToolNames.RefreshInsights)]
    [InlineData(ToolNames.RebuildBaselineInsights)]
    public void Write_tools_are_refused_on_read_only_connection(string tool)
    {
        var error = ConnectionRoutingFilter.Route(Multi(), tool, "ro", out _);
        Assert.Contains("read-only", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Read_tools_are_allowed_on_read_only_connection()
    {
        Assert.Null(ConnectionRoutingFilter.Route(Multi(), ToolNames.ReadData, "ro", out var p));
        Assert.Equal("ro", p!.Name);
    }

    [Fact]
    public void Unknown_connection_returns_error_not_exception()
    {
        Assert.Contains("does not exist", ConnectionRoutingFilter.Route(Multi(), ToolNames.ReadData, "nope", out _));
    }
}
