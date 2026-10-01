// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.

using System.Data;
using System.Diagnostics;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Mssql.McpServer.InsightsLayer;

namespace Mssql.McpServer.Scripting;

public sealed record ResultColumn(string Name, string Type);

/// <param name="Batch">1-based index of the batch (in script order) that produced the set.</param>
/// <param name="RowCount">Total rows the server returned; <see cref="Rows"/> holds at most the row cap.</param>
public sealed record ResultSet(int Batch, IReadOnlyList<ResultColumn> Columns, IReadOnlyList<object?[]> Rows, long RowCount, bool Truncated);

/// <param name="Kind"><c>info</c>, <c>rows</c>, <c>error</c> or <c>warning</c>.</param>
/// <param name="Line">1-based line in the submitted script, or null when unknown.</param>
public sealed record ScriptMessage(string Kind, string Text, int? Line);

public sealed record ScriptRunResult(
    IReadOnlyList<ResultSet> ResultSets,
    IReadOnlyList<ScriptMessage> Messages,
    bool HadErrors,
    int Batches,
    long ElapsedMs);

/// <summary>
/// Runs a multi-batch script like SSMS: one session for the whole script, every result set and message collected,
/// SQL errors reported as messages (execution continues with the next batch).
/// </summary>
internal static class ScriptRunner
{
    public const string ReadOnlyRefusedPrefix = "Read-only connection: only a single read-only SELECT per batch can run here.";
    public const string ConnectionClosedMessage = "Connection was closed by the server; remaining batches were not run.";

    public static async Task<ScriptRunResult> RunAsync(
        SqlConnection conn,
        string script,
        bool readOnly,
        int maxRowsPerResultSet,
        CancellationToken ct,
        ILogger? logger = null)
    {
        logger ??= NullLogger.Instance;
        var stopwatch = Stopwatch.StartNew();
        var batches = ScriptBatchSplitter.Split(script);
        var resultSets = new List<ResultSet>();
        var messages = new List<ScriptMessage>();
        var hadErrors = false;
        var startLine = 1;

        void AddError(SqlError error)
        {
            int? line = error.LineNumber > 0 ? startLine + error.LineNumber - 1 : null;
            var header = $"Msg {error.Number}, Level {error.Class}, State {error.State}" + (line is null ? string.Empty : $", Line {line}");
            messages.Add(new ScriptMessage("error", header + Environment.NewLine + error.Message, line));
            hadErrors = true;
        }

        void OnInfoMessage(object sender, SqlInfoMessageEventArgs e)
        {
            foreach (SqlError error in e.Errors)
            {
                if (error.Class > 10)
                {
                    AddError(error);
                }
                else
                {
                    messages.Add(new ScriptMessage("info", error.Message, error.LineNumber > 0 ? startLine + error.LineNumber - 1 : null));
                }
            }
        }

        void OnStatementCompleted(object sender, StatementCompletedEventArgs e) =>
            messages.Add(new ScriptMessage("rows", e.RecordCount == 1 ? "(1 row affected)" : $"({e.RecordCount} rows affected)", null));

        var previousFireOnUserErrors = conn.FireInfoMessageEventOnUserErrors;
        conn.FireInfoMessageEventOnUserErrors = true;
        conn.InfoMessage += OnInfoMessage;
        try
        {
            for (var b = 0; b < batches.Count; b++)
            {
                var batch = batches[b];
                startLine = batch.StartLine;
                if (readOnly && !SqlStatementClassifier.TryValidateReadOnly(batch.Text, out var validationError))
                {
                    messages.Add(new ScriptMessage("error", $"{ReadOnlyRefusedPrefix} ({validationError})", batch.StartLine));
                    hadErrors = true;
                    continue;
                }

                for (var run = 0; run < batch.RepeatCount; run++)
                {
                    try
                    {
                        await ExecuteBatchAsync(conn, batch.Text, b + 1, readOnly, maxRowsPerResultSet, resultSets, OnStatementCompleted, logger, ct)
                            .ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException && ct.IsCancellationRequested)
                    {
                        throw new OperationCanceledException("The script run was cancelled.", ex, ct);
                    }
                    catch (SqlException ex)
                    {
                        foreach (SqlError error in ex.Errors)
                        {
                            AddError(error);
                        }
                    }
                    catch (InvalidOperationException ex)
                    {
                        // e.g. the session broke between batches; reported like a SQL error, then the state check stops the run.
                        messages.Add(new ScriptMessage("error", ex.Message, batch.StartLine));
                        hadErrors = true;
                    }

                    if (conn.State != ConnectionState.Open)
                    {
                        messages.Add(new ScriptMessage("error", ConnectionClosedMessage, null));
                        hadErrors = true;
                        return Finish();
                    }
                }
            }

            if (!readOnly)
            {
                try
                {
                    await RollBackOpenTransactionsAsync(conn, messages, ct).ConfigureAwait(false);
                }
                catch (SqlException ex) when (!ct.IsCancellationRequested)
                {
                    foreach (SqlError error in ex.Errors)
                    {
                        AddError(error);
                    }
                }
            }

            return Finish();
        }
        finally
        {
            conn.InfoMessage -= OnInfoMessage;
            conn.FireInfoMessageEventOnUserErrors = previousFireOnUserErrors;
        }

        ScriptRunResult Finish() => new(resultSets, messages, hadErrors, batches.Count, stopwatch.ElapsedMilliseconds);
    }

    private static async Task ExecuteBatchAsync(
        SqlConnection conn,
        string text,
        int batchNumber,
        bool readOnly,
        int maxRows,
        List<ResultSet> resultSets,
        StatementCompletedEventHandler onStatementCompleted,
        ILogger logger,
        CancellationToken ct)
    {
        // Read-only backstop behind the parser gate: whatever the batch does, nothing is committed.
        await using var tx = readOnly
            ? (SqlTransaction)await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct).ConfigureAwait(false)
            : null;
        try
        {
            await using var cmd = new SqlCommand(text, conn, tx) { CommandTimeout = 0 };
            cmd.StatementCompleted += onStatementCompleted;
            using var cancelRegistration = ct.Register(cmd.Cancel);
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            do
            {
                if (reader.FieldCount > 0)
                {
                    resultSets.Add(await ReadResultSetAsync(reader, batchNumber, maxRows, ct).ConfigureAwait(false));
                }
            }
            while (await reader.NextResultAsync(ct).ConfigureAwait(false));
        }
        finally
        {
            if (tx is not null)
            {
                // Quiet: an error in the batch may already have ended the transaction on the server.
                await InsightsLayerService.RollbackQuietlyAsync(tx, logger).ConfigureAwait(false);
            }
        }
    }

    private static async Task<ResultSet> ReadResultSetAsync(SqlDataReader reader, int batchNumber, int maxRows, CancellationToken ct)
    {
        var columns = new ResultColumn[reader.FieldCount];
        for (var i = 0; i < columns.Length; i++)
        {
            columns[i] = new ResultColumn(reader.GetName(i), reader.GetDataTypeName(i));
        }

        var rows = new List<object?[]>();
        long rowCount = 0;
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            rowCount++;
            if (rows.Count >= maxRows)
            {
                continue;
            }

            var row = new object?[columns.Length];
            for (var i = 0; i < row.Length; i++)
            {
                row[i] = ReadValue(reader, i);
            }

            rows.Add(row);
        }

        return new ResultSet(batchNumber, columns, rows, rowCount, rowCount > rows.Count);
    }

    private static object? ReadValue(SqlDataReader reader, int ordinal)
    {
        try
        {
            return reader.IsDBNull(ordinal) ? null : reader.GetValue(ordinal);
        }
        catch (Exception ex) when (ex is not SqlException)
        {
            // e.g. a CLR UDT (hierarchyid, geography) whose assembly is not loaded in this process.
            return $"<{reader.GetDataTypeName(ordinal)}>";
        }
    }

    /// <summary>Each run is its own session: a transaction left open would roll back invisibly on dispose, so say so.</summary>
    private static async Task RollBackOpenTransactionsAsync(SqlConnection conn, List<ScriptMessage> messages, CancellationToken ct)
    {
        await using var count = new SqlCommand("SELECT @@TRANCOUNT", conn) { CommandTimeout = 0 };
        var open = Convert.ToInt32(await count.ExecuteScalarAsync(ct).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
        if (open <= 0)
        {
            return;
        }

        await using var rollback = new SqlCommand("IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;", conn) { CommandTimeout = 0 };
        await rollback.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        messages.Add(new ScriptMessage(
            "warning",
            $"The script left {open} open transaction(s); they were rolled back. Each run uses a new session - COMMIT in the same run.",
            null));
    }
}
