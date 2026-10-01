// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.

using System.Data;
using System.Globalization;
using System.Reflection;
using System.Text.Json.Serialization;
using Microsoft.Data.SqlClient;
using Mssql.McpServer.InsightsLayer;

namespace Mssql.McpServer.Scripting;

/// <summary>
/// <c>ddl_history status</c>: what exists of the DDL history on the current database. <see cref="TableCompatible"/> is
/// true only for an existing table the trigger can insert into (false when the table is missing).
/// </summary>
public sealed record DdlAuditStatus(bool TableExists, bool TableCompatible, bool TriggerExists, bool TriggerEnabled, bool CanInstall, string? ServerName = null, string? DatabaseName = null, IReadOnlyList<string>? Warnings = null);

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

    /// <summary>The columns the DDL_Audit trigger's INSERT names, in the order the incompatibility error lists them.</summary>
    internal static readonly IReadOnlyList<string> TriggerColumns =
        ["HostName", "LoginName", "SchemaName", "ObjectName", "ObjectType", "EventType", "CommandText", "CommandXML", "ProgramName"];

    private const string TableScriptResource = "Mssql.McpServer.InsightsLayer.SqlScripts.CreateDdlAuditLog.sql";

    // The server's own names for the target, so the extension's consent modal never relies on profile parsing.
    private const string StateSql = """
        SELECT
            CASE WHEN OBJECT_ID(N'dbo.DDL_AuditLog', N'U') IS NULL THEN 0 ELSE 1 END,
            (SELECT TOP (1) CAST(t.is_disabled AS int) FROM sys.triggers AS t WHERE t.parent_class = 0 AND t.name = N'DDL_Audit'),
            CAST(COALESCE(@@SERVERNAME, SERVERPROPERTY('ServerName')) AS nvarchar(256)),
            DB_NAME();
        """;

    // TYPE_NAME(system_type_id): alias types resolve to their base type; rowversion reads as timestamp.
    private const string ColumnsSql = """
        SELECT c.name, TYPE_NAME(c.system_type_id), c.max_length, c.is_nullable, c.is_identity, c.is_computed,
               CAST(CASE WHEN c.default_object_id <> 0 THEN 1 ELSE 0 END AS bit)
        FROM sys.columns AS c
        WHERE c.object_id = OBJECT_ID(N'dbo.DDL_AuditLog', N'U')
        ORDER BY c.column_id;
        """;

    // CREATE TRIGGER ... WITH EXECUTE AS 'dbo' fails with 15151 without this (db_owner has it).
    private const string CanImpersonateDboSql = "SELECT HAS_PERMS_BY_NAME(N'dbo', N'USER', N'IMPERSONATE');";

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

    /// <param name="Incompatibilities">Why the trigger's INSERT would fail on the existing table; empty when it would work or the table is missing.</param>
    /// <param name="Warnings">Compatible but lossy column choices of the existing table.</param>
    private sealed record State(
        bool TableExists,
        bool TriggerExists,
        bool TriggerEnabled,
        IReadOnlyList<string> Incompatibilities,
        IReadOnlyList<string> Warnings,
        string? ServerName,
        string? DatabaseName);

    public static async Task<DdlAuditStatus> GetStatusAsync(SqlConnection conn, bool readOnly, CancellationToken cancellationToken)
    {
        var state = await ReadStateAsync(conn, cancellationToken).ConfigureAwait(false);
        var compatible = state.TableExists && state.Incompatibilities.Count == 0;
        return new DdlAuditStatus(
            state.TableExists,
            TableCompatible: compatible,
            state.TriggerExists,
            state.TriggerEnabled,
            CanInstall: !readOnly && (!state.TableExists || (compatible && !state.TriggerExists)),
            state.ServerName,
            state.DatabaseName,
            Warnings: state.Warnings.Count > 0 ? state.Warnings : null);
    }

    /// <summary>
    /// Creates <c>dbo.DDL_AuditLog</c> and then the <c>DDL_Audit</c> trigger, each only when missing. Batches run one by
    /// one with no transaction around them. The caller must have refused read-only connections already.
    /// </summary>
    /// <param name="triggerScript">Test hook: trigger script text to run instead of the embedded one.</param>
    /// <exception cref="InvalidOperationException">
    /// The existing table is incompatible (nothing created), the trigger failed after the table was created, or the
    /// objects are still missing afterwards (for example, no permission).
    /// </exception>
    public static async Task<DdlAuditInstallResult> InstallAsync(SqlConnection conn, CancellationToken cancellationToken, string? triggerScript = null)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var before = await ReadStateAsync(conn, cancellationToken).ConfigureAwait(false);

        // On an incompatible table the trigger's INSERT would fail and roll back every later DDL statement: create nothing.
        if (before.TableExists && !before.TriggerExists && before.Incompatibilities.Count > 0)
        {
            throw new InvalidOperationException(IncompatibleTableError(before.Incompatibilities));
        }

        // Checked before anything is created, so a missing permission never leaves a table without its trigger.
        if (!before.TriggerExists && !await CanImpersonateDboAsync(conn, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException(ImpersonateDboError);
        }

        if (!before.TableExists)
        {
            var tableSql = await InsightsLayerService.ReadEmbeddedResourceAsync(assembly, TableScriptResource, cancellationToken).ConfigureAwait(false);
            await InsightsLayerService.ExecuteBatchesAsync(conn, tableSql, cancellationToken).ConfigureAwait(false);
        }

        // The script's own ENABLE TRIGGER batch only ever reaches the trigger it has just created.
        if (!before.TriggerExists)
        {
            try
            {
                triggerScript ??= await InsightsLayerService
                    .ReadEmbeddedResourceAsync(assembly, InsightsLayerService.TriggerScriptResource, cancellationToken)
                    .ConfigureAwait(false);
                await InsightsLayerService.ExecuteBatchesAsync(conn, triggerScript, cancellationToken).ConfigureAwait(false);
            }
            catch (SqlException ex) when (ex.Number == 15151)
            {
                // The permission check passed but CREATE ... WITH EXECUTE AS 'dbo' was still refused.
                throw new InvalidOperationException(before.TableExists ? ImpersonateDboError : PartialInstallError(ImpersonateDboNeed + "."), ex);
            }
            catch (Exception ex) when (!before.TableExists && ex is not OperationCanceledException)
            {
                throw new InvalidOperationException(PartialInstallError(ex.Message), ex);
            }
        }

        var after = await ReadStateAsync(conn, cancellationToken).ConfigureAwait(false);
        if (!after.TableExists || !after.TriggerExists)
        {
            throw new InvalidOperationException(
                $"DDL history install did not complete (table {(after.TableExists ? "present" : "missing")}, trigger {(after.TriggerExists ? "present" : "missing")}). "
                + "Creating the DDL_Audit trigger needs db_owner (or ALTER ANY DATABASE DDL TRIGGER plus IMPERSONATE on dbo).");
        }

        return new DdlAuditInstallResult(
            CreatedTable: !before.TableExists,
            CreatedTrigger: !before.TriggerExists,
            TriggerEnabled: after.TriggerEnabled,
            Warning: before.TriggerExists && !after.TriggerEnabled ? DisabledTriggerWarning : null);
    }

    /// <summary>One <c>sys.columns</c> row of an existing <c>dbo.DDL_AuditLog</c>.</summary>
    /// <param name="TypeName">System type name (alias types resolve to their base type).</param>
    /// <param name="MaxLength"><c>max_length</c> in bytes; -1 for (max) and xml.</param>
    internal sealed record AuditColumn(string Name, string TypeName, short MaxLength, bool IsNullable, bool IsIdentity, bool IsComputed, bool HasDefault);

    /// <summary>The varchar/nvarchar name columns and the characters the trigger writes into each (LEFT(..., n)).</summary>
    private static readonly (string Name, int Chars)[] TextColumns =
    [
        ("HostName", 100), ("LoginName", 100), ("SchemaName", 100), ("ObjectName", 100), ("ObjectType", 100), ("EventType", 64), ("ProgramName", 100),
    ];

    private static readonly string[] IntegerTypes = ["tinyint", "smallint", "int", "bigint"];
    private static readonly string[] DateTimeTypes = ["datetime", "datetime2", "smalldatetime", "datetimeoffset", "date"];

    internal const string VarcharCommandTextWarning = "CommandText is varchar(max); non-Latin text in DDL will be stored lossy.";

    /// <summary>
    /// Why the trigger's INSERT could fail on an existing table, one entry per problem, in this order: the trigger
    /// columns (missing, computed, identity, too narrow or the wrong type), then ID (an integer type) and PostTime (a
    /// date/time type), then every non-trigger column the INSERT leaves out that still needs a value (NOT NULL with no
    /// default, not identity, not computed, not rowversion). Names compare case-insensitively.
    /// </summary>
    /// <param name="warnings">Compatible but lossy: a varchar(max) CommandText.</param>
    internal static IReadOnlyList<string> DescribeColumnProblems(IReadOnlyList<AuditColumn> columns, out IReadOnlyList<string> warnings)
    {
        AuditColumn? Find(string name) => columns.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
        var problems = new List<string>();
        var lossy = new List<string>();

        foreach (var name in TriggerColumns)
        {
            var c = Find(name);
            var problem = c is null ? $"{name} missing"
                : c.IsComputed ? $"{name} is a computed column"
                : c.IsIdentity ? $"{name} is an identity column"
                : TypeProblem(c);
            if (problem is not null)
            {
                problems.Add(problem);
            }
            else if (c is not null && name == "CommandText" && Is(c, "varchar"))
            {
                lossy.Add(VarcharCommandTextWarning);
            }
        }

        var id = Find("ID");
        if (id is null)
        {
            problems.Add("ID missing");
        }
        else if (!IntegerTypes.Contains(id.TypeName, StringComparer.OrdinalIgnoreCase))
        {
            problems.Add($"ID {DisplayType(id)}, needs an integer type");
        }

        var postTime = Find("PostTime");
        if (postTime is null)
        {
            problems.Add("PostTime missing");
        }
        else if (!DateTimeTypes.Contains(postTime.TypeName, StringComparer.OrdinalIgnoreCase))
        {
            problems.Add($"PostTime {DisplayType(postTime)}, needs a date/time type");
        }

        problems.AddRange(columns
            .Where(c => !TriggerColumns.Contains(c.Name, StringComparer.OrdinalIgnoreCase))
            .Where(c => !c.IsNullable && !c.IsIdentity && !c.IsComputed && !c.HasDefault && !Is(c, "timestamp"))
            .Select(c => $"{c.Name} NOT NULL without a default or identity"));

        warnings = lossy;
        return problems;
    }

    private static string? TypeProblem(AuditColumn c)
    {
        var text = TextColumns.FirstOrDefault(t => string.Equals(t.Name, c.Name, StringComparison.OrdinalIgnoreCase));
        if (text.Name is not null)
        {
            if (!Is(c, "varchar") && !Is(c, "nvarchar"))
            {
                return $"{c.Name} {DisplayType(c)}, needs varchar or nvarchar of at least {text.Chars} characters";
            }

            return c.MaxLength == -1 || Chars(c) >= text.Chars ? null : $"{c.Name} {DisplayType(c)}, needs at least {text.Chars} characters";
        }

        var isMaxText = (Is(c, "varchar") || Is(c, "nvarchar")) && c.MaxLength == -1;
        if (string.Equals(c.Name, "CommandText", StringComparison.OrdinalIgnoreCase))
        {
            return isMaxText ? null : $"{c.Name} {DisplayType(c)}, needs nvarchar(max) or varchar(max)";
        }

        // CommandXML: SQL Server has no implicit xml-to-character conversion (error 257), and the trigger's INSERT is
        // resolved against an existing table at CREATE TRIGGER time, so n/varchar(max) cannot even get the trigger.
        return Is(c, "xml") ? null : $"{c.Name} {DisplayType(c)}, needs xml";
    }

    private static bool Is(AuditColumn c, string type) => string.Equals(c.TypeName, type, StringComparison.OrdinalIgnoreCase);

    private static bool IsUnicode(AuditColumn c) => Is(c, "nvarchar") || Is(c, "nchar");

    private static int Chars(AuditColumn c) => IsUnicode(c) ? c.MaxLength / 2 : c.MaxLength;

    /// <summary>The column type as T-SQL spells it: <c>varchar(10)</c>, <c>nvarchar(max)</c>, <c>int</c>.</summary>
    private static string DisplayType(AuditColumn c)
    {
        var type = c.TypeName.ToLowerInvariant();
        return type is "varchar" or "nvarchar" or "char" or "nchar" or "varbinary" or "binary"
            ? $"{type}({(c.MaxLength == -1 ? "max" : Chars(c).ToString(CultureInfo.InvariantCulture))})"
            : type;
    }

    internal static string IncompatibleTableError(IEnumerable<string> incompatibilities) =>
        $"dbo.DDL_AuditLog exists but the DDL_Audit trigger cannot write to it: {string.Join("; ", incompatibilities)}. Nothing was created.";

    private const string ImpersonateDboNeed = "Creating DDL_Audit WITH EXECUTE AS 'dbo' needs db_owner (or IMPERSONATE on dbo) on this database";

    internal const string ImpersonateDboError = ImpersonateDboNeed + "; nothing was created.";

    internal static string PartialInstallError(string message) =>
        $"dbo.DDL_AuditLog was created, but creating the DDL_Audit trigger failed: {message}";

    /// <summary>The newest <paramref name="top"/> entries for one object; null when the table is missing.</summary>
    public static async Task<IReadOnlyList<DdlAuditEntry>?> ListAsync(
        SqlConnection conn, string? schema, string name, int top, CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(conn, cancellationToken).ConfigureAwait(false))
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

    /// <summary>One entry with its command text. <c>TableExists</c> is false when the table is missing.</summary>
    public static async Task<(bool TableExists, DdlAuditCommand? Command)> GetAsync(SqlConnection conn, int id, CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(conn, cancellationToken).ConfigureAwait(false))
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

    private static async Task<bool> TableExistsAsync(SqlConnection conn, CancellationToken cancellationToken)
    {
        await using var cmd = new SqlCommand("SELECT CASE WHEN OBJECT_ID(N'dbo.DDL_AuditLog', N'U') IS NULL THEN 0 ELSE 1 END;", conn);
        return await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is 1;
    }

    private static async Task<State> ReadStateAsync(SqlConnection conn, CancellationToken cancellationToken)
    {
        bool tableExists, triggerExists, triggerEnabled;
        string? serverName, databaseName;
        await using (var cmd = new SqlCommand(StateSql, conn))
        {
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            _ = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            tableExists = reader.GetInt32(0) == 1;
            triggerExists = !reader.IsDBNull(1);
            triggerEnabled = triggerExists && reader.GetInt32(1) == 0;
            serverName = reader.IsDBNull(2) ? null : reader.GetString(2);
            databaseName = reader.IsDBNull(3) ? null : reader.GetString(3);
        }

        IReadOnlyList<string> incompatibilities = [], warnings = [];
        if (tableExists)
        {
            incompatibilities = DescribeColumnProblems(await ReadColumnsAsync(conn, cancellationToken).ConfigureAwait(false), out warnings);
        }

        return new State(tableExists, triggerExists, triggerEnabled, incompatibilities, warnings, serverName, databaseName);
    }

    private static async Task<IReadOnlyList<AuditColumn>> ReadColumnsAsync(SqlConnection conn, CancellationToken cancellationToken)
    {
        var columns = new List<AuditColumn>();
        await using var cmd = new SqlCommand(ColumnsSql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            columns.Add(new AuditColumn(
                Name: reader.GetString(0),
                TypeName: reader.GetString(1),
                MaxLength: reader.GetInt16(2),
                IsNullable: reader.GetBoolean(3),
                IsIdentity: reader.GetBoolean(4),
                IsComputed: reader.GetBoolean(5),
                HasDefault: reader.GetBoolean(6)));
        }

        return columns;
    }

    private static async Task<bool> CanImpersonateDboAsync(SqlConnection conn, CancellationToken cancellationToken)
    {
        await using var cmd = new SqlCommand(CanImpersonateDboSql, conn);
        return await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is 1;
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
