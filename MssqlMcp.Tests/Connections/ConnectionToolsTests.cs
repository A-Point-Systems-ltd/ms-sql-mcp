using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Mssql.McpServer;
using Mssql.McpServer.Connections;
using Mssql.McpServer.InsightsLayer;

namespace MssqlMcp.Tests.Connections;

public sealed class ConnectionToolsTests
{
    private static (Tools tools, ConnectionRegistry reg) Create(params ConnectionProfile[] profiles)
    {
        var reg = new ConnectionRegistry(profiles);
        var tools = new Tools(new SqlConnectionFactory(reg), NoOpInsightsLayerService.Instance,
            NoOpInsightDdlProcessingQueue.Instance, NullLogger<Tools>.Instance, reg);
        return (tools, reg);
    }

    private static ConnectionProfile P(string n) =>
        new(n, $"Server=s{n};Database=d{n};User ID=u;Password=topsecret", false, true, ConnectionSource.Configured);

    [Fact]
    public void List_connections_reports_rule_and_hides_secrets()
    {
        var (tools, _) = Create(P("a"), P("b"));
        var json = JsonSerializer.Serialize(tools.ListConnections().Data);
        Assert.DoesNotContain("topsecret", json, StringComparison.Ordinal);
        Assert.Contains("\"connectionRequired\":true", json, StringComparison.Ordinal);
        Assert.Contains("\"count\":2", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Single_connection_list_reports_not_required()
    {
        var (tools, _) = Create(P("a"));
        Assert.Contains("\"connectionRequired\":false", JsonSerializer.Serialize(tools.ListConnections().Data), StringComparison.Ordinal);
    }

    [Fact]
    public void Close_last_open_connection_is_refused()
    {
        var (tools, _) = Create(P("a"));
        var result = tools.CloseConnection("a");
        Assert.False(result.Success);
        Assert.Contains("last", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Close_configured_keeps_it_registered()
    {
        var (tools, reg) = Create(P("a"), P("b"));
        Assert.True(tools.CloseConnection("b").Success);
        Assert.False(reg.IsOpen("b"));
        Assert.True(reg.ConnectionArgumentRequired);
    }

    [Fact]
    public async Task Adhoc_open_is_refused_unless_enabled()
    {
        Environment.SetEnvironmentVariable("MSSQL_ALLOW_ADHOC_CONNECTIONS", null);
        var (tools, _) = Create(P("a"));
        var result = await tools.OpenConnection("x", "Server=.;Trusted_Connection=True");
        Assert.False(result.Success);
        Assert.Contains("MSSQL_ALLOW_ADHOC_CONNECTIONS", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Open_unknown_configured_name_without_connection_string_fails()
    {
        var (tools, _) = Create(P("a"));
        Assert.False((await tools.OpenConnection("zzz")).Success);
    }

    [Fact]
    public async Task Adhoc_open_cannot_overwrite_a_configured_connection_or_change_its_source()
    {
        Environment.SetEnvironmentVariable("MSSQL_ALLOW_ADHOC_CONNECTIONS", "true");
        try
        {
            var (tools, reg) = Create(P("a"), P("b"));
            var result = await tools.OpenConnection("A", "Server=evil;Trusted_Connection=True", readOnly: false);
            Assert.False(result.Success);
            Assert.Contains("cannot be redefined", result.Error, StringComparison.Ordinal);
            Assert.Equal(ConnectionSource.Configured, reg.Find("a")!.Source);
            Assert.Contains("topsecret", reg.Find("a")!.ConnectionString, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("MSSQL_ALLOW_ADHOC_CONNECTIONS", null);
        }
    }

    [Theory]
    [InlineData("bad name")]
    [InlineData("x\n")]
    [InlineData("")]
    public async Task Adhoc_open_rejects_invalid_names(string name)
    {
        Environment.SetEnvironmentVariable("MSSQL_ALLOW_ADHOC_CONNECTIONS", "true");
        try
        {
            var (tools, reg) = Create(P("a"));
            var result = await tools.OpenConnection(name, "Server=.;Trusted_Connection=True");
            Assert.False(result.Success);
            Assert.Equal(1, reg.Count);
        }
        finally
        {
            Environment.SetEnvironmentVariable("MSSQL_ALLOW_ADHOC_CONNECTIONS", null);
        }
    }

    [Fact]
    public async Task Failed_adhoc_test_registers_nothing()
    {
        Environment.SetEnvironmentVariable("MSSQL_ALLOW_ADHOC_CONNECTIONS", "true");
        try
        {
            var (tools, reg) = Create(P("a"));
            var result = await tools.OpenConnection("x", "Server=127.0.0.1,1;Connect Timeout=1;Password=topsecret");
            Assert.False(result.Success);
            Assert.Null(reg.Find("x"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("MSSQL_ALLOW_ADHOC_CONNECTIONS", null);
        }
    }
}
