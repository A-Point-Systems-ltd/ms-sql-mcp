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
    public void Register_never_replaces_a_configured_profile_or_changes_its_source()
    {
        var reg = new ConnectionRegistry([P("a"), P("b")]);
        reg.Close("a");
        var stored = reg.Register(new ConnectionProfile("A", "Server=evil", false, false, ConnectionSource.Adhoc));

        Assert.Equal(ConnectionSource.Configured, stored.Source);
        Assert.Equal("srv-a", reg.List().Single(s => s.Name == "a").DataSource);
        Assert.Equal(ConnectionSource.Configured, reg.Find("a")!.Source);
        Assert.True(reg.IsOpen("a"));
        Assert.Equal(2, reg.Count);
    }

    [Fact]
    public void Close_returns_true_even_when_the_connection_string_is_malformed()
    {
        var reg = new ConnectionRegistry([P("a"), new ConnectionProfile("bad", "this is ;; not = a connection string", false, false, ConnectionSource.Adhoc)]);
        Assert.True(reg.Close("bad"));
        Assert.Null(reg.Find("bad"));

        var reg2 = new ConnectionRegistry([P("a"), new ConnectionProfile("bad", "this is ;; not = a connection string", false, false, ConnectionSource.Configured)]);
        Assert.True(reg2.Close("bad"));
        Assert.False(reg2.IsOpen("bad"));
    }

    [Fact]
    public void TryClose_reports_each_outcome()
    {
        var reg = new ConnectionRegistry([P("a"), P("b")]);
        Assert.Equal(CloseResult.NotFound, reg.TryClose("zzz"));
        Assert.Equal(CloseResult.Closed, reg.TryClose("b"));
        Assert.Equal(CloseResult.NotOpen, reg.TryClose("b"));
        Assert.Equal(CloseResult.LastOpen, reg.TryClose("a"));
        Assert.True(reg.IsOpen("a"));
    }

    [Fact]
    public async Task Concurrent_TryClose_of_two_open_connections_closes_exactly_one()
    {
        for (var round = 0; round < 200; round++)
        {
            var reg = new ConnectionRegistry([P("a"), P("b")]);
            using var gate = new ManualResetEventSlim();
            var tasks = new[] { "a", "b" }.Select(n => Task.Run(() => { gate.Wait(); return reg.TryClose(n); })).ToArray();
            gate.Set();
            var results = await Task.WhenAll(tasks);

            Assert.Single(results, CloseResult.Closed);
            Assert.Single(results, CloseResult.LastOpen);
            Assert.Equal(1, reg.List().Count(s => s.IsOpen));
        }
    }

    [Fact]
    public void Register_returns_the_status_of_the_stored_profile()
    {
        var reg = new ConnectionRegistry([P("a")]);
        var status = reg.Register(P("t", source: ConnectionSource.Adhoc));
        Assert.Equal("t", status.Name);
        Assert.True(status.IsOpen);
        Assert.Equal("srv-t", status.DataSource);
    }

    [Fact]
    public void Empty_registry_resolve_and_try_resolve_single_do_not_throw_unexpectedly()
    {
        var reg = new ConnectionRegistry([]);
        Assert.Null(reg.TryResolveSingle());
        Assert.Contains("No connection is configured", Assert.Throws<ConnectionResolutionException>(() => reg.Resolve(null)).Message);
        Assert.Empty(reg.List());
    }

    [Fact]
    public void Concurrent_register_close_resolve_and_list_stay_consistent()
    {
        var reg = new ConnectionRegistry([P("main")]);
        var errors = new System.Collections.Concurrent.ConcurrentQueue<Exception>();

        Parallel.For(0, 400, i =>
        {
            var name = $"t{i % 8}";
            try
            {
                switch (i % 4)
                {
                    case 0:
                        _ = reg.Register(P(name) with { Source = ConnectionSource.Adhoc });
                        break;
                    case 1:
                        _ = reg.Close(name);
                        break;
                    case 2:
                        _ = reg.List();
                        _ = reg.DescribeAll();
                        _ = reg.TryResolveSingle();
                        break;
                    default:
                        try
                        {
                            _ = reg.Resolve(i % 8 == 3 ? null : "main");
                        }
                        catch (ConnectionResolutionException)
                        {
                            // expected while several connections are registered
                        }

                        break;
                }
            }
            catch (Exception ex)
            {
                errors.Enqueue(ex);
            }
        });

        Assert.Empty(errors);
        Assert.Equal(reg.Count, reg.List().Count);
        Assert.True(reg.IsOpen("main"));
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
