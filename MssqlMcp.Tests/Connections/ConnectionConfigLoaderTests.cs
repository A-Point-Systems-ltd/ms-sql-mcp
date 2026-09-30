// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.

using Mssql.McpServer.Connections;

namespace MssqlMcp.Tests.Connections;

public sealed class ConnectionConfigLoaderTests
{
    private static IReadOnlyList<ConnectionProfile> Load(Dictionary<string, string?> env, Dictionary<string, string>? files = null) =>
        ConnectionConfigLoader.Load(k => env.TryGetValue(k, out var v) ? v : null, p => files![p]);

    [Fact]
    public void Legacy_connection_string_becomes_profile_named_default()
    {
        var p = Assert.Single(Load(new() { ["CONNECTION_STRING"] = "Server=.;Database=a;Trusted_Connection=True" }));
        Assert.Equal("default", p.Name);
        Assert.False(p.ReadOnly);
        Assert.Equal(ConnectionSource.Legacy, p.Source);
    }

    [Fact]
    public void Json_env_defines_multiple_profiles_in_order()
    {
        var profiles = Load(new()
        {
            ["MSSQL_CONNECTIONS"] = """
                [{"name":"prod","connectionString":"Server=p;Database=a;Trusted_Connection=True","readOnly":true},
                 {"name":"dev","connectionString":"Server=d;Database=a;Trusted_Connection=True","insights":false}]
                """,
        });

        Assert.Equal(["prod", "dev"], profiles.Select(p => p.Name));
        Assert.True(profiles[0].ReadOnly);
        Assert.False(profiles[1].InsightsEnabled);
    }

    [Fact]
    public void Legacy_and_json_are_merged()
    {
        var profiles = Load(new()
        {
            ["CONNECTION_STRING"] = "Server=.;Database=a;Trusted_Connection=True",
            ["MSSQL_CONNECTIONS"] = """[{"name":"dev","connectionString":"Server=d;Database=b;Trusted_Connection=True"}]""",
        });

        Assert.Equal(["default", "dev"], profiles.Select(p => p.Name));
    }

    [Fact]
    public void File_config_is_read_and_env_placeholders_are_expanded()
    {
        var profiles = Load(
            new() { ["MSSQL_CONNECTIONS_FILE"] = @"C:\cfg.json", ["SALES_PWD"] = "s3cr;et" },
            new() { [@"C:\cfg.json"] = """[{"name":"sales","connectionString":"Server=s;Database=x;User ID=u;Password=\"${env:SALES_PWD}\""}]""" });

        Assert.Contains("s3cr;et", Assert.Single(profiles).ConnectionString);
    }

    [Theory]
    [InlineData("""[{"name":"","connectionString":"Server=."}]""", "name")]
    [InlineData("""[{"name":"a b","connectionString":"Server=."}]""", "name")]
    [InlineData("""[{"name":"a","connectionString":""}]""", "connectionString")]
    [InlineData("""[{"name":"a","connectionString":"Server=."},{"name":"A","connectionString":"Server=."}]""", "duplicate")]
    [InlineData("""[{"name":"a","connectionString":"Server=.","default":true}]""", "no default connection")]
    [InlineData("""{"name":"a"}""", "array")]
    [InlineData("""[{"name":"a","connectionString":"Password=${env:MISSING}"}]""", "MISSING")]
    [InlineData("""[null]""", "MSSQL_CONNECTIONS")]
    [InlineData("""[{"name":"a","connectionString":"Server=.","read_only":true}]""", "JSON array")]
    public void Invalid_config_throws_with_actionable_message(string json, string expectedFragment)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Load(new() { ["MSSQL_CONNECTIONS"] = json }));
        Assert.Contains(expectedFragment, ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void No_configuration_returns_empty_list()
    {
        Assert.Empty(Load(new()));
    }

    [Fact]
    public void File_read_failure_is_wrapped_with_variable_and_path()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => ConnectionConfigLoader.Load(
            k => k == "MSSQL_CONNECTIONS_FILE" ? @"C:\missing.json" : null,
            p => throw new FileNotFoundException("nope")));
        Assert.Contains("MSSQL_CONNECTIONS_FILE", ex.Message);
        Assert.Contains(@"C:\missing.json", ex.Message);
    }

    [Fact]
    public void Profile_ToString_does_not_leak_password()
    {
        var p = new ConnectionProfile("a", "Server=.;Database=x;User ID=u;Password=topsecret", false, true, ConnectionSource.Configured);
        Assert.DoesNotContain("topsecret", p.ToString(), StringComparison.Ordinal);
        Assert.Contains("***MASKED***", p.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("p;w=d'x\"y z")]
    [InlineData(" lead;trail ")]
    [InlineData("a\"\"b")]
    [InlineData("plain")]
    public void Quoted_env_placeholder_round_trips_any_password(string password)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new[]
        {
            new { name = "q", connectionString = "Data Source=s;Initial Catalog=d;User ID=u;Password=\"${env:PWD_Q}\";Encrypt=True" },
        });
        var p = Assert.Single(Load(new() { ["MSSQL_CONNECTIONS"] = json, ["PWD_Q"] = password }));
        var b = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(p.ConnectionString);
        Assert.Equal(password, b.Password);
        Assert.Equal("s", b.DataSource);
        Assert.True(b.Encrypt == Microsoft.Data.SqlClient.SqlConnectionEncryptOption.Mandatory);
    }

    [Fact]
    public void Unquoted_env_placeholder_is_substituted_raw()
    {
        var p = Assert.Single(Load(new()
        {
            ["MSSQL_CONNECTIONS"] = "[{\"name\":\"r\",\"connectionString\":\"Data Source=${env:SRV};Initial Catalog=d\"}]",
            ["SRV"] = "host\"x",
        }));
        Assert.Equal("Data Source=host\"x;Initial Catalog=d", p.ConnectionString);
    }
}
