// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.

using System.Data;
using System.Data.SqlTypes;
using System.Diagnostics;
using System.Globalization;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Mssql.McpServer.InsightsLayer;

namespace Mssql.McpServer.Scripting;

public sealed record ResultColumn(string Name, string Type);

/// <param name="Batch">1-based index of the batch (in script order) that produced the set.</param>
/// <param name="RowCount">Total rows the server returned; <see cref="Rows"/> holds at most the row cap.</param>
/// <param name="Rows">
/// Cell values as JSON-safe types: decimal, money and <c>bigint</c> beyond +/-2^53 as exact invariant strings, binary as
/// <c>0x</c> + uppercase hex, and over-long strings or binaries cut with a "... (truncated, N chars|bytes)" suffix.
/// </param>
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
/// Size caps for one <see cref="ScriptRunner"/> run. A cap never stops execution; it limits what is kept and returned,
/// and error messages are always kept. Tests override single values with <c>with</c>.
/// </summary>
internal sealed record ScriptRunLimits
{
    public static readonly ScriptRunLimits Default = new();

    /// <summary>Rows kept across all result sets; later rows are only counted.</summary>
    public int MaxTotalRows { get; init; } = ScriptRunner.MaxTotalRows;

    /// <summary>Messages kept; beyond it info and row-count messages are dropped (errors and warnings are kept).</summary>
    public int MaxMessages { get; init; } = ScriptRunner.MaxMessages;

    /// <summary>Result sets returned; later sets are read to the end but not returned.</summary>
    public int MaxResultSets { get; init; } = ScriptRunner.MaxResultSets;

    /// <summary>Characters kept of one string value.</summary>
    public int MaxCellChars { get; init; } = ScriptRunner.MaxCellChars;

    /// <summary>Bytes kept of one binary value (before hex encoding).</summary>
    public int MaxCellBytes { get; init; } = ScriptRunner.MaxCellBytes;

    /// <summary>Approximate response size of kept cells (rendered length x2 + 16 per cell); later rows are only counted.</summary>
    public long MaxResponseBytes { get; init; } = ScriptRunner.MaxResponseBytes;

    /// <summary>Largest <c>GO n</c>; a batch with a larger count is refused with an error and not run.</summary>
    public int MaxRepeatCount { get; init; } = ScriptRunner.MaxRepeatCount;
}

/// <summary>
/// Runs a multi-batch script like SSMS: one session for the whole script, every result set and message collected,
/// SQL errors reported as messages (execution continues with the next batch).
/// </summary>
internal static class ScriptRunner
{
    public const string ReadOnlyRefusedPrefix = "Read-only connection: only a single read-only SELECT per batch can run here.";

    /// <summary>Rows kept per run across all result sets; later rows are only counted.</summary>
    public const int MaxTotalRows = 50_000;

    internal const int MaxMessages = 10_000;
    internal const int MaxResultSets = 200;
    internal const int MaxCellChars = 65_536;
    internal const int MaxCellBytes = 32_768;
    internal const long MaxResponseBytes = 32L * 1024 * 1024;
    internal const int MaxRepeatCount = 10_000;

    public const string ConnectionClosedMessage = "Connection was closed by the server; remaining batches were not run.";

    /// <summary>Largest integer a JavaScript number (IEEE double) holds exactly: 2^53.</summary>
    private const long MaxExactDouble = 9_007_199_254_740_992L;

    /// <summary>Approximate per-cell overhead of the JSON response, next to twice the rendered length.</summary>
    private const int CellOverheadBytes = 16;

    public static async Task<ScriptRunResult> RunAsync(
        SqlConnection conn,
        string script,
        bool readOnly,
        int maxRowsPerResultSet,
        CancellationToken ct,
        ILogger? logger = null,
        ScriptRunLimits? limits = null)
    {
        logger ??= NullLogger.Instance;
        limits ??= ScriptRunLimits.Default;
        var stopwatch = Stopwatch.StartNew();
        var batches = ScriptBatchSplitter.Split(script);
        var run = new RunState(limits);
        var messages = run.Messages;
        var hadErrors = false;
        var startLine = 1;
        var batchText = string.Empty;
        string? definedModule = null;
        var definedModuleResolved = false;

        // Inside a procedure, LineNumber counts from the procedure's text, so it is a script line only when the
        // current batch is that module's own CREATE/ALTER (a compile error); otherwise (e.g. EXEC) there is none.
        int? ScriptLine(SqlError error)
        {
            if (error.LineNumber <= 0)
            {
                return null;
            }

            if (!string.IsNullOrEmpty(error.Procedure))
            {
                if (!definedModuleResolved)
                {
                    definedModule = ScriptBatchSplitter.DefinedModuleName(batchText);
                    definedModuleResolved = true;
                }

                if (definedModule is null || !string.Equals(definedModule, error.Procedure, StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }
            }

            return startLine + error.LineNumber - 1;
        }

        void AddError(SqlError error)
        {
            var line = ScriptLine(error);
            var header = FormatErrorHeader(error.Number, error.Class, error.State, error.Procedure, line ?? error.LineNumber);
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
                    run.AddInfo(new ScriptMessage("info", error.Message, ScriptLine(error)));
                }
            }
        }

        void OnStatementCompleted(object sender, StatementCompletedEventArgs e) =>
            run.AddInfo(new ScriptMessage("rows", e.RecordCount == 1 ? "(1 row affected)" : $"({e.RecordCount} rows affected)", null));

        var previousFireOnUserErrors = conn.FireInfoMessageEventOnUserErrors;
        conn.FireInfoMessageEventOnUserErrors = true;
        conn.InfoMessage += OnInfoMessage;
        try
        {
            for (var b = 0; b < batches.Count; b++)
            {
                var batch = batches[b];
                startLine = batch.StartLine;
                batchText = batch.Text;
                definedModuleResolved = false;

                // SSMS sends comment-only batches; on a read-only connection they would only be refused, so skip them.
                if (readOnly && ScriptBatchSplitter.IsCommentOnly(batch.Text))
                {
                    continue;
                }

                if (batch.RepeatCount > limits.MaxRepeatCount)
                {
                    messages.Add(new ScriptMessage(
                        "error",
                        $"GO {batch.RepeatCount} exceeds the limit of {limits.MaxRepeatCount} repetitions; the batch was not run.",
                        batch.StartLine));
                    hadErrors = true;
                    continue;
                }

                if (readOnly && !SqlStatementClassifier.TryValidateReadOnly(batch.Text, editorWording: true, out var validationError))
                {
                    messages.Add(new ScriptMessage("error", $"{ReadOnlyRefusedPrefix} ({validationError})", batch.StartLine));
                    hadErrors = true;
                    continue;
                }

                for (var repeat = 0; repeat < batch.RepeatCount; repeat++)
                {
                    try
                    {
                        await ExecuteBatchAsync(conn, batch.Text, b + 1, readOnly, maxRowsPerResultSet, run, OnStatementCompleted, logger, ct)
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

                    if (run.RowsExhausted && !run.RowsWarned)
                    {
                        run.RowsWarned = true;
                        messages.Add(new ScriptMessage("warning", $"Row budget of {limits.MaxTotalRows} rows per run reached; later result sets show no rows.", null));
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
            }

            return Finish();
        }
        catch (OperationCanceledException) when (!readOnly)
        {
            // The script may have left a transaction open (with locks) on this still-open connection.
            await RollBackAfterCancellationAsync(conn, logger).ConfigureAwait(false);
            throw;
        }
        finally
        {
            conn.InfoMessage -= OnInfoMessage;
            conn.FireInfoMessageEventOnUserErrors = previousFireOnUserErrors;
        }

        ScriptRunResult Finish() => new(run.ResultSets, messages, hadErrors, batches.Count, stopwatch.ElapsedMilliseconds);
    }

    /// <summary>SSMS-style error header. The <c>, Line n</c> part is omitted when the effective line is 0 or unknown.</summary>
    internal static string FormatErrorHeader(int number, int level, int state, string? procedure, int effectiveLine)
    {
        var header = $"Msg {number}, Level {level}, State {state}";
        if (!string.IsNullOrEmpty(procedure))
        {
            header += $", Procedure {procedure}";
        }

        return effectiveLine > 0 ? $"{header}, Line {effectiveLine}" : header;
    }

    /// <summary>
    /// The value as sent to the extension (which parses JSON into doubles): exact strings where a JSON number would round,
    /// SSMS-style hex for binary, and over-long strings or binaries cut to the cell caps.
    /// </summary>
    internal static object? ToWireValue(object? value, ScriptRunLimits limits) => value switch
    {
        null or DBNull => null,
        string s when s.Length > limits.MaxCellChars =>
            string.Concat(s.AsSpan(0, limits.MaxCellChars), $"… (truncated, {s.Length} chars)"),
        byte[] bytes => BinaryText(bytes, limits.MaxCellBytes),
        decimal d => d.ToString(CultureInfo.InvariantCulture),
        SqlDecimal d => d.ToString(),
        SqlMoney m => m.ToString(),
        long l when l > MaxExactDouble || l < -MaxExactDouble => l.ToString(CultureInfo.InvariantCulture),
        ulong u => u.ToString(CultureInfo.InvariantCulture),
        _ => value,
    };

    private static string BinaryText(byte[] bytes, int maxBytes) => bytes.Length > maxBytes
        ? $"0x{Convert.ToHexString(bytes, 0, maxBytes)}… (truncated, {bytes.Length} bytes)"
        : "0x" + Convert.ToHexString(bytes);

    /// <summary>Approximate JSON cost of one kept cell: its rendered length twice (escaping, UTF-16) plus overhead.</summary>
    private static long CellCost(object? value)
    {
        var length = value switch
        {
            null => 4,
            string s => s.Length,
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture).Length,
            _ => value.ToString()?.Length ?? 0,
        };
        return (2L * length) + CellOverheadBytes;
    }

    private static async Task ExecuteBatchAsync(
        SqlConnection conn,
        string text,
        int batchNumber,
        bool readOnly,
        int maxRows,
        RunState run,
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
                    if (run.ResultSets.Count >= run.Limits.MaxResultSets)
                    {
                        await DrainAsync(reader, ct).ConfigureAwait(false);
                        run.WarnResultSetsCapped();
                    }
                    else
                    {
                        run.ResultSets.Add(await ReadResultSetAsync(reader, batchNumber, maxRows, run, ct).ConfigureAwait(false));
                    }
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

    private static async Task DrainAsync(SqlDataReader reader, CancellationToken ct)
    {
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
        }
    }

    private static async Task<ResultSet> ReadResultSetAsync(SqlDataReader reader, int batchNumber, int maxRows, RunState run, CancellationToken ct)
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
            if (rows.Count >= maxRows || run.BytesExhausted)
            {
                continue;
            }

            if (run.RowsRemaining == 0)
            {
                run.RowsExhausted = true;
                continue;
            }

            var row = new object?[columns.Length];
            long cost = 0;
            for (var i = 0; i < row.Length; i++)
            {
                row[i] = ToWireValue(ReadValue(reader, i), run.Limits);
                cost += CellCost(row[i]);
            }

            if (run.ResponseBytes + cost > run.Limits.MaxResponseBytes)
            {
                run.ExhaustBytes();
                continue;
            }

            run.ResponseBytes += cost;
            run.RowsRemaining--;
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
            // e.g. decimal(38) beyond the .NET decimal range: the Sql* value still has an exact text form.
            try
            {
                return reader.GetSqlValue(ordinal)?.ToString();
            }
            catch (Exception inner) when (inner is not SqlException)
            {
                // e.g. a CLR UDT (hierarchyid, geography) whose assembly is not loaded in this process.
                return $"<{reader.GetDataTypeName(ordinal)}>";
            }
        }
    }

    /// <summary>What one run keeps so far, against its <see cref="ScriptRunLimits"/>; each cap warns once.</summary>
    private sealed class RunState(ScriptRunLimits limits)
    {
        private bool _messagesWarned;
        private bool _resultSetsWarned;

        public ScriptRunLimits Limits { get; } = limits;

        public List<ResultSet> ResultSets { get; } = [];

        public List<ScriptMessage> Messages { get; } = [];

        /// <summary>Rows that may still be kept, across all result sets.</summary>
        public int RowsRemaining { get; set; } = Math.Max(limits.MaxTotalRows, 0);

        /// <summary>True once a row was dropped because the row budget was used up.</summary>
        public bool RowsExhausted { get; set; }

        public bool RowsWarned { get; set; }

        public long ResponseBytes { get; set; }

        /// <summary>True once the response-size budget was exceeded; every later row is only counted.</summary>
        public bool BytesExhausted { get; private set; }

        /// <summary>Adds an info or row-count message unless the message cap is reached (then warns once).</summary>
        public void AddInfo(ScriptMessage message)
        {
            if (Messages.Count < Limits.MaxMessages)
            {
                Messages.Add(message);
                return;
            }

            if (!_messagesWarned)
            {
                _messagesWarned = true;
                Messages.Add(new ScriptMessage("warning", $"Message limit of {Limits.MaxMessages} reached; further info messages were dropped.", null));
            }
        }

        public void WarnResultSetsCapped()
        {
            if (!_resultSetsWarned)
            {
                _resultSetsWarned = true;
                Messages.Add(new ScriptMessage("warning", $"Result-set limit of {Limits.MaxResultSets} reached; later result sets were run but not returned.", null));
            }
        }

        public void ExhaustBytes()
        {
            if (BytesExhausted)
            {
                return;
            }

            BytesExhausted = true;
            Messages.Add(new ScriptMessage(
                "warning",
                $"Response size limit of {FormatSize(Limits.MaxResponseBytes)} reached; later rows were counted but not returned.",
                null));
        }

        private static string FormatSize(long bytes) => bytes >= 1024 * 1024 && bytes % (1024 * 1024) == 0
            ? $"{bytes / (1024 * 1024)} MB"
            : $"{bytes} bytes";
    }

    /// <summary>
    /// Best effort after a cancelled read/write run: end any transaction the script left open so its locks are released even
    /// though the connection stays open. Never throws; the caller rethrows the cancellation.
    /// </summary>
    private static async Task RollBackAfterCancellationAsync(SqlConnection conn, ILogger logger)
    {
        try
        {
            if (conn.State != ConnectionState.Open)
            {
                return;
            }

            await using var rollback = new SqlCommand("IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;", conn) { CommandTimeout = 5 };
            await rollback.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning("Rollback after a cancelled script run failed: {Message}", ex.Message);
        }
    }

    /// <summary>Each run is its own session: a transaction left open would roll back invisibly on dispose, so say so.</summary>
    private static async Task RollBackOpenTransactionsAsync(SqlConnection conn, List<ScriptMessage> messages, CancellationToken ct)
    {
        await using var count = new SqlCommand("SELECT @@TRANCOUNT", conn) { CommandTimeout = 0 };
        var open = Convert.ToInt32(await count.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
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
