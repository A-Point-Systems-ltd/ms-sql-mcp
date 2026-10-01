// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.

using System.Data;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.Data.SqlClient;
using Mssql.McpServer.InsightsLayer;

namespace Mssql.McpServer.Scripting;

/// <summary><c>ddl_history status</c>: what exists of the DDL history on the current database.</summary>
public sealed record DdlAuditStatus(bool TableExists, bool TriggerExists, bool TriggerEnabled, bool CanInstall);

/// <summary><c>ddl_history install</c>: what this call created. Existing objects are never changed.</summary>
public sealed record DdlAuditInstallResult(
    bool CreatedTable,
    bool CreatedTrigger,
    bool TriggerEnabled,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Warning = null);

/// <summary>One <c>ddl_history list</c> row: the audit metadata without the command text (<see cref="Length"/> is its length).</summary>
public sealed record DdlAuditEntry(
    int Id,
    string PostTime,
    string? LoginName,
    string? HostName,
    string? ProgramName,
    string? EventType,
    string? ObjectType,
    string? SchemaName,
    long? Length);

/// <summary><c>ddl_history get</c>: one audit row with its full command text.</summary>
public sealed record DdlAuditCommand(
    int Id,
    string PostTime,
    string? LoginName,
    string? EventType,
    string? ObjectType,
    string? SchemaName,
    string? ObjectName,
    string? CommandText);

/// <summary>
/// The per-database DDL history behind the extension's <c>ddl_history</c> tool: <c>dbo.DDL_AuditLog</c> filled by the
/// <c>DDL_Audit</c> database trigger. Install only creates what is missing; an existing table or trigger (possibly
/// another team's) is never altered, dropped or enabled. Every read is a plain SELECT, safe on read-only connections.
/// </summary>
internal static class DdlAudit
{
    public const int DefaultTop = 100;
    public const int MaxTop = 500;

    public const string NotInstalledError = "DDL history is not installed on this database (dbo.DDL_AuditLog is missing).";
    public const string DisabledTriggerWarning = "DDL_Audit exists but is disabled; it was left unchanged.";

    private const string TableScriptResource = "Mssql.McpServer.InsightsLayer.SqlScripts.CreateDdlAuditLog.sql";

    private const string StateSql = """
        SELECT
            CASE WHEN OBJECT_ID(N'dbo.DDL_AuditLog', N'U') IS NULL THEN 0 ELSE 1 END,
            (SELECT TOP (1) CAST(t.is_disabled AS int) FROM sys.triggers AS t WHERE t.parent_class = 0 AND t.name = N'DDL_Audit');
        """;

    // ObjectName / SchemaName are varchar(100): the trigger stores LEFT(name, 100), so compare the same way.
    // ID order is insertion order, which PostTime (datetime, ~3 ms) cannot break ties for.
    private const string ListSql = """
        SELECT TOP (@top) ID, PostTime, LoginName, HostName, ProgramName, EventType, ObjectType, SchemaName, LEN(CommandText)
        FROM dbo.DDL_AuditLog
        WHERE ObjectName = LEFT(@name, 100)
          AND (@schema IS NULL OR SchemaName = LEFT(@schema, 100) OR SchemaName IS NULL)
        ORDER BY ID DESC;
        """;

    private const string GetSql = """
        SELECT ID, PostTime, LoginName, EventType, ObjectType, SchemaName, ObjectName, CommandText
        FROM dbo.DDL_AuditLog
        WHERE ID = @id;
        """;

    private readonly record struct State(bool TableExists, bool TriggerExists, bool TriggerEnabled);

    public static async Task<DdlAuditStatus> GetStatusAsync(SqlConnection conn, bool readOnly, CancellationToken cancellationToken)
    {
        var state = await ReadStateAsync(conn, cancellationToken).ConfigureAwait(false);
        return new DdlAuditStatus(
            state.TableExists,
            state.TriggerExists,
            state.TriggerEnabled,
            CanInstall: !readOnly && (!state.TableExists || !state.TriggerExists));
    }

    /// <summary>
    /// Creates <c>dbo.DDL_AuditLog</c> and then the <c>DDL_Audit</c> trigger, each only when missing. Batches run one by
    /// one with no transaction around them. The caller must have refused read-only connections already.
    /// </summary>
    /// <exception cref="InvalidOperationException">The objects are still missing afterwards (for example, no permission).</exception>
    public static async Task<DdlAuditInstallResult> InstallAsync(SqlConnection conn, CancellationToken cancellationToken)
    {
        var before = await ReadStateAsync(conn, cancellationToken).ConfigureAwait(false);
        if (!before.TableExists)
        {
            await RunScriptAsync(conn, TableScriptResource, cancellationToken).ConfigureAwait(false);
        }

        // The script's own ENABLE TRIGGER batch only ever reaches the trigger it has just created.
        if (!before.TriggerExists)
        {
            await RunScriptAsync(conn, InsightsLayerService.TriggerScriptResource, cancellationToken).ConfigureAwait(false);
        }

        var after = await ReadStateAsync(conn, cancellationToken).ConfigureAwait(false);
        if (!after.TableExists || !after.TriggerExists)
        {
            throw new InvalidOperationException(
                $"DDL history install did not complete (table {(after.TableExists ? "present" : "missing")}, trigger {(after.TriggerExists ? "present" : "missing")}). "
                + "Creating a database DDL trigger needs ALTER ANY DATABASE DDL TRIGGER (or db_owner / db_ddladmin).");
        }

        return new DdlAuditInstallResult(
            CreatedTable: !before.TableExists,
            CreatedTrigger: !before.TriggerExists,
            TriggerEnabled: after.TriggerEnabled,
            Warning: before.TriggerExists && !after.TriggerEnabled ? DisabledTriggerWarning : null);
    }

    /// <summary>The newest <paramref name="top"/> entries for one object; null when the table is missing.</summary>
    public static async Task<IReadOnlyList<DdlAuditEntry>?> ListAsync(
        SqlConnection conn, string? schema, string name, int top, CancellationToken cancellationToken)
    {
        if (!(await ReadStateAsync(conn, cancellationToken).ConfigureAwait(false)).TableExists)
        {
            return null;
        }

        await using var cmd = new SqlCommand(ListSql, conn);
        _ = cmd.Parameters.Add(new SqlParameter("@top", SqlDbType.Int) { Value = Math.Clamp(top, 1, MaxTop) });
        _ = cmd.Parameters.Add(new SqlParameter("@name", SqlDbType.NVarChar, 128) { Value = name });
        _ = cmd.Parameters.Add(new SqlParameter("@schema", SqlDbType.NVarChar, 128) { Value = (object?)schema ?? DBNull.Value });

        var entries = new List<DdlAuditEntry>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            entries.Add(new DdlAuditEntry(
                Id: Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture),
                PostTime: FormatTime(reader.GetValue(1)),
                LoginName: Text(reader.GetValue(2)),
                HostName: Text(reader.GetValue(3)),
                ProgramName: Text(reader.GetValue(4)),
                EventType: Text(reader.GetValue(5)),
                ObjectType: Text(reader.GetValue(6)),
                SchemaName: Text(reader.GetValue(7)),
                Length: reader.IsDBNull(8) ? null : Convert.ToInt64(reader.GetValue(8), CultureInfo.InvariantCulture)));
        }

        return entries;
    }

    /// <summary>One entry with its command text. <paramref name="tableExists"/> is false when the table is missing.</summary>
    public static async Task<(bool TableExists, DdlAuditCommand? Command)> GetAsync(SqlConnection conn, int id, CancellationToken cancellationToken)
    {
        if (!(await ReadStateAsync(conn, cancellationToken).ConfigureAwait(false)).TableExists)
        {
            return (false, null);
        }

        await using var cmd = new SqlCommand(GetSql, conn);
        _ = cmd.Parameters.Add(new SqlParameter("@id", SqlDbType.Int) { Value = id });
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return (true, null);
        }

        return (true, new DdlAuditCommand(
            Id: Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture),
            PostTime: FormatTime(reader.GetValue(1)),
            LoginName: Text(reader.GetValue(2)),
            EventType: Text(reader.GetValue(3)),
            ObjectType: Text(reader.GetValue(4)),
            SchemaName: Text(reader.GetValue(5)),
            ObjectName: Text(reader.GetValue(6)),
            CommandText: Text(reader.GetValue(7))));
    }

    private static async Task<State> ReadStateAsync(SqlConnection conn, CancellationToken cancellationToken)
    {
        await using var cmd = new SqlCommand(StateSql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        _ = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        var tableExists = reader.GetInt32(0) == 1;
        var triggerExists = !reader.IsDBNull(1);
        return new State(tableExists, triggerExists, triggerExists && reader.GetInt32(1) == 0);
    }

    private static async Task RunScriptAsync(SqlConnection conn, string resourceName, CancellationToken cancellationToken)
    {
        var assembly = Assembly.GetExecutingAssembly();
        await using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded resource not found: {resourceName}.");
        using var streamReader = new StreamReader(stream, Encoding.UTF8);
        var script = await streamReader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);

        foreach (var batch in SqlBatchSplitter.SplitBatches(script))
        {
            if (string.IsNullOrWhiteSpace(batch))
            {
                continue;
            }

            await using var cmd = new SqlCommand(batch, conn) { CommandTimeout = 120 };
            _ = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static string? Text(object value) =>
        value is DBNull ? null : Convert.ToString(value, CultureInfo.InvariantCulture);

    private static string FormatTime(object value) => value switch
    {
        DateTime dt => dt.ToString("yyyy-MM-dd'T'HH:mm:ss.fff", CultureInfo.InvariantCulture),
        DateTimeOffset dto => dto.ToString("yyyy-MM-dd'T'HH:mm:ss.fffzzz", CultureInfo.InvariantCulture),
        DBNull => string.Empty,
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
    };
}
