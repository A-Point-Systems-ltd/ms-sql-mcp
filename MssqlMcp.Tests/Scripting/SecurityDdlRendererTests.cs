// MssqlMcp.Tests/Scripting/SecurityDdlRendererTests.cs
using Mssql.McpServer.Scripting;

namespace MssqlMcp.Tests.Scripting;

public sealed class SecurityDdlRendererTests
{
    private static readonly SqlServerVersion V2008 = SqlServerVersion.Parse("10.50.6560.0");
    private static readonly SqlServerVersion V2019 = SqlServerVersion.Parse("15.0.2000.5");

    [Fact]
    public void Sql_login_never_contains_a_password_and_uses_placeholder()
    {
        var ddl = SecurityDdlRenderer.RenderLogin(new("app", 'S', true, "Sales", "us_english", true, false, ["dbcreator"]), V2019, out _);
        Assert.Contains("CREATE LOGIN [app] WITH PASSWORD = " + SecurityDdlRenderer.PasswordPlaceholder, ddl);
        Assert.Contains("DEFAULT_DATABASE = [Sales]", ddl);
        Assert.Contains("CHECK_POLICY = ON, CHECK_EXPIRATION = OFF", ddl);
        Assert.Contains("ALTER LOGIN [app] DISABLE;", ddl);
        Assert.Contains("ALTER SERVER ROLE [dbcreator] ADD MEMBER [app];", ddl);
        Assert.DoesNotContain("SID =", ddl, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(" SID", ddl, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("HASHED", ddl, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Windows_login_on_2008R2_uses_sp_addsrvrolemember()
    {
        var ddl = SecurityDdlRenderer.RenderLogin(new(@"CORP\dana", 'U', false, "master", null, null, null, ["sysadmin"]), V2008, out _);
        Assert.StartsWith(@"CREATE LOGIN [CORP\dana] FROM WINDOWS WITH DEFAULT_DATABASE = [master];", ddl);
        Assert.Contains(@"EXEC sys.sp_addsrvrolemember @loginame = N'CORP\dana', @rolename = N'sysadmin';", ddl);
    }

    [Fact]
    public void Certificate_login_is_a_warning()
    {
        var ddl = SecurityDdlRenderer.RenderLogin(new("##MS_Cert##", 'C', false, null, null, null, null, []), V2019, out var warnings);
        Assert.StartsWith("-- WARNING", ddl);
        Assert.NotEmpty(warnings);
    }

    [Fact]
    public void Database_user_for_login_with_roles()
    {
        var ddl = SecurityDdlRenderer.RenderDatabaseUser(new("app", 'S', "app", "dbo", false, ["db_datareader"]), V2008, out _);
        Assert.Contains("CREATE USER [app] FOR LOGIN [app] WITH DEFAULT_SCHEMA = [dbo];", ddl);
        Assert.Contains("EXEC sys.sp_addrolemember @rolename = N'db_datareader', @membername = N'app';", ddl);
    }

    [Fact]
    public void Database_user_without_login()
    {
        Assert.StartsWith("CREATE USER [svc] WITHOUT LOGIN WITH DEFAULT_SCHEMA = [dbo];",
            SecurityDdlRenderer.RenderDatabaseUser(new("svc", 'S', null, "dbo", true, []), V2019, out _));
    }

    [Fact]
    public void Custom_role_is_created_fixed_role_only_gets_members()
    {
        var custom = SecurityDdlRenderer.RenderDatabaseRole(new("reporting", false, false, "dbo", null, ["app"]), V2019);
        Assert.Contains("CREATE ROLE [reporting] AUTHORIZATION [dbo];", custom);
        Assert.Contains("ALTER ROLE [reporting] ADD MEMBER [app];", custom);

        var fixedRole = SecurityDdlRenderer.RenderDatabaseRole(new("db_owner", true, false, "dbo", null, ["app"]), V2019);
        Assert.DoesNotContain("CREATE ROLE", fixedRole);
        Assert.Contains("ALTER ROLE [db_owner] ADD MEMBER [app];", fixedRole);
    }

    [Fact]
    public void Application_role_uses_password_placeholder()
    {
        var ddl = SecurityDdlRenderer.RenderDatabaseRole(new("approle", false, true, null, "dbo", []), V2019);
        Assert.Equal("CREATE APPLICATION ROLE [approle] WITH PASSWORD = " + SecurityDdlRenderer.PasswordPlaceholder + ", DEFAULT_SCHEMA = [dbo];", ddl);
    }

    [Fact]
    public void Names_cannot_break_out_of_warning_comments()
    {
        var ddl = SecurityDdlRenderer.RenderLogin(new("x\r\nDROP LOGIN [y];--", 'C', false, null, null, null, null, []), V2019, out var w);
        Assert.DoesNotContain('\n', ddl);
        Assert.DoesNotContain('\r', ddl);
        Assert.DoesNotContain('\n', w[0]);

        var role = SecurityDdlRenderer.RenderServerRole(new("r\nDROP LOGIN [y];", false, []), V2008);
        Assert.DoesNotContain('\n', role);
    }

    [Fact]
    public void Table_index_warning_cannot_break_out_of_comment()
    {
        var ix = new IndexMeta("ix\r\nDROP TABLE [t];--", 3, false, false, false, null, false, []);
        var ddl = TableDdlRenderer.RenderIndex("dbo", "t", ix, out var warning);
        Assert.DoesNotContain('\n', ddl);
        Assert.DoesNotContain('\n', warning!);
    }

    [Fact]
    public void Server_role_variants()
    {
        var user = new ServerRoleMeta("ops", false, ["app"]);
        var v11 = SecurityDdlRenderer.RenderServerRole(user, V2019);
        Assert.Contains("CREATE SERVER ROLE [ops];", v11);
        Assert.Contains("ALTER SERVER ROLE [ops] ADD MEMBER [app];", v11);

        var old = SecurityDdlRenderer.RenderServerRole(user, V2008);
        Assert.StartsWith("-- WARNING", old);
        Assert.DoesNotContain("CREATE SERVER ROLE", old);

        var oldFixed = SecurityDdlRenderer.RenderServerRole(new("dbcreator", true, ["app"]), V2008);
        Assert.Equal("EXEC sys.sp_addsrvrolemember @loginame = N'app', @rolename = N'dbcreator';", oldFixed);

        var fixedRole = SecurityDdlRenderer.RenderServerRole(new("sysadmin", true, ["app"]), V2019);
        Assert.Equal("ALTER SERVER ROLE [sysadmin] ADD MEMBER [app];", fixedRole);
    }

    [Fact]
    public void Quoting_of_brackets_and_quotes()
    {
        var login = SecurityDdlRenderer.RenderLogin(new("a]b", 'U', false, null, null, null, null, []), V2019, out _);
        Assert.Contains("[a]]b]", login);

        var user = SecurityDdlRenderer.RenderDatabaseUser(new("o'brien", 'S', "o'brien", null, false, ["db_datareader"]), V2008, out _);
        Assert.Contains("@membername = N'o''brien'", user);
    }

    [Fact]
    public void Windows_group_login_and_unsupported_user_types()
    {
        var g = SecurityDdlRenderer.RenderLogin(new(@"CORP\staff", 'G', false, null, null, null, null, []), V2019, out var gw);
        Assert.Equal(@"CREATE LOGIN [CORP\staff] FROM WINDOWS;", g);
        Assert.Empty(gw);

        foreach (var t in new[] { 'E', 'X' })
        {
            var ddl = SecurityDdlRenderer.RenderDatabaseUser(new("ext", t, null, null, false, []), V2019, out var w);
            Assert.StartsWith("-- WARNING", ddl);
            Assert.Single(w);
        }
    }

    [Fact]
    public void Sql_user_without_login_info_is_scripted_without_login_with_warning()
    {
        var ddl = SecurityDdlRenderer.RenderDatabaseUser(new("x", 'S', null, null, false, []), V2019, out var w);
        Assert.Equal("CREATE USER [x] WITHOUT LOGIN;", ddl);
        Assert.Equal("user [x] has no matching login; scripted as CREATE USER [x] WITHOUT LOGIN", Assert.Single(w));

        SecurityDdlRenderer.RenderDatabaseUser(new("svc", 'S', null, "dbo", true, []), V2019, out var w2);
        Assert.Empty(w2);
    }

    [Fact]
    public void Check_expiration_on_forces_check_policy_on()
    {
        var ddl = SecurityDdlRenderer.RenderLogin(new("a", 'S', false, null, null, null, true, []), V2019, out _);
        Assert.Contains("CHECK_POLICY = ON, CHECK_EXPIRATION = ON", ddl);
    }
}
