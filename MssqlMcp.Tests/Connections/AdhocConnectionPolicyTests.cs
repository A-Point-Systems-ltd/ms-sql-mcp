using Microsoft.Extensions.Logging.Abstractions;
using Mssql.McpServer;
using Mssql.McpServer.Connections;
using Mssql.McpServer.InsightsLayer;

namespace MssqlMcp.Tests.Connections;

[Collection(EnvVarLock.Name)]
public sealed class AdhocConnectionPolicyTests
{
    private const string SqlAuth = "Server=db1;Database=d;User ID=u;Password=p";

    private static Func<string, string?> Env(params (string Key, string Value)[] vars) =>
        key => vars.FirstOrDefault(v => v.Key == key).Value;

    [Theory]
    [InlineData("Server=h;Integrated Security=true")]
    [InlineData("Server=h;Integrated Security=SSPI")]
    [InlineData("Server=h;Trusted_Connection=yes")]
    [InlineData("Server=h;Authentication=ActiveDirectoryIntegrated")]
    [InlineData("Server=h;Authentication=ActiveDirectoryDefault")]
    [InlineData("Server=h;Authentication=ActiveDirectoryManagedIdentity")]
    [InlineData("Server=h;Authentication=Active Directory Password;User ID=u;Password=p")]
    public void Server_identity_auth_is_refused_unless_operator_allows_it(string cs)
    {
        Assert.Contains(AdhocConnectionPolicy.AllowIntegratedAuthVariable, AdhocConnectionPolicy.Validate(cs, true, Env()));
        Assert.Null(AdhocConnectionPolicy.Validate(cs, true, Env((AdhocConnectionPolicy.AllowIntegratedAuthVariable, "true"))));
    }

    [Theory]
    [InlineData(SqlAuth + ";AttachDBFilename=C:\\x.mdf", "AttachDBFilename")]
    [InlineData(SqlAuth + ";User Instance=true", "User Instance")]
    public void Attach_file_and_user_instance_are_always_refused(string cs, string keyword)
    {
        var all = Env((AdhocConnectionPolicy.AllowIntegratedAuthVariable, "true"), (AdhocConnectionPolicy.AllowWriteVariable, "true"));
        Assert.Contains(keyword, AdhocConnectionPolicy.Validate(cs, true, all));
    }

    [Fact]
    public void Unparsable_string_is_refused_without_echo()
    {
        var error = AdhocConnectionPolicy.Validate("Server=h;Password=topsecret;Bogus Keyword=1", true, Env());
        Assert.NotNull(error);
        Assert.DoesNotContain("topsecret", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Writable_requires_operator_switch()
    {
        Assert.Contains(AdhocConnectionPolicy.AllowWriteVariable, AdhocConnectionPolicy.Validate(SqlAuth, false, Env()));
        Assert.Null(AdhocConnectionPolicy.Validate(SqlAuth, false, Env((AdhocConnectionPolicy.AllowWriteVariable, "TRUE"))));
        Assert.Null(AdhocConnectionPolicy.Validate(SqlAuth, true, Env()));
    }

    [Theory]
    [InlineData("db1", true)]
    [InlineData("DB1", true)]
    [InlineData("tcp:db1,1433", true)]
    [InlineData("db1\\INST", true)]
    [InlineData("np:\\\\db1\\pipe\\sql\\query", true)]
    [InlineData("db2", false)]
    [InlineData("db1.evil.com", false)]
    public void Host_allowlist_matches_host_part_case_insensitively(string dataSource, bool allowed)
    {
        var env = Env((AdhocConnectionPolicy.AllowedHostsVariable, " db1 , other "));
        var error = AdhocConnectionPolicy.Validate($"Data Source={dataSource};User ID=u;Password=p", true, env);
        Assert.Equal(allowed, error is null);
    }

    [Fact]
    public void Empty_allowlist_means_no_host_limit() =>
        Assert.Null(AdhocConnectionPolicy.Validate(SqlAuth, true, Env((AdhocConnectionPolicy.AllowedHostsVariable, " , "))));

    [Fact]
    public async Task Refused_open_registers_nothing()
    {
        Environment.SetEnvironmentVariable("MSSQL_ALLOW_ADHOC_CONNECTIONS", "true");
        try
        {
            var reg = new ConnectionRegistry([new("a", "Server=a;User ID=u;Password=p", false, true, ConnectionSource.Configured)]);
            var tools = new Tools(new SqlConnectionFactory(reg), NoOpInsightsLayerService.Instance, NoOpInsightDdlProcessingQueue.Instance, NullLogger<Tools>.Instance, reg);
            foreach (var (cs, ro) in new[] { ("Server=h;Trusted_Connection=True", true), (SqlAuth, false), (SqlAuth + ";User Instance=true", true) })
            {
                var result = await tools.OpenConnection("x", cs, readOnly: ro);
                Assert.False(result.Success);
                Assert.Equal(1, reg.Count);
                Assert.Null(reg.Find("x"));
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("MSSQL_ALLOW_ADHOC_CONNECTIONS", null);
        }
    }

    [Fact]
    public async Task Probe_failure_message_is_generic()
    {
        Environment.SetEnvironmentVariable("MSSQL_ALLOW_ADHOC_CONNECTIONS", "true");
        try
        {
            var reg = new ConnectionRegistry([new("a", "Server=a;User ID=u;Password=p", false, true, ConnectionSource.Configured)]);
            var tools = new Tools(new SqlConnectionFactory(reg), NoOpInsightsLayerService.Instance, NoOpInsightDdlProcessingQueue.Instance, NullLogger<Tools>.Instance, reg);
            var result = await tools.OpenConnection("x", "Server=127.0.0.1,1;Connect Timeout=1;User ID=u;Password=topsecret");
            Assert.False(result.Success);
            Assert.Equal("Connection test failed for 'x'.", result.Error);
            Assert.Null(reg.Find("x"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("MSSQL_ALLOW_ADHOC_CONNECTIONS", null);
        }
    }
}
