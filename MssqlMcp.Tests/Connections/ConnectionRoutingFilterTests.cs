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

    [Theory]
    [InlineData("no_such_tool", false)]
    [InlineData("", false)]
    [InlineData("READ_DATA", false)]
    [InlineData(ToolNames.ListConnections, false)]
    [InlineData(ToolNames.OpenConnection, false)]
    [InlineData(ToolNames.CloseConnection, false)]
    [InlineData(ToolNames.ReadData, true)]
    [InlineData(ToolNames.ExecuteSql, true)]
    [InlineData(ToolNames.RunScript, false)]
    [InlineData(ToolNames.DdlHistory, false)]
    [InlineData(ToolNames.LanguageService, false)]
    public void Only_known_data_tools_are_routed(string tool, bool routed) =>
        Assert.Equal(routed, ConnectionRoutingFilter.IsRouted(tool));

    [Fact]
    public void Run_script_is_routed_only_when_the_script_runner_is_enabled()
    {
        Assert.False(ConnectionRoutingFilter.IsRouted(ToolNames.RunScript, scriptRunnerEnabled: false));
        Assert.True(ConnectionRoutingFilter.IsRouted(ToolNames.RunScript, scriptRunnerEnabled: true));
        Assert.False(ConnectionRoutingFilter.IsRouted(ToolNames.DdlHistory, scriptRunnerEnabled: false));
        Assert.True(ConnectionRoutingFilter.IsRouted(ToolNames.DdlHistory, scriptRunnerEnabled: true));
        Assert.False(ConnectionRoutingFilter.IsRouted(ToolNames.LanguageService, scriptRunnerEnabled: false));
        Assert.True(ConnectionRoutingFilter.IsRouted(ToolNames.LanguageService, scriptRunnerEnabled: true));
        Assert.True(ConnectionRoutingFilter.IsRouted(ToolNames.ReadData, scriptRunnerEnabled: true));
        Assert.False(ConnectionRoutingFilter.IsRouted(ToolNames.ListConnections, scriptRunnerEnabled: true));
    }

    [Fact]
    public void Probe_tools_are_routed_only_when_enabled_and_never_count_as_write_tools()
    {
        foreach (var tool in ToolNames.ProbeOnlyTools)
        {
            Assert.False(ConnectionRoutingFilter.IsRouted(tool), tool);
            Assert.False(ConnectionRoutingFilter.IsRouted(tool, scriptRunnerEnabled: true), tool);
            Assert.True(ConnectionRoutingFilter.IsRouted(tool, probeToolsEnabled: true), tool);
            Assert.DoesNotContain(tool, ToolNames.All);
            Assert.DoesNotContain(tool, ToolNames.WriteTools);
        }

        // A read-only probe profile may still bring a database online: the form asked the person, not the model.
        Assert.Null(ConnectionRoutingFilter.Route(Multi(), ToolNames.ProbeBringOnline, "ro", out var p));
        Assert.NotNull(p);
    }

    [Fact]
    public void Every_non_management_tool_is_routed() =>
        Assert.All(ToolNames.All.Where(t => !ToolNames.ConnectionManagementTools.Contains(t)), t => Assert.True(ConnectionRoutingFilter.IsRouted(t), t));

    [Fact]
    public void Run_script_is_not_an_agent_tool_and_handles_read_only_itself()
    {
        Assert.Equal(23, ToolNames.All.Count);
        Assert.DoesNotContain(ToolNames.RunScript, ToolNames.All);
        Assert.DoesNotContain(ToolNames.RunScript, ToolNames.WriteTools);
        Assert.Null(ConnectionRoutingFilter.Route(Multi(), ToolNames.RunScript, "ro", out var p));
        Assert.True(p!.ReadOnly);
    }

    [Fact]
    public void Ddl_history_is_an_extension_only_tool_that_handles_read_only_itself()
    {
        Assert.Contains(ToolNames.DdlHistory, ToolNames.ExtensionOnlyTools);
        Assert.DoesNotContain(ToolNames.DdlHistory, ToolNames.All);
        Assert.DoesNotContain(ToolNames.DdlHistory, ToolNames.WriteTools);
        Assert.Null(ConnectionRoutingFilter.Route(Multi(), ToolNames.DdlHistory, "ro", out var p));
        Assert.True(p!.ReadOnly);
    }

    [Fact]
    public void Language_service_is_an_extension_only_tool_allowed_on_read_only_profiles()
    {
        Assert.Contains(ToolNames.LanguageService, ToolNames.ExtensionOnlyTools);
        Assert.DoesNotContain(ToolNames.LanguageService, ToolNames.All);
        Assert.DoesNotContain(ToolNames.LanguageService, ToolNames.WriteTools);
        Assert.Null(ConnectionRoutingFilter.Route(Multi(), ToolNames.LanguageService, "ro", out var p));
        Assert.True(p!.ReadOnly);
    }

    [Fact]
    public void Read_tools_are_allowed_on_read_only_connection()
    {
        Assert.Null(ConnectionRoutingFilter.Route(Multi(), ToolNames.ReadData, "ro", out var p));
        Assert.Equal("ro", p!.Name);
    }

    [Theory]
    [InlineData("123")]
    [InlineData("true")]
    [InlineData("{\"a\":1}")]
    [InlineData("[\"rw\"]")]
    public void Non_string_connection_argument_is_an_error(string json)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var error = ConnectionRoutingFilter.ReadConnectionArgument(doc.RootElement.Clone(), out var value);
        Assert.Equal("'connection' must be a string (a name from list_connections).", error);
        Assert.Null(value);
    }

    [Fact]
    public void String_null_and_absent_connection_argument_are_accepted()
    {
        using var str = System.Text.Json.JsonDocument.Parse("\"rw\"");
        Assert.Null(ConnectionRoutingFilter.ReadConnectionArgument(str.RootElement.Clone(), out var value));
        Assert.Equal("rw", value);

        using var nul = System.Text.Json.JsonDocument.Parse("null");
        Assert.Null(ConnectionRoutingFilter.ReadConnectionArgument(nul.RootElement.Clone(), out value));
        Assert.Null(value);

        Assert.Null(ConnectionRoutingFilter.ReadConnectionArgument(null, out value));
        Assert.Null(value);
    }

    [Fact]
    public void Unknown_connection_returns_error_not_exception()
    {
        Assert.Contains("does not exist", ConnectionRoutingFilter.Route(Multi(), ToolNames.ReadData, "nope", out _));
    }
}
