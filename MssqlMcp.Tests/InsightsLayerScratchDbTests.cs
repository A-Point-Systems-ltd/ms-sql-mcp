// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.

using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using Mssql.McpServer;
using Mssql.McpServer.Connections;
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

            await AssertEnrichmentRulesAsync(dbo, dbCs, ct);
        }
        finally
        {
            await ExecAsync(serverCs, $"IF DB_ID(N'{dbName}') IS NOT NULL BEGIN ALTER DATABASE [{dbName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{dbName}]; END");
        }
    }

    /// <summary>When the layer asks for upsert_insight: initial baseline, structure change, near-empty table filled.</summary>
    private static async Task AssertEnrichmentRulesAsync(InsightsLayerService dbo, string dbCs, CancellationToken ct)
    {
        const string fillRows = "SELECT TOP ({0}) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) FROM sys.all_objects a CROSS JOIN sys.all_objects b";

        // Non-structural DDL (a CHECK constraint) keeps the authored insight.
        await ExecAsync(dbCs, "CREATE TABLE dbo.Enr1 (Id INT NOT NULL, Qty INT NULL);");
        await dbo.ProcessDdlChangesAsync(ct);
        Assert.True((await dbo.UpsertInsightAsync(NewInsight("Enr1", "authored"), ct)).Success);
        await ExecAsync(dbCs, "ALTER TABLE dbo.Enr1 ADD CONSTRAINT CK_Enr1_Qty CHECK (Qty >= 0);");
        await dbo.ProcessDdlChangesAsync(ct);
        var (kept, keptFreshness) = await dbo.GetInsightForObjectAsync("Table", "dbo", "Enr1", ct);
        Assert.Equal(InsightFreshness.Fresh, keptFreshness);
        Assert.Equal("authored", kept!.Description);
        Assert.Equal(0, kept.RowCountAtAnalysis);
        Assert.Null(await dbo.GetEnrichmentContextAsync(kept, keptFreshness, ct));

        // A new column archives it; the rebuilt baseline asks for an update with the previous text and the DDL.
        await ExecAsync(dbCs, "ALTER TABLE dbo.Enr1 ADD Note NVARCHAR(20) NULL;");
        await dbo.ProcessDdlChangesAsync(ct);
        var (rebuilt, rebuiltFreshness) = await dbo.EnsureBaselineForObjectAsync("Table", "dbo", "Enr1", ct);
        Assert.Equal(InsightsLayerService.AutoMechanicalModel, rebuilt!.LlmModel);
        var changed = await dbo.GetEnrichmentContextAsync(rebuilt, rebuiltFreshness, ct);
        Assert.Equal(InsightEnrichmentTrigger.StructureChanged, changed!.Trigger);
        Assert.Equal("authored", changed.PreviousInsight!.Description);
        Assert.Contains(changed.StructuralEvents, e => e.EventType == "ALTER_TABLE");

        // Drop + identical re-create restores the authored insight: no request.
        Assert.True((await dbo.UpsertInsightAsync(NewInsight("Enr1", "authored v2"), ct)).Success);
        await ExecAsync(dbCs, "DROP TABLE dbo.Enr1; CREATE TABLE dbo.Enr1 (Id INT NOT NULL, Qty INT NULL, Note NVARCHAR(20) NULL);");
        await dbo.ProcessDdlChangesAsync(ct);
        var (restored, restoredFreshness) = await dbo.EnsureBaselineForObjectAsync("Table", "dbo", "Enr1", ct);
        Assert.Equal(InsightFreshness.Fresh, restoredFreshness);
        Assert.Equal("authored v2", restored!.Description);
        Assert.Null(await dbo.GetEnrichmentContextAsync(restored, restoredFreshness, ct));

        // Written while empty, now 150 rows: re-evaluate.
        await ExecAsync(dbCs, "INSERT INTO dbo.Enr1 (Id) " + string.Format(System.Globalization.CultureInfo.InvariantCulture, fillRows, 150) + ";");
        var (grown, grownFreshness) = await dbo.GetInsightForObjectAsync("Table", "dbo", "Enr1", ct);
        var populated = await dbo.GetEnrichmentContextAsync(grown!, grownFreshness, ct);
        Assert.Equal(InsightEnrichmentTrigger.DataPopulated, populated!.Trigger);
        Assert.True(populated.RowsNow >= InsightsLayerService.DataPopulatedRowThreshold);

        // Written with 150 rows: further growth never asks again.
        Assert.True((await dbo.UpsertInsightAsync(NewInsight("Enr1", "authored v3"), ct)).Success);
        await ExecAsync(dbCs, "INSERT INTO dbo.Enr1 (Id) " + string.Format(System.Globalization.CultureInfo.InvariantCulture, fillRows, 500) + ";");
        var (big, bigFreshness) = await dbo.GetInsightForObjectAsync("Table", "dbo", "Enr1", ct);
        Assert.Equal(150, big!.RowCountAtAnalysis);
        Assert.Null(await dbo.GetEnrichmentContextAsync(big, bigFreshness, ct));

        // A restore keeps the original row count and analysis time: written at 150, re-created empty, refilled.
        await ExecAsync(dbCs, "DROP TABLE dbo.Enr1; CREATE TABLE dbo.Enr1 (Id INT NOT NULL, Qty INT NULL, Note NVARCHAR(20) NULL);");
        var (again, againFreshness) = await dbo.EnsureBaselineForObjectAsync("Table", "dbo", "Enr1", ct);
        Assert.Equal("authored v3", again!.Description);
        Assert.Equal(150, again.RowCountAtAnalysis);
        Assert.Equal(big.LastAnalyzed, again.LastAnalyzed);
        await ExecAsync(dbCs, "INSERT INTO dbo.Enr1 (Id) " + string.Format(System.Globalization.CultureInfo.InvariantCulture, fillRows, 150) + ";");
        (again, againFreshness) = await dbo.GetInsightForObjectAsync("Table", "dbo", "Enr1", ct);
        Assert.Null(await dbo.GetEnrichmentContextAsync(again!, againFreshness, ct));

        // A fresh baseline whose structure matches an authored insight in history is restored the same day
        // (databases upgraded from the archive-on-any-DDL behaviour), never reported as StructureChanged.
        await ExecAsync(dbCs, """
            UPDATE AIInsights.SchemaInsights
            SET LLMModel = N'auto-mechanical', Description = N'over-archived baseline', Confidence = 0.30
            WHERE ObjectName = N'Enr1' AND ColumnName IS NULL;
            """);
        var (upgraded, upgradedFreshness) = await dbo.EnsureBaselineForObjectAsync("Table", "dbo", "Enr1", ct);
        Assert.Equal("authored v3", upgraded!.Description);
        Assert.Null(await dbo.GetEnrichmentContextAsync(upgraded, upgradedFreshness, ct));

        // Views: a baseline never counts (it would run the view); an authored insight written on an empty view
        // asks again once the view reaches 100 rows, and the check is throttled.
        await ExecAsync(dbCs, "CREATE TABLE dbo.EnrSrc (Id INT NOT NULL);");
        await ExecAsync(dbCs, "CREATE VIEW dbo.EnrV AS SELECT Id FROM dbo.EnrSrc;");
        var (viewBaseline, _) = await dbo.EnsureBaselineForObjectAsync("View", "dbo", "EnrV", ct);
        Assert.Equal(InsightsLayerService.AutoMechanicalModel, viewBaseline!.LlmModel);
        Assert.Null(viewBaseline.RowCountAtAnalysis);
        Assert.True((await dbo.UpsertInsightAsync(NewInsight("EnrV", "authored view", "View"), ct)).Success);
        var (viewInsight, viewFreshness) = await dbo.GetInsightForObjectAsync("View", "dbo", "EnrV", ct);
        Assert.Equal(0, viewInsight!.RowCountAtAnalysis);
        await ExecAsync(dbCs, "INSERT INTO dbo.EnrSrc (Id) " + string.Format(System.Globalization.CultureInfo.InvariantCulture, fillRows, 120) + ";");
        var viewPopulated = await dbo.GetEnrichmentContextAsync(viewInsight, viewFreshness, ct);
        Assert.Equal(InsightEnrichmentTrigger.DataPopulated, viewPopulated!.Trigger);
        Assert.Equal(101, viewPopulated.RowsNow);
        Assert.Null(await dbo.GetEnrichmentContextAsync(viewInsight, viewFreshness, ct));

        // A brand-new object with no authored history.
        await ExecAsync(dbCs, "CREATE TABLE dbo.Enr2 (Id INT NOT NULL);");
        var (baseline, baselineFreshness) = await dbo.EnsureBaselineForObjectAsync("Table", "dbo", "Enr2", ct);
        Assert.Equal(InsightEnrichmentTrigger.InitialBaselineOnly, (await dbo.GetEnrichmentContextAsync(baseline!, baselineFreshness, ct))!.Trigger);

        // Read-only connections refuse upsert_insight, so they are never asked for one.
        using (CurrentConnection.Use(new ConnectionProfile("ro", dbCs, ReadOnly: true, InsightsEnabled: true, ConnectionSource.Configured)))
        {
            var (roBaseline, roFreshness) = await dbo.GetInsightForObjectAsync("Table", "dbo", "Enr2", ct);
            Assert.NotNull(roBaseline);
            Assert.Null(await dbo.GetEnrichmentContextAsync(roBaseline!, roFreshness, ct));
        }

        // Without dbo.DDL_AuditLog the fingerprint still detects the change; there are just no events to show.
        await ExecAsync(dbCs, "DISABLE TRIGGER [DDL_Audit] ON DATABASE; DROP TABLE dbo.DDL_AuditLog;");
        Assert.True((await dbo.UpsertInsightAsync(NewInsight("Enr2", "authored"), ct)).Success);
        await ExecAsync(dbCs, "ALTER TABLE dbo.Enr2 ADD Extra INT NULL;");
        var (noAudit, noAuditFreshness) = await dbo.EnsureBaselineForObjectAsync("Table", "dbo", "Enr2", ct);
        var noAuditContext = await dbo.GetEnrichmentContextAsync(noAudit!, noAuditFreshness, ct);
        Assert.Equal(InsightEnrichmentTrigger.StructureChanged, noAuditContext!.Trigger);
        Assert.Empty(noAuditContext.StructuralEvents);

        // An install that predates RowCountAtAnalysis keeps working: read, upsert, archive, restore and context,
        // with unknown row counts and therefore no DataPopulated trigger. A new service has no cached column check.
        await ExecAsync(dbCs, """
            ALTER TABLE AIInsights.SchemaInsights DROP COLUMN RowCountAtAnalysis;
            ALTER TABLE AIInsights.InsightHistory DROP COLUMN RowCountAtAnalysis;
            """);
        var legacy = new InsightsLayerService(new FixedFactory(dbCs), NullLogger<InsightsLayerService>.Instance);
        await ExecAsync(dbCs, "CREATE TABLE dbo.Leg1 (Id INT NOT NULL);");
        var (legacyBaseline, legacyBaselineFreshness) = await legacy.EnsureBaselineForObjectAsync("Table", "dbo", "Leg1", ct);
        Assert.Equal(InsightEnrichmentTrigger.InitialBaselineOnly, (await legacy.GetEnrichmentContextAsync(legacyBaseline!, legacyBaselineFreshness, ct))!.Trigger);
        Assert.True((await legacy.UpsertInsightAsync(NewInsight("Leg1", "legacy authored"), ct)).Success);
        var (legacyAuthored, legacyAuthoredFreshness) = await legacy.GetInsightForObjectAsync("Table", "dbo", "Leg1", ct);
        Assert.Null(legacyAuthored!.RowCountAtAnalysis);
        await ExecAsync(dbCs, "INSERT INTO dbo.Leg1 (Id) " + string.Format(System.Globalization.CultureInfo.InvariantCulture, fillRows, 150) + ";");
        Assert.Null(await legacy.GetEnrichmentContextAsync(legacyAuthored, legacyAuthoredFreshness, ct));
        await ExecAsync(dbCs, "ALTER TABLE dbo.Leg1 ADD Extra INT NULL;");
        var (legacyChanged, legacyChangedFreshness) = await legacy.EnsureBaselineForObjectAsync("Table", "dbo", "Leg1", ct);
        Assert.Equal(InsightEnrichmentTrigger.StructureChanged, (await legacy.GetEnrichmentContextAsync(legacyChanged!, legacyChangedFreshness, ct))!.Trigger);
        await ExecAsync(dbCs, "ALTER TABLE dbo.Leg1 DROP COLUMN Extra;");
        var (legacyRestored, _) = await legacy.EnsureBaselineForObjectAsync("Table", "dbo", "Leg1", ct);
        Assert.Equal("legacy authored", legacyRestored!.Description);
        var history = await legacy.GetHistoryAsync("dbo", "Leg1", 10, ct);
        Assert.True(history.Success, history.Error);
    }

    private static SchemaInsight NewInsight(string table, string description, string objectType = "Table") => new()
    {
        ObjectType = objectType,
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

        public Task<SqlConnection> GetOpenUnpooledConnectionAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException("Insights tests use pooled connections only.");
    }
}
