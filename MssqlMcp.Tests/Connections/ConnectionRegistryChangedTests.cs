using Mssql.McpServer.Connections;

namespace MssqlMcp.Tests.Connections;

/// <summary><see cref="ConnectionRegistry.Changed"/>: raised on close, removal and ad-hoc replacement, never on a plain open.</summary>
public sealed class ConnectionRegistryChangedTests
{
    private static ConnectionProfile P(string name, ConnectionSource source = ConnectionSource.Configured, string server = "srv") =>
        new(name, $"Server={server};Database=db_{name};Trusted_Connection=True", false, false, source);

    private static List<ConnectionChangedEventArgs> Record(ConnectionRegistry reg)
    {
        var events = new List<ConnectionChangedEventArgs>();
        reg.Changed += (_, e) => events.Add(e);
        return events;
    }

    [Fact]
    public void Closing_a_configured_connection_raises_Closed()
    {
        var reg = new ConnectionRegistry([P("a"), P("b")]);
        var events = Record(reg);

        Assert.Equal(CloseResult.Closed, reg.TryClose("b"));

        var e = Assert.Single(events);
        Assert.Equal("b", e.Name);
        Assert.Equal(ConnectionChangeKind.Closed, e.Kind);
    }

    [Fact]
    public void Closing_an_adhoc_connection_raises_Removed()
    {
        var reg = new ConnectionRegistry([P("a")]);
        reg.Register(P("x", ConnectionSource.Adhoc));
        var events = Record(reg);

        Assert.True(reg.Close("x"));

        var e = Assert.Single(events);
        Assert.Equal("x", e.Name);
        Assert.Equal(ConnectionChangeKind.Removed, e.Kind);
    }

    [Fact]
    public void Replacing_an_adhoc_connection_raises_Replaced_but_a_first_open_or_reopen_raises_nothing()
    {
        var reg = new ConnectionRegistry([P("a")]);
        var events = Record(reg);

        reg.Register(P("x", ConnectionSource.Adhoc));
        Assert.Empty(events);

        reg.Register(P("x", ConnectionSource.Adhoc, server: "other"));
        var e = Assert.Single(events);
        Assert.Equal("x", e.Name);
        Assert.Equal(ConnectionChangeKind.Replaced, e.Kind);

        events.Clear();
        reg.Close("a");
        events.Clear();
        reg.Register(P("a"));
        Assert.Empty(events);
    }

    [Fact]
    public void Refused_or_unknown_closes_raise_nothing()
    {
        var reg = new ConnectionRegistry([P("a")]);
        var events = Record(reg);

        Assert.Equal(CloseResult.LastOpen, reg.TryClose("a"));
        Assert.Equal(CloseResult.NotFound, reg.TryClose("nope"));

        Assert.Empty(events);
    }

    [Fact]
    public void A_throwing_handler_does_not_break_the_registry()
    {
        var reg = new ConnectionRegistry([P("a"), P("b")]);
        reg.Changed += (_, _) => throw new InvalidOperationException("boom");

        Assert.Equal(CloseResult.Closed, reg.TryClose("b"));
        Assert.False(reg.IsOpen("b"));
    }
}
