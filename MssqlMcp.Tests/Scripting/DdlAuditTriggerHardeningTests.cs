// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.

using System.Security.Cryptography;
using Microsoft.Data.SqlClient;
using Mssql.McpServer.InsightsLayer;
using Mssql.McpServer.Scripting;

namespace MssqlMcp.Tests.Scripting;

/// <summary>
/// The installed DDL_Audit trigger never blocks another principal's DDL: it runs as dbo, forces the SET options its XML
/// methods need, records ORIGINAL_LOGIN(), and swallows audit-insert failures. LocalDB scratch databases only; the one
/// SQL login these tests create is dropped in finally.
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
            // DENY to public as well: dbo (the trigger's execution context) is exempt from it, a caller is not.
            await ScratchDatabases.ExecAsync(cs, $"""
                CREATE USER [{login}] FOR LOGIN [{login}];
                ALTER ROLE db_ddladmin ADD MEMBER [{login}];
                DENY INSERT ON dbo.DDL_AuditLog TO public;
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

    /// <summary>
    /// The documented limit: an error raised inside a DML trigger that someone put on dbo.DDL_AuditLog dooms the
    /// transaction (DML triggers run with XACT_ABORT ON), so SQL Server rolls the DDL back with error 3616.
    /// </summary>
    [SkippableFact]
    public async Task An_error_inside_a_dml_trigger_on_the_audit_table_still_dooms_the_ddl()
    {
        await using var scratch = await ScratchDatabases.CreateAsync(1);
        var cs = scratch.ConnectionStrings[0];
        await InstallAsync(cs);
        // Conditional: creating this trigger is itself audited, and must not doom its own CREATE.
        await ScratchDatabases.ExecAsync(
            cs,
            "CREATE TRIGGER dbo.trg_audit_ins ON dbo.DDL_AuditLog AFTER INSERT AS IF EXISTS (SELECT 1 FROM inserted WHERE ObjectName = N'doomed') RAISERROR('blocked', 16, 1)");

        var ex = await Assert.ThrowsAsync<SqlException>(() => ScratchDatabases.ExecAsync(cs, "CREATE PROCEDURE dbo.doomed AS SELECT 1"));

        Assert.Contains(ex.Errors.Cast<SqlError>(), e => e.Number == 3616);
        Assert.Equal(0, await ProcedureExistsAsync(cs, "doomed"));
    }

    [SkippableFact]
    public async Task Install_without_impersonate_on_dbo_creates_nothing_and_names_db_owner()
    {
        await using var scratch = await ScratchDatabases.CreateAsync(1);
        var cs = scratch.ConnectionStrings[0];
        await ScratchDatabases.ExecAsync(cs, "CREATE USER ddladm WITHOUT LOGIN; ALTER ROLE db_ddladmin ADD MEMBER ddladm;");

        // Not pooled: an impersonating session must not go back to the pool.
        await using (var conn = new SqlConnection(new SqlConnectionStringBuilder(cs) { Pooling = false }.ConnectionString))
        {
            await conn.OpenAsync();
            await ExecOnAsync(conn, "EXECUTE AS USER = 'ddladm'");
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => DdlAudit.InstallAsync(conn, CancellationToken.None));
            Assert.Equal(
                "Creating DDL_Audit WITH EXECUTE AS 'dbo' needs db_owner (or IMPERSONATE on dbo) on this database; nothing was created.",
                ex.Message);
        }

        Assert.Equal(0, await ScratchDatabases.ScalarAsync<int>(cs, "SELECT CASE WHEN OBJECT_ID(N'dbo.DDL_AuditLog', N'U') IS NULL THEN 0 ELSE 1 END"));
        Assert.Equal(0, await ScratchDatabases.ScalarAsync<int>(cs, "SELECT COUNT(*) FROM sys.triggers WHERE parent_class = 0"));
    }

    [SkippableFact]
    public async Task The_installed_trigger_runs_as_dbo_and_the_new_table_gets_the_object_name_index()
    {
        await using var scratch = await ScratchDatabases.CreateAsync(1);
        var cs = scratch.ConnectionStrings[0];
        await InstallAsync(cs);

        Assert.Equal(
            "dbo",
            await ScratchDatabases.ScalarAsync<string>(cs, "SELECT USER_NAME(m.execute_as_principal_id) FROM sys.triggers t JOIN sys.sql_modules m ON m.object_id = t.object_id WHERE t.parent_class = 0 AND t.name = N'DDL_Audit'"));
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

    [Fact]
    public async Task The_shared_trigger_script_is_the_hardened_one()
    {
        var script = await InsightsLayerService.ReadEmbeddedResourceAsync(
            typeof(InsightsLayerService).Assembly, InsightsLayerService.TriggerScriptResource, CancellationToken.None);

        Assert.Contains("WITH EXECUTE AS 'dbo'", script, StringComparison.Ordinal);
        Assert.Contains("SET XACT_ABORT OFF;", script, StringComparison.Ordinal);
        Assert.Contains("BEGIN CATCH", script, StringComparison.Ordinal);
        Assert.DoesNotContain("SUSER_SNAME", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("CREATE OR ALTER", script, StringComparison.OrdinalIgnoreCase);
    }
}
