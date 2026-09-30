// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.

using Microsoft.Data.SqlClient;
using Mssql.McpServer.Scripting;

namespace MssqlMcp.Tests.Scripting;

/// <summary>Creates rich objects in a throwaway LocalDB database, scripts, drops, re-runs the script, re-scripts: identical DDL = complete + executable.</summary>
public sealed class ScriptingRoundTripTests
{
    private const string Server = "Server=(localdb)\\MSSQLLocalDB;Integrated Security=true;TrustServerCertificate=True";

    private static async Task ExecBatchesAsync(string cs, string script)
    {
        await using var conn = new SqlConnection(cs);
        await conn.OpenAsync();
        foreach (var batch in Mssql.McpServer.InsightsLayer.SqlBatchSplitter.SplitBatches(script))
        {
            await using var cmd = new SqlCommand(batch, conn);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    private static async Task<ScriptResult> ScriptAsync(string cs, string type, string name, string? parent = null)
    {
        await using var conn = new SqlConnection(cs);
        await conn.OpenAsync();
        var (result, error) = await ObjectScripter.ScriptAsync(conn, type, name, parent, CancellationToken.None);
        Assert.True(result is not null, error);
        return result!;
    }

    [SkippableFact]
    public async Task Table_type_view_and_module_scripts_round_trip()
    {
        var db = $"McpScript_{Guid.NewGuid():N}"[..24];
        try
        {
            await using (var master = new SqlConnection($"{Server};Initial Catalog=master"))
            {
                try { await master.OpenAsync(); } catch (SqlException ex) { throw new SkipException($"LocalDB unavailable: {ex.Message}"); }
                await using var create = new SqlCommand($"CREATE DATABASE [{db}]", master);
                await create.ExecuteNonQueryAsync();
            }

            var cs = $"{Server};Initial Catalog={db}";
            await ExecBatchesAsync(cs, """
                CREATE SCHEMA sales
                GO
                CREATE TYPE dbo.Phone FROM varchar(20) NOT NULL
                GO
                CREATE TABLE sales.Orders (Id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_Orders PRIMARY KEY CLUSTERED)
                GO
                CREATE TABLE sales.[Order Lines] (
                    Id int IDENTITY(10,5) NOT NULL CONSTRAINT PK_OL PRIMARY KEY CLUSTERED,
                    OrderId int NOT NULL,
                    Sku nvarchar(40) COLLATE Latin1_General_CI_AS NOT NULL,
                    Qty decimal(18,3) NOT NULL CONSTRAINT DF_OL_Qty DEFAULT (1),
                    Total AS (Qty * 2) PERSISTED,
                    Phone dbo.Phone NULL,
                    CONSTRAINT UQ_OL UNIQUE NONCLUSTERED (OrderId ASC, Sku DESC),
                    CONSTRAINT CK_OL_Qty CHECK (Qty >= 0))
                GO
                ALTER TABLE sales.[Order Lines] ADD CONSTRAINT FK_OL_Orders FOREIGN KEY (OrderId) REFERENCES sales.Orders (Id) ON DELETE CASCADE
                GO
                CREATE NONCLUSTERED INDEX IX_OL_Order ON sales.[Order Lines] (OrderId) INCLUDE (Qty) WHERE Qty > 0
                GO
                CREATE TYPE dbo.OrderLineList AS TABLE (Id int NOT NULL PRIMARY KEY, Sku nvarchar(40) NULL)
                GO
                CREATE VIEW sales.vOrders AS SELECT o.Id FROM sales.Orders o
                GO
                CREATE PROCEDURE sales.pGet @Id int AS SELECT Id FROM sales.Orders WHERE Id = @Id
                GO
                CREATE TRIGGER sales.trOL ON sales.[Order Lines] AFTER INSERT AS SET NOCOUNT ON
                """);

            var table = await ScriptAsync(cs, "Table", "sales.Order Lines");
            var ix = await ScriptAsync(cs, "Index", "IX_OL_Order", parent: "sales.Order Lines");
            var fk = await ScriptAsync(cs, "ForeignKey", "sales.FK_OL_Orders");
            var alias = await ScriptAsync(cs, "Type", "dbo.Phone");
            var tvp = await ScriptAsync(cs, "Type", "dbo.OrderLineList");
            var proc = await ScriptAsync(cs, "StoredProcedure", "sales.pGet");
            var trig = await ScriptAsync(cs, "TableTrigger", "sales.trOL");
            Assert.Contains("IX_OL_Order", table.Ddl);
            Assert.StartsWith("CREATE NONCLUSTERED INDEX [IX_OL_Order]", ix.Ddl);
            Assert.Contains("ON DELETE CASCADE", fk.Ddl);
            Assert.Equal(DdlForm.CreateOrAlter, proc.Form);  // LocalDB is 2016 SP1+
            Assert.Contains("CREATE OR ALTER PROCEDURE", proc.Ddl);

            // Drop everything the table script recreates, re-run it, and re-script: must be identical.
            await ExecBatchesAsync(cs, "DROP TABLE sales.[Order Lines]\nGO\nDROP TYPE dbo.OrderLineList\nGO\nDROP TYPE dbo.Phone");
            await ExecBatchesAsync(cs, alias.Ddl);
            await ExecBatchesAsync(cs, tvp.Ddl);
            await ExecBatchesAsync(cs, table.Ddl);
            await ExecBatchesAsync(cs, trig.Ddl);   // CREATE OR ALTER form recreates it
            Assert.Equal(table.Ddl, (await ScriptAsync(cs, "Table", "sales.Order Lines")).Ddl);
            Assert.Equal(tvp.Ddl, (await ScriptAsync(cs, "Type", "dbo.OrderLineList")).Ddl);
            await ExecBatchesAsync(cs, proc.Ddl);   // idempotent re-run
        }
        finally
        {
            await CleanupAsync($"IF DB_ID(N'{db}') IS NOT NULL BEGIN ALTER DATABASE [{db}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{db}]; END");
        }
    }

    [SkippableFact]
    public async Task Ignore_dup_key_not_for_replication_and_filegroup_survive_replay()
    {
        await using var scratch = await ScratchDatabases.CreateAsync(1);
        var cs = scratch.ConnectionStrings[0];
        var db = scratch.Names[0];
        await ExecBatchesAsync(cs, $"""
            DECLARE @path nvarchar(4000) = CONVERT(nvarchar(4000), SERVERPROPERTY('InstanceDefaultDataPath')) + N'{db}_fg2.ndf';
            EXEC(N'ALTER DATABASE [{db}] ADD FILEGROUP [FG2]');
            EXEC(N'ALTER DATABASE [{db}] ADD FILE (NAME = N''{db}_fg2'', FILENAME = N''' + @path + N''') TO FILEGROUP [FG2]');
            GO
            CREATE TABLE dbo.R (
                Id int IDENTITY(1,1) NOT FOR REPLICATION NOT NULL CONSTRAINT PK_R PRIMARY KEY CLUSTERED WITH (IGNORE_DUP_KEY = ON) ON [FG2],
                Code int NOT NULL,
                CONSTRAINT CK_R CHECK NOT FOR REPLICATION (Code > 0)) ON [FG2]
            GO
            CREATE UNIQUE NONCLUSTERED INDEX UX_R_Code ON dbo.R (Code) WITH (IGNORE_DUP_KEY = ON) ON [FG2]
            """);
        const string Probe = """
            SELECT CONCAT(
                (SELECT COUNT(*) FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.R') AND ignore_dup_key = 1), '|',
                (SELECT COUNT(*) FROM sys.identity_columns WHERE object_id = OBJECT_ID(N'dbo.R') AND is_not_for_replication = 1), '|',
                (SELECT COUNT(*) FROM sys.check_constraints WHERE parent_object_id = OBJECT_ID(N'dbo.R') AND is_not_for_replication = 1), '|',
                (SELECT COUNT(*) FROM sys.indexes i JOIN sys.data_spaces ds ON ds.data_space_id = i.data_space_id
                 WHERE i.object_id = OBJECT_ID(N'dbo.R') AND ds.name = N'FG2'));
            """;
        async Task<string> ProbeAsync()
        {
            await using var conn = new SqlConnection(cs);
            await conn.OpenAsync();
            await using var cmd = new SqlCommand(Probe, conn);
            return (string)(await cmd.ExecuteScalarAsync())!;
        }

        var before = await ProbeAsync();
        Assert.Equal("2|1|1|2", before);
        var table = await ScriptAsync(cs, "Table", "dbo.R");
        Assert.Empty(table.Warnings);

        await ExecBatchesAsync(cs, "DROP TABLE dbo.R");
        await ExecBatchesAsync(cs, table.Ddl);
        Assert.Equal(before, await ProbeAsync());
        Assert.Equal(table.Ddl, (await ScriptAsync(cs, "Table", "dbo.R")).Ddl);
    }

    [SkippableFact]
    public async Task Disabled_dml_and_ddl_triggers_replay_as_disabled()
    {
        await using var scratch = await ScratchDatabases.CreateAsync(1);
        var cs = scratch.ConnectionStrings[0];
        await ExecBatchesAsync(cs, """
            CREATE TABLE dbo.T (Id int NOT NULL)
            GO
            CREATE TRIGGER dbo.trOff ON dbo.T AFTER INSERT AS SET NOCOUNT ON
            GO
            CREATE TRIGGER dbo.trOn ON dbo.T AFTER UPDATE AS SET NOCOUNT ON
            GO
            CREATE TRIGGER trDdlOff ON DATABASE FOR CREATE_PROCEDURE AS SET NOCOUNT ON
            GO
            DISABLE TRIGGER dbo.trOff ON dbo.T
            GO
            DISABLE TRIGGER trDdlOff ON DATABASE
            """);

        var dml = await ScriptAsync(cs, "TableTrigger", "dbo.trOff");
        var ddl = await ScriptAsync(cs, "DatabaseTrigger", "trDdlOff");
        var enabled = await ScriptAsync(cs, "TableTrigger", "dbo.trOn");
        Assert.EndsWith("\r\nGO\r\nDISABLE TRIGGER [dbo].[trOff] ON [dbo].[T];\r\nGO", dml.Ddl);
        Assert.EndsWith("\r\nGO\r\nDISABLE TRIGGER [trDdlOff] ON DATABASE;\r\nGO", ddl.Ddl);
        Assert.Contains(Mssql.McpServer.Scripting.ObjectScripter.DisabledTriggerWarning, dml.Warnings);
        Assert.Contains(Mssql.McpServer.Scripting.ObjectScripter.DisabledTriggerWarning, ddl.Warnings);
        Assert.DoesNotContain("DISABLE", enabled.Ddl, StringComparison.Ordinal);
        Assert.DoesNotContain("ENABLE TRIGGER", enabled.Ddl, StringComparison.Ordinal);
        Assert.Empty(enabled.Warnings);

        await ExecBatchesAsync(cs, "DROP TRIGGER dbo.trOff\nGO\nDROP TRIGGER trDdlOff ON DATABASE");
        await ExecBatchesAsync(cs, dml.Ddl);
        await ExecBatchesAsync(cs, ddl.Ddl);
        await using var conn = new SqlConnection(cs);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand("SELECT COUNT(*) FROM sys.triggers WHERE name IN (N'trOff', N'trDdlOff') AND is_disabled = 1;", conn);
        Assert.Equal(2, (int)(await cmd.ExecuteScalarAsync())!);
    }

    [SkippableFact]
    public async Task Orphaned_sql_user_is_scripted_without_login_with_warning_and_loginless_user_without()
    {
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var db = $"McpScriptUsr_{suffix}";
        var login = $"McpOrphan_{suffix}";
        try
        {
            await using (var master = new SqlConnection($"{Server};Initial Catalog=master"))
            {
                try { await master.OpenAsync(); } catch (SqlException ex) { throw new SkipException($"LocalDB unavailable: {ex.Message}"); }
                // Throwaway login with a random password on local LocalDB only; dropped again before scripting and in finally.
                var password = Guid.NewGuid().ToString("N") + "aA1!";
                await using var create = new SqlCommand(
                    $"CREATE DATABASE [{db}]; CREATE LOGIN [{login}] WITH PASSWORD = N'{password}', CHECK_POLICY = OFF;", master);
                await create.ExecuteNonQueryAsync();
            }

            var cs = $"{Server};Initial Catalog={db}";
            await ExecBatchesAsync(cs, $"CREATE USER [orphan] FOR LOGIN [{login}]\nGO\nCREATE USER [loginless] WITHOUT LOGIN");
            await ExecBatchesAsync($"{Server};Initial Catalog=master", $"DROP LOGIN [{login}]");

            var orphan = await ScriptAsync(cs, "DatabaseUser", "orphan");
            const string OrphanWarning = "user [orphan] has no matching login; scripted as CREATE USER [orphan] WITHOUT LOGIN";
            Assert.Equal(OrphanWarning, Assert.Single(orphan.Warnings));
            Assert.Equal($"-- WARNING: {OrphanWarning}\r\nCREATE USER [orphan] WITHOUT LOGIN WITH DEFAULT_SCHEMA = [dbo];", orphan.Ddl);

            var loginless = await ScriptAsync(cs, "DatabaseUser", "loginless");
            Assert.Empty(loginless.Warnings);
            Assert.Equal("CREATE USER [loginless] WITHOUT LOGIN WITH DEFAULT_SCHEMA = [dbo];", loginless.Ddl);
        }
        finally
        {
            await CleanupAsync(
                $"IF DB_ID(N'{db}') IS NOT NULL BEGIN ALTER DATABASE [{db}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{db}]; END; " +
                $"IF SUSER_ID(N'{login}') IS NOT NULL DROP LOGIN [{login}];");
        }
    }

    /// <summary>Best-effort cleanup that never throws, so it cannot mask the test's own failure.</summary>
    private static async Task CleanupAsync(string sql)
    {
        try
        {
            await using var master = new SqlConnection($"{Server};Initial Catalog=master");
            await master.OpenAsync();
            await using var cmd = new SqlCommand(sql, master);
            await cmd.ExecuteNonQueryAsync();
        }
        catch (Exception)
        {
            // Swallowed on purpose: a cleanup failure must not replace the original assertion failure.
        }
    }
}
