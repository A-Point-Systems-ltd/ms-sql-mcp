// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.

using System.Security.Cryptography;
using Microsoft.Data.SqlClient;
using Mssql.McpServer.InsightsLayer;
using Mssql.McpServer.Scripting;

namespace MssqlMcp.Tests.Scripting;

/// <summary>
/// The installed DDL_Audit trigger never blocks another principal's DDL: it runs as the loginless DDL_Audit_Writer
/// (INSERT / SELECT on dbo.DDL_AuditLog only), forces the SET options its XML methods need, records ORIGINAL_LOGIN(),
/// skips the insert while the audit table has DML triggers, and swallows audit-insert failures. LocalDB scratch databases
/// only; every SQL login these tests create is dropped in finally.
/// </summary>
public sealed class DdlAuditTriggerHardeningTests
{
    private const string LocalDbServer = "Server=(localdb)\\MSSQLLocalDB;Integrated Security=true;TrustServerCertificate=True";

    private static async Task InstallAsync(string cs)
    {
        await using var conn = new SqlConnection(cs);
        await conn.OpenAsync();
        _ = await DdlAudit.InstallAsync(conn, CancellationToken.None);
    }

    private static Task<int> ProcedureExistsAsync(string cs, string name) =>
        ScratchDatabases.ScalarAsync<int>(cs, $"SELECT CASE WHEN OBJECT_ID(N'dbo.{name}', N'P') IS NULL THEN 0 ELSE 1 END");

    private static async Task ExecOnAsync(SqlConnection conn, string sql)
    {
        await using var cmd = new SqlCommand(sql, conn);
        _ = await cmd.ExecuteNonQueryAsync();
    }

    [SkippableFact]
    public async Task A_db_ddladmin_only_login_can_create_a_procedure_and_its_original_login_is_recorded()
    {
        await using var scratch = await ScratchDatabases.CreateAsync(1);
        var cs = scratch.ConnectionStrings[0];
        await InstallAsync(cs);

        var login = $"p5_ddladm_{Guid.NewGuid():N}"[..20];
        var password = "Aa1!" + Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        await using (var master = new SqlConnection($"{LocalDbServer};Initial Catalog=master"))
        {
            await master.OpenAsync();
            await ExecOnAsync(master, $"CREATE LOGIN [{login}] WITH PASSWORD = N'{password}', CHECK_POLICY = OFF;");
        }

        try
        {
            // The caller has no permission on dbo.DDL_AuditLog at all (only DDL_Audit_Writer has INSERT / SELECT).
            await ScratchDatabases.ExecAsync(cs, $"""
                CREATE USER [{login}] FOR LOGIN [{login}];
                ALTER ROLE db_ddladmin ADD MEMBER [{login}];
                """);

            var userCs = new SqlConnectionStringBuilder(cs) { IntegratedSecurity = false, UserID = login, Password = password, Pooling = false }.ConnectionString;
            await ScratchDatabases.ExecAsync(userCs, "CREATE PROCEDURE dbo.by_ddladmin AS SELECT 1");

            Assert.Equal(1, await ProcedureExistsAsync(cs, "by_ddladmin"));
            Assert.Equal(login, await ScratchDatabases.ScalarAsync<string>(cs, "SELECT LoginName FROM dbo.DDL_AuditLog WHERE ObjectName = 'by_ddladmin'"));
        }
        finally
        {
            SqlConnection.ClearAllPools();
            await using var master = new SqlConnection($"{LocalDbServer};Initial Catalog=master");
            await master.OpenAsync();
            await ExecOnAsync(master, $"IF SUSER_ID(N'{login}') IS NOT NULL DROP LOGIN [{login}];");
        }
    }

    [SkippableTheory]
    [InlineData("SET ANSI_WARNINGS OFF")]
    [InlineData("SET CONCAT_NULL_YIELDS_NULL OFF")]
    [InlineData("SET ARITHABORT OFF")]
    [InlineData("SET ANSI_PADDING OFF")]
    [InlineData("SET NUMERIC_ROUNDABORT ON")]
    public async Task A_session_with_non_ansi_set_options_can_run_ddl_and_it_is_logged(string setOption)
    {
        await using var scratch = await ScratchDatabases.CreateAsync(1);
        var cs = scratch.ConnectionStrings[0];
        await InstallAsync(cs);

        await using (var conn = new SqlConnection(cs))
        {
            await conn.OpenAsync();
            await ExecOnAsync(conn, setOption);
            await ExecOnAsync(conn, "CREATE PROCEDURE dbo.legacy_session AS SELECT 1");
        }

        Assert.Equal(1, await ProcedureExistsAsync(cs, "legacy_session"));
        Assert.Equal(1, await ScratchDatabases.ScalarAsync<int>(cs, "SELECT COUNT(*) FROM dbo.DDL_AuditLog WHERE ObjectName = 'legacy_session'"));
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_audit_insert_truncation_does_not_roll_back_the_ddl(bool callerXactAbort)
    {
        await using var scratch = await ScratchDatabases.CreateAsync(1);
        var cs = scratch.ConnectionStrings[0];
        await InstallAsync(cs);

        // This ALTER is itself audited: 'DDL_AuditLog' (12 chars) no longer fits, so it is also a truncation case.
        await ScratchDatabases.ExecAsync(cs, "DROP INDEX IX_DDL_AuditLog_ObjectName ON dbo.DDL_AuditLog");
        await ScratchDatabases.ExecAsync(cs, "DELETE FROM dbo.DDL_AuditLog");
        await ScratchDatabases.ExecAsync(cs, "ALTER TABLE dbo.DDL_AuditLog ALTER COLUMN ObjectName varchar(10) NULL");
        Assert.Equal(10, await ScratchDatabases.ScalarAsync<short>(cs, "SELECT COL_LENGTH('dbo.DDL_AuditLog', 'ObjectName')"));

        var messages = new List<string>();
        await using (var conn = new SqlConnection(cs))
        {
            conn.InfoMessage += (_, e) => messages.Add(e.Message);
            await conn.OpenAsync();
            if (callerXactAbort)
            {
                await ExecOnAsync(conn, "SET XACT_ABORT ON");
            }

            await ExecOnAsync(conn, "CREATE PROCEDURE dbo.a_name_longer_than_ten AS SELECT 1");
        }

        Assert.Equal(1, await ProcedureExistsAsync(cs, "a_name_longer_than_ten"));
        Assert.Equal(0, await ScratchDatabases.ScalarAsync<int>(cs, "SELECT COUNT(*) FROM dbo.DDL_AuditLog WHERE EventType IN ('ALTER_TABLE', 'CREATE_PROCEDURE')"));
        Assert.Contains(messages, m => m.StartsWith("DDL_Audit: this change was not logged", StringComparison.Ordinal));
    }

    [SkippableFact]
    public async Task An_audit_insert_constraint_failure_inside_a_user_transaction_keeps_the_transaction_committable()
    {
        await using var scratch = await ScratchDatabases.CreateAsync(1);
        var cs = scratch.ConnectionStrings[0];
        await InstallAsync(cs);
        await ScratchDatabases.ExecAsync(cs, "ALTER TABLE dbo.DDL_AuditLog ADD CONSTRAINT CK_probe CHECK (ObjectName IS NULL OR ObjectName <> 'refused')");

        await using (var conn = new SqlConnection(cs))
        {
            await conn.OpenAsync();
            await using var tx = conn.BeginTransaction();
            await using (var cmd = new SqlCommand("CREATE PROCEDURE dbo.refused AS SELECT 1", conn, tx))
            {
                _ = await cmd.ExecuteNonQueryAsync();
            }

            await using (var state = new SqlCommand("SELECT XACT_STATE()", conn, tx))
            {
                Assert.Equal((short)1, Convert.ToInt16(await state.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture));
            }

            tx.Commit();
        }

        Assert.Equal(1, await ProcedureExistsAsync(cs, "refused"));
    }

    private const string WriterExistsSql = "SELECT COUNT(*) FROM sys.database_principals WHERE name = N'DDL_Audit_Writer'";
    private const string TableExistsSql = "SELECT CASE WHEN OBJECT_ID(N'dbo.DDL_AuditLog', N'U') IS NULL THEN 0 ELSE 1 END";
    private const string DatabaseTriggerCountSql = "SELECT COUNT(*) FROM sys.triggers WHERE parent_class = 0";

    private static async Task ExecOnMasterAsync(string sql)
    {
        await using var master = new SqlConnection($"{LocalDbServer};Initial Catalog=master");
        await master.OpenAsync();
        await ExecOnAsync(master, sql);
    }

    private static string NewPassword() => "Aa1!" + Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

    private static Task DropLoginAsync(string login)
    {
        SqlConnection.ClearAllPools();
        return ExecOnMasterAsync($"IF SUSER_ID(N'{login}') IS NOT NULL DROP LOGIN [{login}];");
    }

    /// <summary>A db_ddladmin-only session (EXECUTE AS USER on an unpooled connection, so it never returns to the pool).</summary>
    private static async Task<SqlConnection> OpenAsUserAsync(string cs, string user)
    {
        var conn = new SqlConnection(new SqlConnectionStringBuilder(cs) { Pooling = false }.ConnectionString);
        await conn.OpenAsync();
        await ExecOnAsync(conn, $"EXECUTE AS USER = '{user}'");
        return conn;
    }

    /// <summary>
    /// RC1 / NET-019: a db_ddladmin-only user plants an AFTER INSERT trigger on dbo.DDL_AuditLog that adds itself to
    /// db_owner. With the old EXECUTE AS 'dbo' trigger that escalated (probe: IS_ROLEMEMBER = 1). Now the trigger runs as
    /// DDL_Audit_Writer and skips the INSERT while the table has a trigger: no escalation, and the DDL still succeeds.
    /// </summary>
    [SkippableFact]
    public async Task A_planted_trigger_on_the_audit_table_cannot_escalate_and_the_ddl_still_succeeds_unlogged()
    {
        await using var scratch = await ScratchDatabases.CreateAsync(1);
        var cs = scratch.ConnectionStrings[0];
        await InstallAsync(cs);
        await ScratchDatabases.ExecAsync(cs, "CREATE USER ddladm WITHOUT LOGIN; ALTER ROLE db_ddladmin ADD MEMBER ddladm;");

        var messages = new List<string>();
        await using (var conn = await OpenAsUserAsync(cs, "ddladm"))
        {
            conn.InfoMessage += (_, e) => messages.Add(e.Message);
            await ExecOnAsync(conn, "CREATE TRIGGER dbo.trg_plant ON dbo.DDL_AuditLog AFTER INSERT AS SET NOCOUNT ON; ALTER ROLE db_owner ADD MEMBER ddladm;");
            await ExecOnAsync(conn, "CREATE PROCEDURE dbo.after_plant AS SELECT 1");
        }

        Assert.Equal(0, await ScratchDatabases.ScalarAsync<int>(cs, """
            SELECT COUNT(*) FROM sys.database_role_members rm
            JOIN sys.database_principals r ON r.principal_id = rm.role_principal_id
            JOIN sys.database_principals m ON m.principal_id = rm.member_principal_id
            WHERE r.name = N'db_owner' AND m.name = N'ddladm'
            """));
        Assert.Equal(1, await ProcedureExistsAsync(cs, "after_plant"));
        Assert.Equal(0, await ScratchDatabases.ScalarAsync<int>(cs, "SELECT COUNT(*) FROM dbo.DDL_AuditLog WHERE ObjectName IN ('after_plant', 'trg_plant')"));
        Assert.Contains(messages, m => m == "DDL_Audit: this change was not logged: DDL_AuditLog has triggers.");
    }

    /// <summary>
    /// RC1: a database restored after its owner login was dropped has an orphaned dbo, so EXECUTE AS 'dbo' fails
    /// (15517) inside every DDL statement. The DDL_Audit_Writer trigger keeps working there.
    /// </summary>
    [SkippableFact]
    public async Task On_a_database_with_an_orphaned_owner_ddl_succeeds_and_is_logged()
    {
        await using var scratch = await ScratchDatabases.CreateAsync(1);
        var db = scratch.Names[0];
        var cs = scratch.ConnectionStrings[0];
        var owner = $"p5_owner_{Guid.NewGuid():N}"[..20];
        var backup = Path.Combine(Path.GetTempPath(), $"{db}.bak");
        try
        {
            await ExecOnMasterAsync($"CREATE LOGIN [{owner}] WITH PASSWORD = N'{NewPassword()}', CHECK_POLICY = OFF;");
            await ExecOnMasterAsync($"ALTER AUTHORIZATION ON DATABASE::[{db}] TO [{owner}];");
            await ExecOnMasterAsync($"BACKUP DATABASE [{db}] TO DISK = N'{backup}' WITH INIT;");
            SqlConnection.ClearAllPools();
            await ExecOnMasterAsync($"ALTER DATABASE [{db}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{db}];");
            await DropLoginAsync(owner);
            await ExecOnMasterAsync($"RESTORE DATABASE [{db}] FROM DISK = N'{backup}';");

            // The precondition: dbo cannot be impersonated here.
            var orphaned = await Assert.ThrowsAsync<SqlException>(() => ScratchDatabases.ExecAsync(cs, "EXECUTE AS USER = 'dbo'; REVERT;"));
            Assert.Equal(15517, orphaned.Number);

            await InstallAsync(cs);
            await ScratchDatabases.ExecAsync(cs, "CREATE PROCEDURE dbo.on_orphaned AS SELECT 1");

            Assert.Equal(1, await ProcedureExistsAsync(cs, "on_orphaned"));
            Assert.Equal(1, await ScratchDatabases.ScalarAsync<int>(cs, "SELECT COUNT(*) FROM dbo.DDL_AuditLog WHERE ObjectName = 'on_orphaned'"));
        }
        finally
        {
            await DropLoginAsync(owner);
            File.Delete(backup);
        }
    }

    [SkippableFact]
    public async Task The_trigger_runs_as_the_loginless_writer_with_only_insert_and_select_and_the_new_table_gets_the_index()
    {
        await using var scratch = await ScratchDatabases.CreateAsync(1);
        var cs = scratch.ConnectionStrings[0];
        await InstallAsync(cs);

        Assert.Equal(
            "DDL_Audit_Writer",
            await ScratchDatabases.ScalarAsync<string>(cs, "SELECT USER_NAME(m.execute_as_principal_id) FROM sys.triggers t JOIN sys.sql_modules m ON m.object_id = t.object_id WHERE t.parent_class = 0 AND t.name = N'DDL_Audit'"));
        // A loginless SQL user (type S, authentication_type 0 = NONE, no login with its SID), default schema dbo.
        Assert.Equal(1, await ScratchDatabases.ScalarAsync<int>(cs, """
            SELECT COUNT(*) FROM sys.database_principals p
            WHERE p.name = N'DDL_Audit_Writer' AND p.type = 'S' AND p.authentication_type = 0 AND p.default_schema_name = N'dbo'
              AND NOT EXISTS (SELECT 1 FROM sys.server_principals sp WHERE sp.sid = p.sid)
            """));
        // Exactly INSERT and SELECT on dbo.DDL_AuditLog, besides the CONNECT every user has.
        Assert.Equal(2, await ScratchDatabases.ScalarAsync<int>(cs, """
            SELECT COUNT(*) FROM sys.database_permissions
            WHERE grantee_principal_id = DATABASE_PRINCIPAL_ID(N'DDL_Audit_Writer') AND state = 'G' AND permission_name <> 'CONNECT'
            """));
        Assert.Equal(2, await ScratchDatabases.ScalarAsync<int>(cs, """
            SELECT COUNT(*) FROM sys.database_permissions
            WHERE grantee_principal_id = DATABASE_PRINCIPAL_ID(N'DDL_Audit_Writer') AND state = 'G'
              AND major_id = OBJECT_ID(N'dbo.DDL_AuditLog') AND minor_id = 0 AND permission_name IN ('INSERT', 'SELECT')
            """));
        Assert.Equal(0, await ScratchDatabases.ScalarAsync<int>(cs, "SELECT COUNT(*) FROM sys.database_role_members WHERE member_principal_id = DATABASE_PRINCIPAL_ID(N'DDL_Audit_Writer')"));
        Assert.Equal(
            "ObjectName,ID|SchemaName,LoginName,PostTime",
            await ScratchDatabases.ScalarAsync<string>(cs, """
                SELECT STUFF((SELECT ',' + c.name FROM sys.index_columns ic JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                              WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.is_included_column = 0 ORDER BY ic.key_ordinal FOR XML PATH('')), 1, 1, '')
                     + '|' +
                       STUFF((SELECT ',' + c.name FROM sys.index_columns ic JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                              WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.is_included_column = 1 ORDER BY ic.index_column_id FOR XML PATH('')), 1, 1, '')
                FROM sys.indexes i WHERE i.object_id = OBJECT_ID(N'dbo.DDL_AuditLog') AND i.name = N'IX_DDL_AuditLog_ObjectName' AND i.type = 2
                """));
    }

    [SkippableFact]
    public async Task An_existing_loginless_writer_is_reused_and_install_stays_idempotent()
    {
        await using var scratch = await ScratchDatabases.CreateAsync(1);
        var cs = scratch.ConnectionStrings[0];
        await ScratchDatabases.ExecAsync(cs, "CREATE USER [DDL_Audit_Writer] WITHOUT LOGIN;");

        await InstallAsync(cs);
        await InstallAsync(cs);
        await ScratchDatabases.ExecAsync(cs, "CREATE PROCEDURE dbo.reused AS SELECT 1");

        Assert.Equal(1, await ScratchDatabases.ScalarAsync<int>(cs, "SELECT COUNT(*) FROM dbo.DDL_AuditLog WHERE ObjectName = 'reused'"));
    }

    [SkippableTheory]
    [InlineData("role")]
    [InlineData("login")]
    public async Task A_pre_existing_writer_that_is_not_a_loginless_user_is_refused_and_nothing_is_created(string kind)
    {
        await using var scratch = await ScratchDatabases.CreateAsync(1);
        var cs = scratch.ConnectionStrings[0];
        var login = $"p5_wr_{Guid.NewGuid():N}"[..20];
        try
        {
            if (kind == "role")
            {
                await ScratchDatabases.ExecAsync(cs, "CREATE ROLE [DDL_Audit_Writer];");
            }
            else
            {
                await ExecOnMasterAsync($"CREATE LOGIN [{login}] WITH PASSWORD = N'{NewPassword()}', CHECK_POLICY = OFF;");
                await ScratchDatabases.ExecAsync(cs, $"CREATE USER [DDL_Audit_Writer] FOR LOGIN [{login}];");
            }

            await using (var conn = new SqlConnection(cs))
            {
                await conn.OpenAsync();
                var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => DdlAudit.InstallAsync(conn, CancellationToken.None));
                Assert.Equal(DdlAudit.WriterConflictError, ex.Message);
            }

            Assert.Equal(0, await ScratchDatabases.ScalarAsync<int>(cs, TableExistsSql));
            Assert.Equal(0, await ScratchDatabases.ScalarAsync<int>(cs, DatabaseTriggerCountSql));
        }
        finally
        {
            SqlConnection.ClearAllPools();
            if (kind == "login")
            {
                // The user goes with the scratch database; the login is server-level.
                await DropLoginAsync(login);
            }
        }
    }

    /// <summary>
    /// NET-021 / NET-022: a loginless DDL_Audit_Writer with more rights than the trigger needs is refused (nothing created,
    /// nothing on it altered or revoked), and status reports the same conflict (canInstall false, the text in warnings).
    /// </summary>
    [SkippableTheory]
    [InlineData("ALTER ROLE db_owner ADD MEMBER [DDL_Audit_Writer];", "SELECT COUNT(*) FROM sys.database_role_members WHERE member_principal_id = DATABASE_PRINCIPAL_ID(N'DDL_Audit_Writer')")]
    [InlineData("GRANT CREATE TABLE TO [DDL_Audit_Writer];", "SELECT COUNT(*) FROM sys.database_permissions WHERE grantee_principal_id = DATABASE_PRINCIPAL_ID(N'DDL_Audit_Writer') AND permission_name = 'CREATE TABLE'")]
    [InlineData("GRANT SELECT ON SCHEMA::dbo TO [DDL_Audit_Writer];", "SELECT COUNT(*) FROM sys.database_permissions WHERE grantee_principal_id = DATABASE_PRINCIPAL_ID(N'DDL_Audit_Writer') AND class = 3")]
    [InlineData("CREATE SCHEMA [w] AUTHORIZATION [DDL_Audit_Writer];", "SELECT COUNT(*) FROM sys.schemas WHERE principal_id = DATABASE_PRINCIPAL_ID(N'DDL_Audit_Writer')")]
    public async Task A_loginless_writer_with_extra_rights_is_refused_left_unchanged_and_reported_by_status(string extra, string stillThereSql)
    {
        await using var scratch = await ScratchDatabases.CreateAsync(1);
        var cs = scratch.ConnectionStrings[0];
        await ScratchDatabases.ExecAsync(cs, "CREATE USER [DDL_Audit_Writer] WITHOUT LOGIN;");
        await ScratchDatabases.ExecAsync(cs, extra);

        await using (var conn = new SqlConnection(cs))
        {
            await conn.OpenAsync();
            var status = await DdlAudit.GetStatusAsync(conn, readOnly: false, CancellationToken.None);
            Assert.False(status.CanInstall);
            Assert.Contains(DdlAudit.WriterRightsError, status.Warnings ?? []);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => DdlAudit.InstallAsync(conn, CancellationToken.None));
            Assert.Equal(
                "A user named DDL_Audit_Writer already exists with more rights than INSERT/SELECT on dbo.DDL_AuditLog; nothing was created. Remove its extra rights or drop it, then retry.",
                ex.Message);
        }

        Assert.Equal(1, await ScratchDatabases.ScalarAsync<int>(cs, stillThereSql));
        Assert.Equal(0, await ScratchDatabases.ScalarAsync<int>(cs, TableExistsSql));
        Assert.Equal(0, await ScratchDatabases.ScalarAsync<int>(cs, DatabaseTriggerCountSql));
    }

    [SkippableFact]
    public async Task A_loginless_writer_with_only_insert_and_select_on_the_existing_table_is_accepted()
    {
        await using var scratch = await ScratchDatabases.CreateAsync(1);
        var cs = scratch.ConnectionStrings[0];
        await using (var conn = new SqlConnection(cs))
        {
            await conn.OpenAsync();
            // A table plus a writer with exactly the trigger's grant (for example a set-up stopped before the trigger).
            await InsightsLayerService.ExecuteBatchesAsync(conn, await InsightsLayerService.ReadEmbeddedResourceAsync(
                typeof(InsightsLayerService).Assembly, "Mssql.McpServer.InsightsLayer.SqlScripts.CreateDdlAuditLog.sql", CancellationToken.None), CancellationToken.None);
        }

        await ScratchDatabases.ExecAsync(cs, "CREATE USER [DDL_Audit_Writer] WITHOUT LOGIN; GRANT INSERT, SELECT ON dbo.DDL_AuditLog TO [DDL_Audit_Writer];");

        await using (var conn = new SqlConnection(cs))
        {
            await conn.OpenAsync();
            Assert.True((await DdlAudit.GetStatusAsync(conn, readOnly: false, CancellationToken.None)).CanInstall);
        }

        await InstallAsync(cs);
        Assert.Equal(1, await ScratchDatabases.ScalarAsync<int>(cs, DatabaseTriggerCountSql));
    }

    [SkippableFact]
    public async Task Status_reports_logging_suppressed_while_the_audit_table_has_dml_triggers()
    {
        await using var scratch = await ScratchDatabases.CreateAsync(1);
        var cs = scratch.ConnectionStrings[0];
        await InstallAsync(cs);

        await using var conn = new SqlConnection(cs);
        await conn.OpenAsync();
        Assert.Null((await DdlAudit.GetStatusAsync(conn, readOnly: true, CancellationToken.None)).LoggingSuppressed);
        await ExecOnAsync(conn, "CREATE TRIGGER dbo.trg_any ON dbo.DDL_AuditLog AFTER INSERT AS SET NOCOUNT ON;");
        Assert.True((await DdlAudit.GetStatusAsync(conn, readOnly: true, CancellationToken.None)).LoggingSuppressed);
    }

    [SkippableFact]
    public async Task Install_by_a_db_ddladmin_only_user_creates_nothing_and_says_what_is_needed()
    {
        await using var scratch = await ScratchDatabases.CreateAsync(1);
        var cs = scratch.ConnectionStrings[0];
        await ScratchDatabases.ExecAsync(cs, "CREATE USER ddladm WITHOUT LOGIN; ALTER ROLE db_ddladmin ADD MEMBER ddladm;");

        await using (var conn = await OpenAsUserAsync(cs, "ddladm"))
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => DdlAudit.InstallAsync(conn, CancellationToken.None));
            Assert.StartsWith("Creating the DDL_Audit trigger failed: ", ex.Message, StringComparison.Ordinal);
            Assert.Contains("The dbo.DDL_AuditLog this call created was removed again; nothing was left behind.", ex.Message, StringComparison.Ordinal);
            Assert.EndsWith(DdlAudit.PermissionHint, ex.Message, StringComparison.Ordinal);
        }

        Assert.Equal(0, await ScratchDatabases.ScalarAsync<int>(cs, TableExistsSql));
        Assert.Equal(0, await ScratchDatabases.ScalarAsync<int>(cs, DatabaseTriggerCountSql));
        Assert.Equal(0, await ScratchDatabases.ScalarAsync<int>(cs, WriterExistsSql));
    }

    /// <summary>
    /// The pre-flight: a user who may create users and grant, but cannot impersonate DDL_Audit_Writer (15517), gets an
    /// error, and the user and the table this call created are rolled back before any trigger exists.
    /// </summary>
    [SkippableFact]
    public async Task A_failed_pre_flight_rolls_back_the_user_and_the_table_this_call_created()
    {
        await using var scratch = await ScratchDatabases.CreateAsync(1);
        var cs = scratch.ConnectionStrings[0];
        await ScratchDatabases.ExecAsync(cs, """
            CREATE USER lim WITHOUT LOGIN;
            ALTER ROLE db_ddladmin ADD MEMBER lim; ALTER ROLE db_accessadmin ADD MEMBER lim; ALTER ROLE db_securityadmin ADD MEMBER lim;
            """);

        await using (var conn = await OpenAsUserAsync(cs, "lim"))
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => DdlAudit.InstallAsync(conn, CancellationToken.None));
            Assert.Contains("DDL_Audit_Writer", ex.Message, StringComparison.Ordinal);
            var sql = Assert.IsType<SqlException>(ex.InnerException);
            Assert.Equal(15517, sql.Number);
            Assert.Contains("removed again", ex.Message, StringComparison.Ordinal);
        }

        Assert.Equal(0, await ScratchDatabases.ScalarAsync<int>(cs, TableExistsSql));
        Assert.Equal(0, await ScratchDatabases.ScalarAsync<int>(cs, WriterExistsSql));
        Assert.Equal(0, await ScratchDatabases.ScalarAsync<int>(cs, DatabaseTriggerCountSql));
    }

    [Fact]
    public async Task The_shared_trigger_script_is_the_hardened_one()
    {
        var script = await InsightsLayerService.ReadEmbeddedResourceAsync(
            typeof(InsightsLayerService).Assembly, InsightsLayerService.TriggerScriptResource, CancellationToken.None);

        Assert.Contains("WITH EXECUTE AS 'DDL_Audit_Writer'", script, StringComparison.Ordinal);
        Assert.DoesNotContain("EXECUTE AS 'dbo'", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SET XACT_ABORT OFF;", script, StringComparison.Ordinal);
        Assert.Contains("BEGIN CATCH", script, StringComparison.Ordinal);
        Assert.Contains("parent_id = OBJECT_ID(N'dbo.DDL_AuditLog')", script, StringComparison.Ordinal);
        Assert.DoesNotContain("SUSER_SNAME", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("CREATE OR ALTER", script, StringComparison.OrdinalIgnoreCase);
    }
}
