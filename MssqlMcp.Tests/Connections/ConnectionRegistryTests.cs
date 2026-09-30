using Mssql.McpServer.Connections;

namespace MssqlMcp.Tests.Connections;

public sealed class ConnectionRegistryTests
{
    private static ConnectionProfile P(string name, bool readOnly = false, ConnectionSource source = ConnectionSource.Configured) =>
        new(name, $"Server=srv-{name};Database=db_{name};Trusted_Connection=True", readOnly, true, source);

    [Fact]
    public void Single_connection_resolves_without_a_name()
    {
        var reg = new ConnectionRegistry([P("only")]);
        Assert.False(reg.ConnectionArgumentRequired);
        Assert.Equal("only", reg.Resolve(null).Name);
        Assert.Equal("only", reg.Resolve("ONLY").Name);
    }

    [Fact]
    public void Two_configured_connections_make_the_name_mandatory_and_list_all()
    {
        var reg = new ConnectionRegistry([P("a"), P("b", readOnly: true)]);
        Assert.True(reg.ConnectionArgumentRequired);

        var ex = Assert.Throws<ConnectionResolutionException>(() => reg.Resolve(null));
        Assert.Contains("'connection' argument is required", ex.Message);
        Assert.Contains("a (srv-a/db_a", ex.Message);
        Assert.Contains("b (srv-b/db_b, read-only", ex.Message);
    }

    [Fact]
    public void Closed_connection_still_counts_so_the_rule_does_not_flip()
    {
        var reg = new ConnectionRegistry([P("a"), P("b")]);
        reg.Close("b");
        Assert.True(reg.ConnectionArgumentRequired);
        Assert.Throws<ConnectionResolutionException>(() => reg.Resolve(null));
        Assert.Equal("a", reg.Resolve("a").Name);
    }

    [Fact]
    public void Adding_an_adhoc_connection_makes_the_name_mandatory_and_closing_it_restores_optional()
    {
        var reg = new ConnectionRegistry([P("main")]);
        reg.Register(P("tmp", source: ConnectionSource.Adhoc));
        Assert.True(reg.ConnectionArgumentRequired);
        Assert.Throws<ConnectionResolutionException>(() => reg.Resolve(null));

        Assert.True(reg.Close("tmp"));
        Assert.Null(reg.Find("tmp"));
        Assert.False(reg.ConnectionArgumentRequired);
        Assert.Equal("main", reg.Resolve(null).Name);
    }

    [Fact]
    public void Unknown_or_closed_name_is_explained()
    {
        var reg = new ConnectionRegistry([P("a"), P("b")]);
        reg.Close("b");
        Assert.Contains("does not exist", Assert.Throws<ConnectionResolutionException>(() => reg.Resolve("zzz")).Message);
        Assert.Contains("is closed", Assert.Throws<ConnectionResolutionException>(() => reg.Resolve("b")).Message);
    }

    [Fact]
    public void Single_connection_that_is_closed_cannot_be_resolved()
    {
        var reg = new ConnectionRegistry([P("only")]);
        reg.Close("only");
        Assert.Throws<ConnectionResolutionException>(() => reg.Resolve(null));
        Assert.Null(reg.TryResolveSingle());
    }

    [Fact]
    public void List_never_exposes_connection_strings()
    {
        var status = Assert.Single(new ConnectionRegistry([P("a")]).List());
        Assert.Equal("srv-a", status.DataSource);
        Assert.Equal("db_a", status.Database);
        Assert.DoesNotContain(typeof(ConnectionStatus).GetProperties(), p => p.Name.Contains("ConnectionString", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Current_connection_flows_across_awaits_and_resets_after_scope()
    {
        var profile = P("x");
        using (CurrentConnection.Use(profile))
        {
            await Task.Yield();
            Assert.Same(profile, CurrentConnection.Value);
        }

        Assert.Null(CurrentConnection.Value);
    }
}
