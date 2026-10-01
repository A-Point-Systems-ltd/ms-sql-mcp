// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.

using Microsoft.Data.SqlClient;
using Mssql.McpServer.Scripting;

namespace MssqlMcp.Tests.Scripting;

/// <summary>Runs scripts through <see cref="ScriptRunner"/> against a throwaway LocalDB database.</summary>
public sealed class ScriptRunnerTests
{
    private static async Task<ScriptRunResult> RunAsync(string cs, string script, bool readOnly = false, int maxRows = 1000)
    {
        await using var conn = new SqlConnection(cs);
        await conn.OpenAsync();
        return await ScriptRunner.RunAsync(conn, script, readOnly, maxRows, CancellationToken.None);
    }

    private static async Task<(ScratchDatabases Scratch, string Cs)> ScratchAsync()
    {
        var scratch = await ScratchDatabases.CreateAsync(1);
        return (scratch, scratch.ConnectionStrings[0]);
    }

    [SkippableFact]
    public async Task Returns_every_result_set_including_a_zero_row_set_with_columns()
    {
        var (scratch, cs) = await ScratchAsync();
        await using var _ = scratch;

        var result = await RunAsync(cs, "SELECT 1 AS Id UNION ALL SELECT 2;\nSELECT name FROM sys.objects WHERE 1 = 0;\nGO\nSELECT N'x' AS Tag");

        Assert.False(result.HadErrors);
        Assert.Equal(2, result.Batches);
        Assert.Equal(3, result.ResultSets.Count);
        var first = result.ResultSets[0];
        Assert.Equal(1, first.Batch);
        Assert.Equal(new ResultColumn("Id", "int"), Assert.Single(first.Columns));
        Assert.Equal([1, 2], first.Rows.Select(r => (int)r[0]!));
        Assert.Equal(2, first.RowCount);
        Assert.False(first.Truncated);
        var empty = result.ResultSets[1];
        Assert.Equal("name", Assert.Single(empty.Columns).Name);
        Assert.Empty(empty.Rows);
        Assert.Equal(0, empty.RowCount);
        Assert.Equal(2, result.ResultSets[2].Batch);
        Assert.Equal("x", result.ResultSets[2].Rows[0][0]);
    }

    [SkippableFact]
    public async Task Print_becomes_an_info_message()
    {
        var (scratch, cs) = await ScratchAsync();
        await using var _ = scratch;

        var result = await RunAsync(cs, "SELECT 1 AS a\nGO\nPRINT 'hello'");

        var info = Assert.Single(result.Messages, m => m.Kind == "info");
        Assert.Equal("hello", info.Text);
        Assert.Equal(3, info.Line);
        Assert.False(result.HadErrors);
    }

    [SkippableFact]
    public async Task Statement_row_counts_become_rows_messages_and_temp_tables_span_batches()
    {
        var (scratch, cs) = await ScratchAsync();
        await using var _ = scratch;

        var result = await RunAsync(cs, "CREATE TABLE #t (i int);\nINSERT #t VALUES (1), (2);\nGO\nINSERT #t VALUES (3);\nSELECT COUNT(*) AS n FROM #t;");

        Assert.False(result.HadErrors);
        Assert.Equal(
            ["(2 rows affected)", "(1 row affected)", "(1 row affected)"],
            result.Messages.Where(m => m.Kind == "rows").Select(m => m.Text));
        Assert.Equal(3, Assert.Single(result.ResultSets).Rows[0][0]);
    }

    [SkippableFact]
    public async Task Syntax_error_reports_the_script_line_and_later_batches_still_run()
    {
        var (scratch, cs) = await ScratchAsync();
        await using var _ = scratch;

        var result = await RunAsync(cs, "SELECT 1 AS a\nGO\nSELECT 2 AS b\nSELECT )\nGO\nSELECT 3 AS c");

        Assert.True(result.HadErrors);
        var error = Assert.Single(result.Messages, m => m.Kind == "error");
        Assert.Equal(4, error.Line);
        Assert.StartsWith("Msg 102, Level 15, State 1, Line 4" + Environment.NewLine, error.Text, StringComparison.Ordinal);
        Assert.Equal([1, 3], result.ResultSets.Select(s => s.Batch));
        Assert.Equal("c", result.ResultSets[1].Columns[0].Name);
    }

    [SkippableFact]
    public async Task Max_rows_keeps_the_first_rows_and_counts_the_rest()
    {
        var (scratch, cs) = await ScratchAsync();
        await using var _ = scratch;

        var result = await RunAsync(cs, "SELECT v FROM (VALUES (1), (2), (3), (4), (5)) AS t(v) ORDER BY v", maxRows: 2);

        var set = Assert.Single(result.ResultSets);
        Assert.Equal([1, 2], set.Rows.Select(r => (int)r[0]!));
        Assert.Equal(5, set.RowCount);
        Assert.True(set.Truncated);
    }

    [SkippableFact]
    public async Task Read_only_runs_selects_and_refuses_writes()
    {
        var (scratch, cs) = await ScratchAsync();
        await using var _ = scratch;

        var result = await RunAsync(cs, "SELECT 7 AS a\nGO\nCREATE TABLE dbo.X (i int)\nGO\nSELECT 8 AS b", readOnly: true);

        Assert.True(result.HadErrors);
        Assert.Equal([7, 8], result.ResultSets.Select(s => (int)s.Rows[0][0]!));
        var error = Assert.Single(result.Messages, m => m.Kind == "error");
        Assert.StartsWith("Read-only connection: only a single read-only SELECT per batch can run here. (", error.Text, StringComparison.Ordinal);
        Assert.Equal(3, error.Line);
        Assert.Equal(1, await ScratchDatabases.ScalarAsync<int>(cs, "SELECT CASE WHEN OBJECT_ID(N'dbo.X') IS NULL THEN 1 ELSE 0 END"));
    }

    [SkippableFact]
    public async Task Open_transaction_is_rolled_back_with_a_warning()
    {
        var (scratch, cs) = await ScratchAsync();
        await using var _ = scratch;
        await ScratchDatabases.ExecAsync(cs, "CREATE TABLE dbo.T (i int)");

        var result = await RunAsync(cs, "BEGIN TRAN;\nINSERT dbo.T VALUES (1);");

        Assert.False(result.HadErrors);
        var warning = Assert.Single(result.Messages, m => m.Kind == "warning");
        Assert.Equal("The script left 1 open transaction(s); they were rolled back. Each run uses a new session - COMMIT in the same run.", warning.Text);
        Assert.Equal(0, await ScratchDatabases.ScalarAsync<int>(cs, "SELECT COUNT(*) FROM dbo.T"));
    }

    [SkippableFact]
    public async Task Set_options_carry_into_later_batches()
    {
        var (scratch, cs) = await ScratchAsync();
        await using var _ = scratch;

        var result = await RunAsync(cs, "SET QUOTED_IDENTIFIER OFF\nGO\nCREATE PROCEDURE dbo.p AS SELECT \"x\" AS v\nGO");

        Assert.False(result.HadErrors, string.Join(" | ", result.Messages.Select(m => m.Text)));
        Assert.False(await ScratchDatabases.ScalarAsync<bool>(cs, "SELECT uses_quoted_identifier FROM sys.sql_modules WHERE object_id = OBJECT_ID(N'dbo.p')"));
    }

    [SkippableFact]
    public async Task Fatal_error_stops_the_run_when_the_server_closes_the_connection()
    {
        var (scratch, cs) = await ScratchAsync();
        await using var _ = scratch;

        // Severity 20 needs sysadmin (the LocalDB owner is) and makes the server end the session.
        var result = await RunAsync(cs, "SELECT 1 AS a\nGO\nRAISERROR('boom', 20, 1) WITH LOG\nGO\nSELECT 2 AS b");

        Assert.True(result.HadErrors);
        var fatal = Assert.Single(result.Messages, m => m.Text.StartsWith("Msg 50000,", StringComparison.Ordinal));
        Assert.Equal("error", fatal.Kind);
        Assert.StartsWith("Msg 50000, Level 20, State 1, Line 3", fatal.Text, StringComparison.Ordinal);
        Assert.Equal(ScriptRunner.ConnectionClosedMessage, result.Messages[^1].Text);
        Assert.Equal([1], result.ResultSets.Select(s => s.Batch));
    }

    [SkippableFact]
    public async Task Cancellation_propagates()
    {
        var (scratch, cs) = await ScratchAsync();
        await using var _ = scratch;
        await using var conn = new SqlConnection(cs);
        await conn.OpenAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ScriptRunner.RunAsync(conn, "WAITFOR DELAY '00:00:30'", readOnly: false, 1000, cts.Token));
    }

    [SkippableFact]
    public async Task Repeat_count_runs_the_batch_n_times()
    {
        var (scratch, cs) = await ScratchAsync();
        await using var _ = scratch;
        await ScratchDatabases.ExecAsync(cs, "CREATE TABLE dbo.R (i int)");

        var result = await RunAsync(cs, "INSERT dbo.R VALUES (1)\nGO 3");

        Assert.False(result.HadErrors);
        Assert.Equal(1, result.Batches);
        Assert.Equal(3, await ScratchDatabases.ScalarAsync<int>(cs, "SELECT COUNT(*) FROM dbo.R"));
    }
}
