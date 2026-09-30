// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.

using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using Mssql.McpServer;
using Mssql.McpServer.InsightsLayer;
using Mssql.McpServer.InsightsLayer.Models;

namespace MssqlMcp.Tests;

/// <summary>
/// Live checks for install verification, trigger upgrade, concurrency and visibility-safe archiving.
/// Opt-in: set <c>RUN_INSIGHTS_SCRATCH_DB_TEST=1</c> and <c>INSIGHTS_SCRATCH_SQL_SERVER</c> (e.g. <c>.\SQLEXPRESS</c>).
/// Creates a throwaway database named <c>McpQaScratch_*</c> with Windows auth, and drops it at the end.
/// Never points at an existing database.
/// </summary>
[Collection(EnvVarLock.Name)]
public sealed class InsightsLayerScratchDbTests
{
    private const string LowPrivUser = "mcp_lowpriv";

    [SkippableFact]
    public async Task Install_upgrade_concurrency_and_visibility_against_scratch_database()
    {
        Skip.IfNot(string.Equals(Environment.GetEnvironmentVariable("RUN_INSIGHTS_SCRATCH_DB_TEST"), "1", StringComparison.Ordinal));
        var server = Environment.GetEnvironmentVariable("INSIGHTS_SCRATCH_SQL_SERVER");
        Skip.If(string.IsNullOrWhiteSpace(server), "INSIGHTS_SCRATCH_SQL_SERVER is not set.");

        var dbName = "McpQaScratch_" + Guid.NewGuid().ToString("N")[..10];
        var serverCs = new SqlConnectionStringBuilder
        {
            DataSource = server,
            IntegratedSecurity = true,
            TrustServerCertificate = true,
            InitialCatalog = "master"
        }.ConnectionString;
        var dbCs = new SqlConnectionStringBuilder(serverCs) { InitialCatalog = dbName }.ConnectionString;

        await ExecAsync(serverCs, $"CREATE DATABASE [{dbName}];");
        try
        {
            var dbo = new InsightsLayerService(new FixedFactory(dbCs), NullLogger<InsightsLayerService>.Instance);
            var ct = CancellationToken.None;

            // NET-020: audit table + trigger present, AIInsights absent.
            await CreateLegacyAuditObjectsAsync(dbCs);
            var preStatus = await dbo.GetStatusAsync(ct);
            Assert.False(preStatus.AiInsightsSchemaExists);
            Assert.True(preStatus.DdlAuditTableExists);
            Assert.True(preStatus.DdlAuditTriggerEnabled);

            // NET-003 / NET-006: install verifies, and never replaces an existing (possibly client-owned) trigger.
            var legacyDefinition = await ScalarAsync<string>(dbCs, "SELECT OBJECT_DEFINITION(object_id) FROM sys.triggers WHERE name = N'DDL_Audit' AND parent_class = 0;");
            var install = await dbo.InstallLayerAsync(ct);
            Assert.True(install.Success, install.Error);
            var definition = await ScalarAsync<string>(dbCs, "SELECT OBJECT_DEFINITION(object_id) FROM sys.triggers WHERE name = N'DDL_Audit' AND parent_class = 0;");
            Assert.Equal(legacyDefinition, definition);

            // A fresh install gets the current trigger (nvarchar-safe names).
            await ExecAsync(dbCs, "DROP TRIGGER [DDL_Audit] ON DATABASE;");
            var freshInstall = await dbo.InstallLayerAsync(ct);
            Assert.True(freshInstall.Success, freshInstall.Error);
            definition = await ScalarAsync<string>(dbCs, "SELECT OBJECT_DEFINITION(object_id) FROM sys.triggers WHERE name = N'DDL_Audit' AND parent_class = 0;");
            Assert.Contains("NVARCHAR(128)", definition, StringComparison.OrdinalIgnoreCase);

            var reinstall = await dbo.InstallLayerAsync(ct);
            Assert.True(reinstall.Success, reinstall.Error);
            var status = await dbo.GetStatusAsync(ct);
            Assert.True(status.AiInsightsSchemaExists && status.DdlAuditTableExists && status.DdlAuditTriggerEnabled);

            // NET-006: a 120-character name must not break the caller's DDL.
            var longName = "T" + new string('x', 119);
            await ExecAsync(dbCs, $"CREATE TABLE dbo.[{longName}] (Id INT NOT NULL);");
            var auditedLength = await ScalarAsync<int>(dbCs, "SELECT TOP (1) LEN(ObjectName) FROM dbo.DDL_AuditLog WHERE EventType = 'CREATE_TABLE' ORDER BY ID DESC;");
            Assert.Equal(100, auditedLength);

            // NET-007: parallel upserts of one key produce exactly one row.
            await ExecAsync(dbCs, "CREATE TABLE dbo.Orders (Id INT NOT NULL PRIMARY KEY, Amount DECIMAL(10,2) NULL);");
            await dbo.ProcessDdlChangesAsync(ct);
            var upserts = await Task.WhenAll(Enumerable.Range(0, 20).Select(i => dbo.UpsertInsightAsync(NewInsight("Orders", $"parallel {i}"), ct)));
            Assert.All(upserts, u => Assert.True(u.Success, u.Error));
            Assert.Equal(1, await ScalarAsync<int>(dbCs, "SELECT COUNT(*) FROM AIInsights.SchemaInsights WHERE ObjectName = N'Orders' AND ColumnName IS NULL;"));

            // NET-007: parallel upserts of different new keys all succeed (range-lock deadlocks are retried).
            for (var i = 0; i < 10; i++)
            {
                await ExecAsync(dbCs, $"CREATE TABLE dbo.P{i} (Id INT NOT NULL);");
            }

            await dbo.ProcessDdlChangesAsync(ct);
            var distinct = await Task.WhenAll(Enumerable.Range(0, 10).Select(i => dbo.UpsertInsightAsync(NewInsight($"P{i}", "parallel distinct"), ct)));
            Assert.All(distinct, u => Assert.True(u.Success, u.Error));
            Assert.Equal(10, await ScalarAsync<int>(dbCs, "SELECT COUNT(*) FROM AIInsights.SchemaInsights WHERE Description = N'parallel distinct';"));

            // NET-007: a run that finds the app lock held is skipped.
            await using (var holder = new SqlConnection(dbCs))
            {
                await holder.OpenAsync(ct);
                await using (var lockCmd = new SqlCommand("EXEC sp_getapplock @Resource = N'AIInsights.ProcessDdlChanges', @LockMode = N'Exclusive', @LockOwner = N'Session', @LockTimeout = 0;", holder))
                {
                    _ = await lockCmd.ExecuteNonQueryAsync(ct);
                }

                var busy = await dbo.RefreshInsightsAsync(ct);
                Assert.True(busy.Success, busy.Error);
                Assert.Equal("skipped_busy", ReadProperty(busy.Data, "ddlProcessing"));
            }

            var done = await dbo.RefreshInsightsAsync(ct);
            Assert.Equal("completed", ReadProperty(done.Data, "ddlProcessing"));

            // NET-008: with no audit rows pending, the scan catches a change the trigger did not log.
            await ExecAsync(dbCs, "DISABLE TRIGGER [DDL_Audit] ON DATABASE;");
            await ExecAsync(dbCs, "ALTER TABLE dbo.Orders ADD Note NVARCHAR(50) NULL;");
            await ExecAsync(dbCs, "ENABLE TRIGGER [DDL_Audit] ON DATABASE;");
            await dbo.ProcessDdlChangesAsync(ct);
            Assert.Equal(1, await ScalarAsync<int>(dbCs, "SELECT COUNT(*) FROM AIInsights.InsightHistory WHERE ObjectName = N'Orders' AND ArchivedByEvent = N'FingerprintScan' AND ArchiveReason = N'FingerprintMismatch';"));

            // NET-014: a login that cannot see dbo.Orders must not archive its insight.
            await dbo.ProcessDdlChangesAsync(ct);
            var upsert = await dbo.UpsertInsightAsync(NewInsight("Orders", "curated"), ct);
            Assert.True(upsert.Success, upsert.Error);
            await ExecAsync(dbCs, $"CREATE USER [{LowPrivUser}] WITHOUT LOGIN; GRANT SELECT, INSERT, UPDATE, DELETE ON SCHEMA::AIInsights TO [{LowPrivUser}];");
            await dbo.ProcessDdlChangesAsync(ct);
            var before = await ScalarAsync<int>(dbCs, "SELECT COUNT(*) FROM AIInsights.SchemaInsights WHERE ObjectName = N'Orders';");

            var lowPriv = new InsightsLayerService(new FixedFactory(new SqlConnectionStringBuilder(dbCs) { Pooling = false }.ConnectionString, LowPrivUser), NullLogger<InsightsLayerService>.Instance);
            var (insight, freshness) = await lowPriv.GetInsightForObjectAsync("Table", "dbo", "Orders", ct);
            Assert.Equal(InsightFreshness.AccessDenied, freshness);
            Assert.NotNull(insight);
            await lowPriv.ProcessDdlChangesAsync(ct);
            Assert.Equal(before, await ScalarAsync<int>(dbCs, "SELECT COUNT(*) FROM AIInsights.SchemaInsights WHERE ObjectName = N'Orders';"));

            // NET-003: install without DDL permissions reports failure instead of installed=true.
            var deniedInstall = await lowPriv.InstallLayerAsync(ct);
            Assert.False(deniedInstall.Success);
        }
        finally
        {
            await ExecAsync(serverCs, $"IF DB_ID(N'{dbName}') IS NOT NULL BEGIN ALTER DATABASE [{dbName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{dbName}]; END");
        }
    }

    private static SchemaInsight NewInsight(string table, string description) => new()
    {
        ObjectType = "Table",
        SchemaName = "dbo",
        ObjectName = table,
        Description = description,
        LlmModel = "scratch-test",
        Confidence = 0.5m,
        AnalyzedBy = nameof(InsightsLayerScratchDbTests),
        Version = 1
    };

    private static string? ReadProperty(object? data, string name) =>
        data?.GetType().GetProperty(name)?.GetValue(data) as string;

    /// <summary>The pre-upgrade trigger shape: varchar(100) extraction plus the LastPerUser/PRINT block.</summary>
    private static async Task CreateLegacyAuditObjectsAsync(string dbCs)
    {
        await ExecAsync(dbCs, """
            CREATE TABLE dbo.DDL_AuditLog (
                ID INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
                PostTime DATETIME NOT NULL DEFAULT (GETDATE()),
                HostName VARCHAR(100) NULL, LoginName VARCHAR(100) NULL, SchemaName VARCHAR(100) NULL,
                ObjectName VARCHAR(100) NULL, ObjectType VARCHAR(100) NULL, EventType VARCHAR(64) NULL,
                CommandText NVARCHAR(MAX) NULL, CommandXML XML NULL, ProgramName VARCHAR(100) NULL);
            """);
        await ExecAsync(dbCs, """
            CREATE TRIGGER [DDL_Audit] ON DATABASE FOR ddl_database_level_events AS
            SET NOCOUNT ON
            DECLARE @x XML = EVENTDATA();
            INSERT INTO dbo.DDL_AuditLog (ObjectName, EventType)
            VALUES (@x.value('(/EVENT_INSTANCE/ObjectName)[1]', 'VARCHAR(100)'), @x.value('(/EVENT_INSTANCE/EventType)[1]', 'VARCHAR(64)'));
            DECLARE @s NVARCHAR(MAX) = N'LastPerUser';
            IF @@rowcount > 0 PRINT @s
            """);
    }

    private static async Task ExecAsync(string cs, string sql)
    {
        await using var conn = new SqlConnection(cs);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 120 };
        _ = await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<T> ScalarAsync<T>(string cs, string sql)
    {
        await using var conn = new SqlConnection(cs);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(sql, conn);
        return (T)(await cmd.ExecuteScalarAsync())!;
    }

    private sealed class FixedFactory(string connectionString, string? executeAsUser = null) : ISqlConnectionFactory
    {
        public async Task<SqlConnection> GetOpenConnectionAsync(CancellationToken cancellationToken)
        {
            var conn = new SqlConnection(connectionString);
            await conn.OpenAsync(cancellationToken);
            if (executeAsUser is not null)
            {
                await using var cmd = new SqlCommand("EXECUTE AS USER = @User;", conn);
                cmd.Parameters.AddWithValue("@User", executeAsUser);
                _ = await cmd.ExecuteNonQueryAsync(cancellationToken);
            }

            return conn;
        }
    }
}
