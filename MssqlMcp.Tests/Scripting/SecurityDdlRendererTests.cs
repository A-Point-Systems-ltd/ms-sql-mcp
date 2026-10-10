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
        Assert.StartsWith("--create\r\n" + @"CREATE LOGIN [CORP\dana] FROM WINDOWS WITH DEFAULT_DATABASE = [master];", ddl);
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

    [Theory]
    [InlineData("guest")]
    [InlineData("sys")]
    [InlineData("INFORMATION_SCHEMA")]
    [InlineData("dbo")]
    public void Builtin_database_users_are_not_created(string name)
    {
        var ddl = SecurityDdlRenderer.RenderDatabaseUser(new(name, 'S', null, name, false, ["db_owner"]), V2019, out var warnings);
        Assert.Equal($"-- {name} is a built-in principal and is not scripted.", ddl);
        Assert.DoesNotContain("CREATE USER", ddl);
        Assert.Single(warnings);
    }

    [Fact]
    public void Contained_database_user_gets_password_placeholder_and_warning()
    {
        var ddl = SecurityDdlRenderer.RenderDatabaseUser(new("cu", 'S', null, "app", false, ["db_datareader"], true), V2019, out var warnings);
        Assert.Contains("CREATE USER [cu] WITH PASSWORD = " + SecurityDdlRenderer.PasswordPlaceholder + ", DEFAULT_SCHEMA = [app];", ddl);
        Assert.DoesNotContain("WITHOUT LOGIN", ddl);
        Assert.Contains("ALTER ROLE [db_datareader] ADD MEMBER [cu];", ddl);
        Assert.Single(warnings);
    }

    [Fact]
    public void Database_user_without_login()
    {
        Assert.StartsWith("--create\r\nCREATE USER [svc] WITHOUT LOGIN WITH DEFAULT_SCHEMA = [dbo];",
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
        Assert.Equal("--create\r\nCREATE APPLICATION ROLE [approle] WITH PASSWORD = " + SecurityDdlRenderer.PasswordPlaceholder + ", DEFAULT_SCHEMA = [dbo];", ddl);
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
    public void Public_role_is_never_created()
    {
        // public is reported as non-fixed by the catalog (is_fixed_role = 0), but always exists.
        var db = SecurityDdlRenderer.RenderDatabaseRole(new("public", false, false, "dbo", null, []), V2019);
        Assert.DoesNotContain("CREATE", db);
        Assert.StartsWith("-- ", db);

        var dbWithMember = SecurityDdlRenderer.RenderDatabaseRole(new("public", false, false, "dbo", null, ["app"]), V2008);
        Assert.Equal("--members\r\nEXEC sys.sp_addrolemember @rolename = N'public', @membername = N'app';", dbWithMember);

        var server = SecurityDdlRenderer.RenderServerRole(new("public", false, []), V2019, out var w);
        Assert.DoesNotContain("CREATE", server);
        Assert.Empty(w);
    }

    [Fact]
    public void Server_role_variants()
    {
        var user = new ServerRoleMeta("ops", false, ["app"]);
        var v11 = SecurityDdlRenderer.RenderServerRole(user, V2019);
        Assert.Contains("CREATE SERVER ROLE [ops];", v11);
        Assert.Contains("ALTER SERVER ROLE [ops] ADD MEMBER [app];", v11);

        var old = SecurityDdlRenderer.RenderServerRole(user, V2008, out var oldWarnings);
        Assert.StartsWith("-- WARNING", old);
        Assert.Single(oldWarnings);
        Assert.DoesNotContain("CREATE SERVER ROLE", old);

        var oldFixed = SecurityDdlRenderer.RenderServerRole(new("dbcreator", true, ["app"]), V2008);
        Assert.Equal("--members\r\nEXEC sys.sp_addsrvrolemember @loginame = N'app', @rolename = N'dbcreator';", oldFixed);

        var fixedRole = SecurityDdlRenderer.RenderServerRole(new("sysadmin", true, ["app"]), V2019);
        Assert.Equal("--members\r\nALTER SERVER ROLE [sysadmin] ADD MEMBER [app];", fixedRole);
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
        Assert.Equal("--create\r\n" + @"CREATE LOGIN [CORP\staff] FROM WINDOWS;", g);
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
        Assert.Equal("--create\r\n-- WARNING: user [x] has no matching login; scripted as CREATE USER [x] WITHOUT LOGIN\r\nCREATE USER [x] WITHOUT LOGIN;", ddl);
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

    [Fact]
    public void Database_role_has_titled_sections_with_grouped_permissions()
    {
        var role = new DatabaseRoleMeta("api_programmers", false, false, "dbo", null, [@"APOINTDC\DevTeam"], ["api"],
        [
            new('G', "SELECT", "[dbo].[AiUsageLog]"),
            new('G', "INSERT", "[dbo].[AiUsageLog]"),
            new('G', "SELECT", "[dbo].[AiBatchJobs]"),
            new('G', "INSERT", "[dbo].[AiBatchJobs]"),
            new('G', "UPDATE", "[dbo].[AiBatchJobs]"),
            new('W', "EXECUTE", "SCHEMA::[api]"),
            new('D', "DELETE", "[dbo].[AiBatchJobs]"),
            new('G', "SELECT", "[dbo].[T] ([Secret])"),
            new('G', "CONNECT", null),
            new('G', "SEND", null, "service"),
        ]);
        var ddl = SecurityDdlRenderer.RenderDatabaseRole(role, V2019, out var warnings);
        Assert.Equal(string.Join("\r\n",
            "--create",
            "CREATE ROLE [api_programmers] AUTHORIZATION [dbo];",
            "",
            "--owned schemas",
            "ALTER AUTHORIZATION ON SCHEMA::[api] TO [api_programmers];",
            "",
            "--members",
            @"ALTER ROLE [api_programmers] ADD MEMBER [APOINTDC\DevTeam];",
            "",
            "--permissions",
            "-- WARNING: SEND on a service securable is not scripted.",
            "GRANT SELECT, INSERT ON [dbo].[AiUsageLog] TO [api_programmers];",
            "GRANT SELECT, INSERT, UPDATE ON [dbo].[AiBatchJobs] TO [api_programmers];",
            "GRANT EXECUTE ON SCHEMA::[api] TO [api_programmers] WITH GRANT OPTION;",
            "DENY DELETE ON [dbo].[AiBatchJobs] TO [api_programmers];",
            "GRANT SELECT ON [dbo].[T] ([Secret]) TO [api_programmers];",
            "GRANT CONNECT TO [api_programmers];"), ddl);
        Assert.Single(warnings);
    }

    [Fact]
    public void Login_includes_server_permissions_and_its_database_user()
    {
        var user = new DatabaseUserMeta("app", 'S', "app", "dbo", false, ["db_datareader"], false, ["appschema"], [new('G', "EXECUTE", null)]);
        var login = new LoginMeta("app", 'S', false, "Sales", null, true, false, ["dbcreator"],
            [new('G', "VIEW SERVER STATE", null), new('G', "IMPERSONATE", "LOGIN::[other]")],
            new LoginDatabaseMeta("Sales", user, user.OwnedSchemas!, user.Permissions!));
        var ddl = SecurityDdlRenderer.RenderLogin(login, V2019, out _);
        Assert.Contains("--server roles\r\nALTER SERVER ROLE [dbcreator] ADD MEMBER [app];", ddl);
        Assert.Contains("--server permissions\r\nGRANT VIEW SERVER STATE TO [app];\r\nGRANT IMPERSONATE ON LOGIN::[other] TO [app];", ddl);
        Assert.Contains("--database [Sales]: user [app]\r\nUSE [Sales];\r\nCREATE USER [app] FOR LOGIN [app] WITH DEFAULT_SCHEMA = [dbo];", ddl);
        Assert.Contains("--owned schemas\r\nALTER AUTHORIZATION ON SCHEMA::[appschema] TO [app];", ddl);
        Assert.Contains("--database roles\r\nALTER ROLE [db_datareader] ADD MEMBER [app];", ddl);
        Assert.Contains("--database permissions\r\nGRANT EXECUTE TO [app];", ddl);
        Assert.DoesNotContain("PASSWORD = N'", ddl.Replace(SecurityDdlRenderer.PasswordPlaceholder, string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);

        var owner = new LoginMeta("sa2", 'S', false, null, null, null, null, [], [], new LoginDatabaseMeta("Sales", user with { Name = "dbo" }, [], []));
        Assert.Contains("database owner", SecurityDdlRenderer.RenderLogin(owner, V2019, out _), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, null, null, null, null, null)]
    [InlineData(1, "dbo", "T", null, null, "[dbo].[T]")]
    [InlineData(1, "dbo", "T", "c", null, "[dbo].[T] ([c])")]
    [InlineData(3, "api", null, null, null, "SCHEMA::[api]")]
    [InlineData(4, null, "r", null, "R", "ROLE::[r]")]
    [InlineData(4, null, "u", null, "S", "USER::[u]")]
    [InlineData(6, "dbo", "tt", null, null, "TYPE::[dbo].[tt]")]
    [InlineData(25, null, "cert", null, null, "CERTIFICATE::[cert]")]
    public void Database_securables_render_their_on_clause(int cls, string? schema, string? name, string? column, string? principalType, string? expected)
    {
        var (on, unsupported) = CatalogReader.DatabaseSecurable(cls, "X", schema, name, column, principalType);
        Assert.Equal(expected, on);
        Assert.Null(unsupported);
        Assert.Equal("service", CatalogReader.DatabaseSecurable(17, "SERVICE", null, "s", null, null).Unsupported);
    }

    [Fact]
    public void A_fixed_role_owning_its_own_schema_has_nothing_to_script()
    {
        var ddl = SecurityDdlRenderer.RenderDatabaseRole(new("db_datareader", true, false, "dbo", null, [], ["db_datareader"], []), V2019);
        Assert.StartsWith("-- ", ddl);
        Assert.DoesNotContain("ALTER AUTHORIZATION", ddl);

        var custom = SecurityDdlRenderer.RenderDatabaseRole(new("db_datareader", true, false, "dbo", null, [], ["reports"], []), V2019);
        Assert.Contains("ALTER AUTHORIZATION ON SCHEMA::[reports] TO [db_datareader];", custom);
    }

    [Fact]
    public void Azure_sql_database_has_no_server_permissions_but_managed_instance_does()
    {
        Assert.False(SqlServerVersion.Parse("12.0.2000.8", SqlServerVersion.EngineEditionAzureSqlDatabase).HasServerPermissions);
        Assert.True(SqlServerVersion.Parse("12.0.2000.8", SqlServerVersion.EngineEditionAzureManagedInstance).HasServerPermissions);
        Assert.True(V2008.HasServerPermissions);
    }
}
