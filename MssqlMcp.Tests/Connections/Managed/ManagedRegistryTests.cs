using Mssql.McpServer.Connections;
using Mssql.McpServer.Connections.Managed;

namespace MssqlMcp.Tests.Connections.Managed;

public sealed class ManagedRegistryTests
{
    private static ConnectionProfile P(string name, ConnectionSource source, string db = "db") =>
        new(name, $"Server=srv;Database={db};Integrated Security=True", false, true, source);

    [Fact]
    public void UpsertManaged_refuses_other_sources_and_rejects_non_managed_profiles()
    {
        var reg = new ConnectionRegistry([P("cfg", ConnectionSource.Configured)]);
        Assert.False(reg.UpsertManaged(P("CFG", ConnectionSource.Managed)));
        Assert.Equal(ConnectionSource.Configured, reg.Find("cfg")!.Source);
        Assert.Throws<ArgumentException>(() => reg.UpsertManaged(P("x", ConnectionSource.Adhoc)));
    }

    [Fact]
    public void Unchanged_upsert_raises_nothing()
    {
        var reg = new ConnectionRegistry([]);
        Assert.True(reg.UpsertManaged(P("m", ConnectionSource.Managed)));
        var raised = 0;
        reg.Changed += (_, _) => raised++;
        Assert.True(reg.UpsertManaged(P("m", ConnectionSource.Managed)));
        Assert.Equal(0, raised);
    }

    [Fact]
    public void RemoveManaged_only_removes_managed_and_may_remove_the_last_connection()
    {
        var reg = new ConnectionRegistry([P("cfg", ConnectionSource.Configured)]);
        reg.UpsertManaged(P("m", ConnectionSource.Managed));
        Assert.False(reg.RemoveManaged("cfg"));
        Assert.True(reg.RemoveManaged("M"));
        Assert.Equal(["cfg"], reg.List().Select(s => s.Name));

        var only = new ConnectionRegistry([]);
        only.UpsertManaged(P("m", ConnectionSource.Managed));
        Assert.True(only.RemoveManaged("m"));
        Assert.Equal(0, only.Count);
    }

    [Fact]
    public void A_managed_connection_can_be_closed_and_stays_listed()
    {
        var reg = new ConnectionRegistry([P("cfg", ConnectionSource.Configured)]);
        reg.UpsertManaged(P("m", ConnectionSource.Managed));
        Assert.Equal(CloseResult.Closed, reg.TryClose("m"));
        Assert.False(reg.IsOpen("m"));
        Assert.Equal(["m"], reg.ManagedNames());
    }

    [Fact]
    public void Store_rejects_a_file_from_a_newer_version()
    {
        var path = Path.Combine(Path.GetTempPath(), "mssqlmcp-v-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, """{"version":99,"connections":[]}""");
            Assert.Throws<ManagedConnectionFileException>(() => new ManagedConnectionStore(path).Load());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Dpapi_round_trip_and_garbage_returns_null()
    {
        var p = new DpapiSecretProtector();
        var blob = p.Protect("pa;ss");
        Assert.DoesNotContain("pa;ss", blob, StringComparison.Ordinal);
        Assert.Equal("pa;ss", p.TryUnprotect(blob));
        Assert.Null(p.TryUnprotect("not base64!"));
        Assert.Null(p.TryUnprotect(Convert.ToBase64String([1, 2, 3])));
    }
}
