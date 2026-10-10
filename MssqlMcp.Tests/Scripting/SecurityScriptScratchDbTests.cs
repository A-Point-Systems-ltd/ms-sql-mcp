using Microsoft.Data.SqlClient;
using Mssql.McpServer.Scripting;

namespace MssqlMcp.Tests.Scripting;

/// <summary>Security principals scripted from a LocalDB scratch database: sections, permissions, owned schemas, login mapping.</summary>
[Collection(MssqlMcp.Tests.LanguageService.LanguageServiceCollection.Name)]
public sealed class SecurityScriptScratchDbTests
{
    [SkippableFact]
    public async Task Role_user_and_login_scripts_carry_permissions_owned_schemas_and_mapping()
    {
        ScratchDatabases scratch;
        try
        {
            scratch = await ScratchDatabases.CreateAsync(1);
        }
        catch (SkipException ex)
        {
            Skip.If(true, ex.Message);
            return;
        }

        await using var _ = scratch;
        var cs = scratch.ConnectionStrings[0];
        var login = "mssqlmcp_sec_" + Guid.NewGuid().ToString("N")[..8];
        await ScratchDatabases.ExecAsync(cs, """
            CREATE TABLE dbo.AiUsageLog (id int, msg nvarchar(10));
            CREATE TABLE dbo.AiBatchJobs (id int, secret int);
            CREATE ROLE api_programmers AUTHORIZATION dbo;
            EXEC(N'CREATE SCHEMA api AUTHORIZATION dbo');
            EXEC(N'CREATE SCHEMA apiown AUTHORIZATION api_programmers');
            CREATE USER devteam WITHOUT LOGIN;
            ALTER ROLE api_programmers ADD MEMBER devteam;
            GRANT SELECT, INSERT ON dbo.AiUsageLog TO api_programmers;
            GRANT SELECT, INSERT, UPDATE ON dbo.AiBatchJobs TO api_programmers;
            DENY SELECT ON dbo.AiBatchJobs (secret) TO api_programmers;
            GRANT EXECUTE ON SCHEMA::api TO api_programmers WITH GRANT OPTION;
            GRANT VIEW DEFINITION ON ROLE::api_programmers TO devteam;
            """);
        await ScratchDatabases.ExecAsync(cs, $"CREATE LOGIN [{login}] WITH PASSWORD = N'Aa1!{Guid.NewGuid():N}', CHECK_POLICY = OFF; CREATE USER [{login}] FOR LOGIN [{login}]; ALTER ROLE db_datareader ADD MEMBER [{login}]; GRANT EXECUTE TO [{login}];");
        try
        {
            await using var conn = new SqlConnection(cs);
            await conn.OpenAsync();

            var role = await Script(conn, "DatabaseRole", "api_programmers");
            Assert.StartsWith("--create\r\nCREATE ROLE [api_programmers] AUTHORIZATION [dbo];", role);
            Assert.Contains("--owned schemas\r\nALTER AUTHORIZATION ON SCHEMA::[apiown] TO [api_programmers];", role);
            Assert.Contains("--members\r\nALTER ROLE [api_programmers] ADD MEMBER [devteam];", role);
            Assert.Contains("--permissions\r\n", role);
            Assert.Contains("GRANT INSERT, SELECT ON [dbo].[AiUsageLog] TO [api_programmers];", role);
            Assert.Contains("GRANT INSERT, SELECT, UPDATE ON [dbo].[AiBatchJobs] TO [api_programmers];", role);
            Assert.Contains("DENY SELECT ON [dbo].[AiBatchJobs] ([secret]) TO [api_programmers];", role);
            Assert.True(role.Contains("GRANT EXECUTE ON SCHEMA::[api] TO [api_programmers] WITH GRANT OPTION;", StringComparison.Ordinal), role);

            var user = await Script(conn, "DatabaseUser", "devteam");
            Assert.Contains("--roles\r\nALTER ROLE [api_programmers] ADD MEMBER [devteam];", user);
            Assert.Contains("GRANT VIEW DEFINITION ON ROLE::[api_programmers] TO [devteam];", user);

            var loginScript = await Script(conn, "Login", login);
            Assert.Contains("--server permissions\r\nGRANT CONNECT SQL TO [" + login + "];", loginScript);
            Assert.Contains($"--database [{conn.Database}]: user [{login}]\r\nUSE [{conn.Database}];\r\nCREATE USER [{login}] FOR LOGIN [{login}]", loginScript);
            Assert.Contains($"--database roles\r\nALTER ROLE [db_datareader] ADD MEMBER [{login}];", loginScript);
            Assert.Contains($"--database permissions\r\nGRANT CONNECT, EXECUTE TO [{login}];", loginScript);
            Assert.DoesNotContain("Aa1!", loginScript, StringComparison.Ordinal);
        }
        finally
        {
            await ScratchDatabases.ExecAsync(cs, $"DROP USER [{login}];");
            await ScratchDatabases.ExecAsync(cs.Replace(new SqlConnectionStringBuilder(cs).InitialCatalog, "master", StringComparison.Ordinal), $"DROP LOGIN [{login}];");
        }
    }

    private static async Task<string> Script(SqlConnection conn, string type, string name)
    {
        var (result, error) = await ObjectScripter.ScriptAsync(conn, type, name, null, CancellationToken.None);
        Assert.Null(error);
        return result!.Ddl;
    }
}
