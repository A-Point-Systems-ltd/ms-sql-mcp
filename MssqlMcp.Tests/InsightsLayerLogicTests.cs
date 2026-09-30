// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.

using System.Data;
using System.Data.Common;
using Microsoft.Extensions.Logging.Abstractions;
using Mssql.McpServer.InsightsLayer;

namespace MssqlMcp.Tests;

public sealed class InsightsLayerLogicTests
{
    [Fact]
    public void Scan_cursor_restarts_when_batch_is_short()
    {
        Assert.Equal(0, InsightsLayerService.NextFingerprintScanCursor([7, 9, 12], take: 5));
    }

    [Fact]
    public void Scan_cursor_restarts_when_nothing_was_scanned()
    {
        Assert.Equal(0, InsightsLayerService.NextFingerprintScanCursor([], take: 5));
    }

    [Fact]
    public void Scan_cursor_continues_after_last_row_of_full_batch()
    {
        Assert.Equal(12, InsightsLayerService.NextFingerprintScanCursor([3, 7, 12], take: 3));
    }

    [Fact]
    public void Scan_cursor_follows_wrapped_order()
    {
        // Scan order after cursor 10 is: ids above 10 first, then wrapped ids from the start.
        Assert.Equal(4, InsightsLayerService.NextFingerprintScanCursor([11, 15, 2, 4], take: 4));
    }

    [Fact]
    public void Scan_cursor_rotation_covers_every_row()
    {
        // Simulates the SQL ordering (InsightID > cursor first, then wrap) over 12 candidate rows.
        var all = Enumerable.Range(1, 12).ToList();
        const int take = 5;
        var cursor = 0;
        var seen = new HashSet<int>();
        for (var run = 0; run < 3; run++)
        {
            var batch = all.OrderBy(id => id > cursor ? 0 : 1).ThenBy(id => id).Take(take).ToList();
            seen.UnionWith(batch);
            cursor = InsightsLayerService.NextFingerprintScanCursor(batch, take);
        }

        Assert.Equal(all.Count, seen.Count);
    }

    [Fact]
    public void Install_gaps_null_when_everything_exists()
    {
        Assert.Null(InsightsLayerService.DescribeInstallGaps(new InsightsLayerService.InstallState(true, true, true, true, true)));
    }

    [Fact]
    public void Install_gaps_report_missing_trigger()
    {
        var gaps = InsightsLayerService.DescribeInstallGaps(new InsightsLayerService.InstallState(true, true, true, false, false));
        Assert.NotNull(gaps);
        Assert.Contains("DDL_Audit is missing", gaps);
    }

    [Fact]
    public void Install_gaps_report_disabled_trigger()
    {
        var gaps = InsightsLayerService.DescribeInstallGaps(new InsightsLayerService.InstallState(true, true, true, true, false));
        Assert.NotNull(gaps);
        Assert.Contains("disabled", gaps);
    }

    [Fact]
    public void Install_gaps_report_missing_schema_and_audit_table()
    {
        var gaps = InsightsLayerService.DescribeInstallGaps(new InsightsLayerService.InstallState(false, false, false, false, false));
        Assert.NotNull(gaps);
        Assert.Contains("AIInsights schema is missing", gaps);
        Assert.Contains("dbo.DDL_AuditLog is missing", gaps);
    }

    [Theory]
    [InlineData("Completed", "completed")]
    [InlineData("SkippedBusy", "skipped_busy")]
    [InlineData("NotInstalled", "not_installed")]
    [InlineData("Failed", "failed")]
    public void Ddl_processing_outcome_wire_values(string outcome, string expected)
    {
        Assert.Equal(expected, InsightsLayerService.DescribeOutcome(Enum.Parse<InsightsLayerService.DdlProcessingOutcome>(outcome)));
    }

    [Fact]
    public void Upsert_lock_resource_is_bounded_and_case_insensitive()
    {
        var longName = new string('n', 128);
        var a = InsightsLayerService.UpsertLockResource("Table", new string('s', 128), longName, new string('c', 128));
        Assert.True(a.Length <= 255);
        Assert.Equal(
            InsightsLayerService.UpsertLockResource("Table", "dbo", "Orders", null),
            InsightsLayerService.UpsertLockResource("TABLE", "DBO", "orders", null));
        Assert.NotEqual(
            InsightsLayerService.UpsertLockResource("Table", "dbo", "Orders", null),
            InsightsLayerService.UpsertLockResource("Table", "dbo", "Orders", "Amount"));
    }

    [Fact]
    public async Task Rollback_failure_does_not_throw()
    {
        var tx = new ThrowingTransaction();
        await InsightsLayerService.RollbackQuietlyAsync(tx, NullLogger.Instance);
        Assert.True(tx.RollbackAttempted);
    }

    [Fact]
    public async Task Original_exception_survives_rollback_failure()
    {
        var tx = new ThrowingTransaction();
        var ex = await Assert.ThrowsAsync<TimeoutException>(async () =>
        {
            try
            {
                throw new TimeoutException("original");
            }
            catch
            {
                await InsightsLayerService.RollbackQuietlyAsync(tx, NullLogger.Instance);
                throw;
            }
        });
        Assert.Equal("original", ex.Message);
    }

    [Fact]
    public void Trigger_script_is_2008R2_compatible_and_never_replaces_an_existing_trigger()
    {
        var sql = ReadScript("CreateDdlAuditTrigger.sql");

        Assert.DoesNotContain("CREATE OR ALTER", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DROP TRIGGER", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("STRING_AGG", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("'VARCHAR(100)'", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("'NVARCHAR(128)'", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Trigger_script_keeps_last_modified_notice_from_spec()
    {
        var sql = ReadScript("CreateDdlAuditTrigger.sql");
        var batches = SqlBatchSplitter.SplitBatches(sql).ToList();

        Assert.Contains("LastPerUser", sql, StringComparison.Ordinal);
        Assert.Contains("PRINT @s", sql, StringComparison.Ordinal);
        Assert.Contains(batches, b => b.StartsWith("CREATE TRIGGER", StringComparison.OrdinalIgnoreCase));
    }

    private static string ReadScript(string fileName)
    {
        var assembly = typeof(InsightsLayerService).Assembly;
        var name = assembly.GetManifestResourceNames().Single(n => n.EndsWith(fileName, StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private sealed class ThrowingTransaction : DbTransaction
    {
        public bool RollbackAttempted { get; private set; }

        public override IsolationLevel IsolationLevel => IsolationLevel.ReadCommitted;

        protected override DbConnection? DbConnection => null;

        public override void Commit()
        {
        }

        public override void Rollback()
        {
            RollbackAttempted = true;
            throw new InvalidOperationException("connection is broken");
        }
    }
}
