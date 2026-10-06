using Microsoft.Data.SqlClient;
using Mssql.McpServer.Connections.Managed;

namespace MssqlMcp.Tests.Connections.Managed;

/// <summary>Same rules as the VS Code form (vscode-extension/test/connectionForm.test.mjs, profile.test.mjs).</summary>
public sealed class ManagedConnectionRulesTests
{
    private static ManagedConnectionInput Win(string name = "dev") =>
        new() { Name = name, Auth = ManagedAuth.Windows, Server = "srv", Database = "db" };

    [Fact]
    public void Valid_windows_input_has_no_errors() => Assert.Empty(ManagedConnectionRules.Validate(Win(), false));

    [Theory]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("semi;colon")]
    public void Invalid_name_is_rejected(string name) =>
        Assert.True(ManagedConnectionRules.Validate(Win(name), false).ContainsKey("name"));

    [Fact]
    public void Server_and_database_are_required()
    {
        var errors = ManagedConnectionRules.Validate(Win() with { Server = " ", Database = "" }, false);
        Assert.Equal("Server is required.", errors["server"]);
        Assert.Equal("Database is required.", errors["database"]);
    }

    [Fact]
    public void Sql_login_needs_user_and_password_unless_one_is_saved()
    {
        var input = Win() with { Auth = ManagedAuth.Sql };
        var errors = ManagedConnectionRules.Validate(input, false);
        Assert.True(errors.ContainsKey("user"));
        Assert.True(errors.ContainsKey("password"));

        Assert.Empty(ManagedConnectionRules.Validate(input with { User = "sa" }, hasSavedPassword: true));
    }

    [Fact]
    public void Entra_interactive_needs_user()
    {
        Assert.True(ManagedConnectionRules.Validate(Win() with { Auth = ManagedAuth.EntraInteractive }, false).ContainsKey("user"));
    }

    [Fact]
    public void Env_placeholders_are_rejected_everywhere()
    {
        Assert.True(ManagedConnectionRules.Validate(Win() with { Server = "${env:X}" }, false).ContainsKey("server"));
        Assert.True(ManagedConnectionRules.Validate(new() { Name = "r", Auth = ManagedAuth.Raw, RawConnectionString = "Server=${ENV:X}" }, false)
            .ContainsKey("rawConnectionString"));
    }

    [Theory]
    [InlineData("Server=s;Database=d;Password=x")]
    [InlineData("Server=s;Database=d;PWD = x")]
    public void Raw_string_must_not_contain_a_password(string raw) =>
        Assert.True(ManagedConnectionRules.Validate(new() { Name = "r", Auth = ManagedAuth.Raw, RawConnectionString = raw }, false)
            .ContainsKey("rawConnectionString"));

    [Fact]
    public void Raw_string_must_parse() =>
        Assert.True(ManagedConnectionRules.Validate(new() { Name = "r", Auth = ManagedAuth.Raw, RawConnectionString = "NotAKey=1" }, false)
            .ContainsKey("rawConnectionString"));

    [Fact]
    public void Unknown_auth_and_encrypt_are_rejected()
    {
        Assert.True(ManagedConnectionRules.Validate(Win() with { Auth = "kerberos" }, false).ContainsKey("auth"));
        Assert.True(ManagedConnectionRules.Validate(Win() with { Encrypt = "maybe" }, false).ContainsKey("encrypt"));
    }

    [Fact]
    public void Windows_connection_string()
    {
        var entry = ManagedConnectionRules.ToEntry(Win() with { Encrypt = ManagedEncrypt.Mandatory, TrustServerCertificate = false }, null);
        var b = new SqlConnectionStringBuilder(ManagedConnectionRules.BuildConnectionString(entry, null));
        Assert.Equal("srv", b.DataSource);
        Assert.Equal("db", b.InitialCatalog);
        Assert.True(b.IntegratedSecurity);
        Assert.Equal(SqlConnectionEncryptOption.Mandatory, b.Encrypt);
        Assert.False(b.TrustServerCertificate);
        Assert.Equal("APoint-ms-sql", b.ApplicationName);
        Assert.Equal(ApplicationIntent.ReadOnly, b.ApplicationIntent);
    }

    [Fact]
    public void Sql_connection_string_quotes_special_characters_in_the_password()
    {
        var entry = ManagedConnectionRules.ToEntry(Win() with { Auth = ManagedAuth.Sql, User = "sa", ReadOnly = false }, "blob");
        var b = new SqlConnectionStringBuilder(ManagedConnectionRules.BuildConnectionString(entry, "p;a\"ss='"));
        Assert.Equal("sa", b.UserID);
        Assert.Equal("p;a\"ss='", b.Password);
        Assert.False(b.IntegratedSecurity);
        Assert.Equal(ApplicationIntent.ReadWrite, b.ApplicationIntent);
    }

    [Fact]
    public void Entra_connection_strings()
    {
        var interactive = ManagedConnectionRules.ToEntry(Win() with { Auth = ManagedAuth.EntraInteractive, User = "a@b.c" }, null);
        Assert.Equal(SqlAuthenticationMethod.ActiveDirectoryInteractive,
            new SqlConnectionStringBuilder(ManagedConnectionRules.BuildConnectionString(interactive, null)).Authentication);

        var dflt = ManagedConnectionRules.ToEntry(Win() with { Auth = ManagedAuth.EntraDefault }, null);
        Assert.Equal(SqlAuthenticationMethod.ActiveDirectoryDefault,
            new SqlConnectionStringBuilder(ManagedConnectionRules.BuildConnectionString(dflt, null)).Authentication);
    }

    [Fact]
    public void ToEntry_drops_fields_that_do_not_apply_to_the_auth()
    {
        var entry = ManagedConnectionRules.ToEntry(Win() with { User = "ignored", RawConnectionString = "ignored" }, "ignored");
        Assert.Null(entry.User);
        Assert.Null(entry.PasswordProtected);
        Assert.Null(entry.RawConnectionString);

        var raw = ManagedConnectionRules.ToEntry(new() { Name = "r", Auth = ManagedAuth.Raw, Server = "x", RawConnectionString = " Server=s " }, null);
        Assert.Equal("", raw.Server);
        Assert.Equal("Server=s", raw.RawConnectionString);
    }

    [Fact]
    public void Database_override_applies_to_raw_and_structured()
    {
        var raw = ManagedConnectionRules.ToEntry(new() { Name = "r", Auth = ManagedAuth.Raw, RawConnectionString = "Server=s;Database=app" }, null);
        Assert.Equal("master", new SqlConnectionStringBuilder(ManagedConnectionRules.BuildConnectionString(raw, null, "master")).InitialCatalog);
        Assert.Equal("master", new SqlConnectionStringBuilder(ManagedConnectionRules.BuildConnectionString(ManagedConnectionRules.ToEntry(Win(), null), null, "master")).InitialCatalog);
    }
}
