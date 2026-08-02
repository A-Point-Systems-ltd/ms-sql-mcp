using System.Data;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Mssql.McpServer.InsightsLayer.Models;

namespace Mssql.McpServer.InsightsLayer;

/// <summary>
/// Implements install, read, upsert, DDL watermark processing, and fingerprint-based invalidation.
/// </summary>
public sealed class InsightsLayerService(
    ISqlConnectionFactory connectionFactory,
    ILogger<InsightsLayerService> logger) : IInsightsLayerService
{
    private const string SchemaScriptResource = "Mssql.McpServer.InsightsLayer.SqlScripts.CreateInsightsSchema.sql";
    private const string TriggerScriptResource = "Mssql.McpServer.InsightsLayer.SqlScripts.CreateDdlAuditTrigger.sql";
    internal const string AutoMechanicalModel = "auto-mechanical";

    private readonly ISqlConnectionFactory _connectionFactory = connectionFactory;
    private readonly ILogger<InsightsLayerService> _logger = logger;

    private enum LiveObjectState
    {
        Found,
        Missing,
        AccessDenied,
        DefinitionUnavailable
    }

    private sealed record LiveFingerprintResult(
        LiveObjectState State,
        DateTime? ModifyDate,
        int? ObjectId,
        string? Fingerprint);
    private sealed record ObjectIdentity(string ObjectType, string SchemaName, string ObjectName);

    public bool IsEnabled => InsightsLayerEnvironment.IsInsightsLayerEnabled;

    public async Task<LayerStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        if (!IsEnabled)
        {
            return new LayerStatus
            {
                LayerEnabledViaEnvironment = false,
                AiInsightsSchemaExists = false,
                DdlAuditTableExists = false,
                DdlAuditTriggerEnabled = false,
                SchemaInsightsCount = 0,
                LastProcessedAuditId = 0,
                LastProcessedAt = null
            };
        }

        await using var conn = await _connectionFactory.GetOpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT
                CASE WHEN EXISTS (SELECT 1 FROM sys.schemas WHERE name = N'AIInsights') THEN 1 ELSE 0 END,
                CASE WHEN OBJECT_ID(N'dbo.DDL_AuditLog', N'U') IS NOT NULL THEN 1 ELSE 0 END,
                CASE WHEN EXISTS (SELECT 1 FROM sys.triggers WHERE name = N'DDL_Audit' AND parent_class = 0 AND is_disabled = 0) THEN 1 ELSE 0 END,
                (SELECT COUNT(*) FROM AIInsights.SchemaInsights),
                ISNULL((SELECT LastProcessedAuditID FROM AIInsights.DdlChangeWatermark WHERE SingletonId = 1), 0),
                (SELECT LastProcessedAt FROM AIInsights.DdlChangeWatermark WHERE SingletonId = 1);
            """;
        try
        {
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return new LayerStatus
                {
                    LayerEnabledViaEnvironment = true,
                    AiInsightsSchemaExists = false,
                    DdlAuditTableExists = false,
                    DdlAuditTriggerEnabled = false,
                    SchemaInsightsCount = 0,
                    LastProcessedAuditId = 0,
                    LastProcessedAt = null
                };
            }

            return new LayerStatus
            {
                LayerEnabledViaEnvironment = true,
                AiInsightsSchemaExists = reader.GetInt32(0) == 1,
                DdlAuditTableExists = reader.GetInt32(1) == 1,
                DdlAuditTriggerEnabled = reader.GetInt32(2) == 1,
                SchemaInsightsCount = reader.GetInt32(3),
                LastProcessedAuditId = reader.GetInt32(4),
                LastProcessedAt = reader.IsDBNull(5) ? null : reader.GetDateTime(5)
            };
        }
        catch (SqlException ex)
        {
            _logger.LogDebug(ex, "Insights status query failed (layer may not be installed).");
            return new LayerStatus
            {
                LayerEnabledViaEnvironment = true,
                AiInsightsSchemaExists = false,
                DdlAuditTableExists = false,
                DdlAuditTriggerEnabled = false,
                SchemaInsightsCount = 0,
                LastProcessedAuditId = 0,
                LastProcessedAt = null
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GetStatusAsync failed.");
            return new LayerStatus
            {
                LayerEnabledViaEnvironment = true,
                AiInsightsSchemaExists = false,
                DdlAuditTableExists = false,
                DdlAuditTriggerEnabled = false,
                SchemaInsightsCount = 0,
                LastProcessedAuditId = 0,
                LastProcessedAt = null
            };
        }
    }

    public async Task<DbOperationResult> InstallLayerAsync(CancellationToken cancellationToken = default)
    {
        if (!IsEnabled)
        {
            return await NoOpInsightsLayerService.Instance.InstallLayerAsync(cancellationToken);
        }

        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            var schemaSql = await ReadEmbeddedResourceAsync(assembly, SchemaScriptResource, cancellationToken).ConfigureAwait(false);
            await ExecuteScriptBatchesAsync(schemaSql, cancellationToken).ConfigureAwait(false);

            await using (var conn = await _connectionFactory.GetOpenConnectionAsync())
            {
                if (await DdlAuditTableExistsAsync(conn, cancellationToken).ConfigureAwait(false)
                    && !await DdlAuditTriggerExistsAsync(conn, cancellationToken).ConfigureAwait(false))
                {
                    var triggerSql = await ReadEmbeddedResourceAsync(assembly, TriggerScriptResource, cancellationToken).ConfigureAwait(false);
                    await ExecuteScriptBatchesAsync(triggerSql, cancellationToken).ConfigureAwait(false);
                }
            }

            return new DbOperationResult(success: true, data: new { installed = true, message = "AIInsights schema, tables, DDL_AuditLog, and DDL_Audit trigger applied (idempotent)." });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "InstallLayerAsync failed.");
            var msg = ex.Message;
            if (msg.Contains("DDL", StringComparison.OrdinalIgnoreCase) || msg.Contains("permission", StringComparison.OrdinalIgnoreCase))
            {
                msg += " Hint: installing the database DDL trigger requires ALTER ANY DATABASE DDL TRIGGER (or membership in ddl_admin / sysadmin).";
            }

            return new DbOperationResult(success: false, error: msg);
        }
    }

    public async Task<(SchemaInsight? insight, InsightFreshness freshness)> GetInsightForObjectAsync(
        string objectType,
        string? schemaName,
        string objectName,
        CancellationToken cancellationToken = default)
    {
        if (!IsEnabled)
        {
            return (null, InsightFreshness.LayerDisabled);
        }

        var schema = NormalizeSchema(schemaName);
        await using var conn = await _connectionFactory.GetOpenConnectionAsync();
        var insight = await TryLoadInsightRowAsync(conn, objectType, schema, objectName, cancellationToken).ConfigureAwait(false);
        if (insight is null)
        {
            return (null, InsightFreshness.Absent);
        }

        var live = await TryComputeLiveFingerprintAsync(conn, objectType, schema, objectName, cancellationToken).ConfigureAwait(false);
        if (live.State == LiveObjectState.Missing)
        {
            await ArchiveInsightAsync(conn, insight.InsightId, "ObjectMissing", "GetInsight", null, cancellationToken).ConfigureAwait(false);
            return (null, InsightFreshness.StaleArchived);
        }

        if (live.State == LiveObjectState.AccessDenied)
        {
            return (insight, InsightFreshness.AccessDenied);
        }

        if (live.State == LiveObjectState.DefinitionUnavailable)
        {
            return (insight, InsightFreshness.DefinitionUnavailable);
        }

        if (IsStaleAgainstLive(
                insight.ObjectIdAtAnalysis,
                insight.ModifyDateAtAnalysis,
                insight.SchemaFingerprint,
                live.ObjectId,
                live.ModifyDate,
                live.Fingerprint))
        {
            await ArchiveInsightAsync(conn, insight.InsightId, "FingerprintMismatch", "GetInsight", null, cancellationToken).ConfigureAwait(false);
            return (null, InsightFreshness.StaleArchived);
        }

        return (insight, InsightFreshness.Fresh);
    }

    /// <summary>
    /// sys.objects.modify_date is a datetime (1/300s granularity), so a value that has been through
    /// the DATETIME2 storage column can legitimately differ from the live read by a few milliseconds.
    /// </summary>
    private static readonly TimeSpan ModifyDateTolerance = TimeSpan.FromMilliseconds(10);

    /// <summary>
    /// Decides whether a stored insight no longer matches the live object. Shared by the read path
    /// and the background fingerprint scan so both invalidate on exactly the same signals.
    /// </summary>
    internal static bool IsStaleAgainstLive(
        int? storedObjectId,
        DateTime? storedModifyDate,
        string? storedFingerprint,
        int? liveObjectId,
        DateTime? liveModifyDate,
        string? liveFingerprint)
    {
        if (storedObjectId.HasValue && liveObjectId.HasValue && storedObjectId.Value != liveObjectId.Value)
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(storedFingerprint) && !string.IsNullOrWhiteSpace(liveFingerprint))
        {
            // The fingerprint covers the columns/definition that an insight actually describes, so it
            // decides alone: modify_date also moves for changes that leave the shape intact, such as
            // auto-created statistics or an index rebuild.
            return !string.Equals(storedFingerprint, liveFingerprint, StringComparison.OrdinalIgnoreCase);
        }

        return storedModifyDate.HasValue
            && liveModifyDate.HasValue
            && (storedModifyDate.Value - liveModifyDate.Value).Duration() > ModifyDateTolerance;
    }

    public async Task<(SchemaInsight? insight, InsightFreshness freshness)> EnsureBaselineForObjectAsync(
        string objectType,
        string? schemaName,
        string objectName,
        CancellationToken cancellationToken = default)
    {
        if (!IsEnabled || !InsightsLayerEnvironment.IsAutoPopulationEnabled)
        {
            return await GetInsightForObjectAsync(objectType, schemaName, objectName, cancellationToken).ConfigureAwait(false);
        }

        var schema = NormalizeSchema(schemaName);
        var (existing, existingFreshness) = await GetInsightForObjectAsync(objectType, schema, objectName, cancellationToken).ConfigureAwait(false);
        if (existingFreshness is InsightFreshness.LayerDisabled or InsightFreshness.AccessDenied or InsightFreshness.DefinitionUnavailable)
        {
            return (existing, existingFreshness);
        }

        var shouldRefreshAutoMechanical = existing is not null
            && existingFreshness == InsightFreshness.Fresh
            && IsAutoMechanical(existing)
            && InsightsLayerEnvironment.IsAutoPopulationRefreshEnabled
            && existing.LastAnalyzed.Date < DateTime.Now.Date;

        if (existing is not null && !shouldRefreshAutoMechanical)
        {
            return (existing, existingFreshness);
        }

        try
        {
            await using var conn = await _connectionFactory.GetOpenConnectionAsync();
            var live = await TryComputeLiveFingerprintAsync(conn, objectType, schema, objectName, cancellationToken).ConfigureAwait(false);
            if (live.State != LiveObjectState.Found)
            {
                return await GetInsightForObjectAsync(objectType, schema, objectName, cancellationToken).ConfigureAwait(false);
            }

            var baseline = await BuildMechanicalBaselineAsync(conn, objectType, schema, objectName, live, cancellationToken).ConfigureAwait(false);
            var upsert = await UpsertInsightAsync(baseline, cancellationToken).ConfigureAwait(false);
            if (!upsert.Success)
            {
                _logger.LogDebug("Auto baseline upsert skipped for {Type} {Schema}.{Object}: {Error}", objectType, schema, objectName, upsert.Error);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "EnsureBaselineForObjectAsync failed for {Type} {Schema}.{Object}", objectType, schema, objectName);
        }

        return await GetInsightForObjectAsync(objectType, schema, objectName, cancellationToken).ConfigureAwait(false);
    }

    public async Task<DbOperationResult> UpsertInsightAsync(SchemaInsight input, CancellationToken cancellationToken = default)
    {
        if (!IsEnabled)
        {
            return await NoOpInsightsLayerService.Instance.UpsertInsightAsync(input, cancellationToken);
        }

        var schema = NormalizeSchema(input.SchemaName);
        var columnName = string.IsNullOrWhiteSpace(input.ColumnName) ? (string?)null : input.ColumnName;

        try
        {
            await using var conn = await _connectionFactory.GetOpenConnectionAsync();
            var live = await TryComputeLiveFingerprintAsync(conn, input.ObjectType, schema, input.ObjectName, cancellationToken).ConfigureAwait(false);
            DateTime? modifyDate = live.State == LiveObjectState.Found ? live.ModifyDate : null;
            string? fingerprint = live.State == LiveObjectState.Found ? live.Fingerprint : null;
            int? objectId = live.State == LiveObjectState.Found ? live.ObjectId : null;

            await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await using (var updateCmd = new SqlCommand(
                    """
                    UPDATE AIInsights.SchemaInsights
                    SET Description = @Description,
                        BusinessPurpose = @BusinessPurpose,
                        DataPatterns = @DataPatterns,
                        UsageGuidelines = @UsageGuidelines,
                        RelatedObjects = @RelatedObjects,
                        LLMModel = @LLMModel,
                        Confidence = @Confidence,
                        LastAnalyzed = GETDATE(),
                        AnalyzedBy = @AnalyzedBy,
                        Version = Version + 1,
                        ModifyDateAtAnalysis = @ModifyDateAtAnalysis,
                        ObjectIdAtAnalysis = @ObjectIdAtAnalysis,
                        SchemaFingerprint = @SchemaFingerprint
                    WHERE ObjectType = @ObjectType
                      AND SchemaName = @SchemaName
                      AND ObjectName = @ObjectName
                      AND ((@ColumnName IS NULL AND ColumnName IS NULL) OR (ColumnName = @ColumnName));
                    """,
                    conn,
                    tx))
                {
                    AddUpsertParameters(updateCmd, input, schema, columnName, modifyDate, objectId, fingerprint);
                    var updated = await updateCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                    if (updated == 0)
                    {
                        await using var insertCmd = new SqlCommand(
                            """
                            INSERT INTO AIInsights.SchemaInsights (
                                ObjectType, SchemaName, ObjectName, ColumnName,
                                Description, BusinessPurpose, DataPatterns, UsageGuidelines, RelatedObjects,
                                LLMModel, Confidence, AnalyzedBy, ModifyDateAtAnalysis, ObjectIdAtAnalysis, SchemaFingerprint)
                            VALUES (
                                @ObjectType, @SchemaName, @ObjectName, @ColumnName,
                                @Description, @BusinessPurpose, @DataPatterns, @UsageGuidelines, @RelatedObjects,
                                @LLMModel, @Confidence, @AnalyzedBy, @ModifyDateAtAnalysis, @ObjectIdAtAnalysis, @SchemaFingerprint);
                            """,
                            conn,
                            tx);
                        AddUpsertParameters(insertCmd, input, schema, columnName, modifyDate, objectId, fingerprint);
                        _ = await insertCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                    }
                }

                await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new DbOperationResult(success: true, data: new { schema, input.ObjectName, input.ObjectType });
            }
            catch
            {
                await tx.RollbackAsync(cancellationToken).ConfigureAwait(false);
                throw;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "UpsertInsightAsync failed.");
            return new DbOperationResult(success: false, error: ex.Message);
        }
    }

    public async Task ProcessDdlChangesAsync(CancellationToken cancellationToken = default)
    {
        if (!IsEnabled)
        {
            return;
        }

        try
        {
            await using var conn = await _connectionFactory.GetOpenConnectionAsync();
            if (!await AiInsightsInstalledAsync(conn, cancellationToken).ConfigureAwait(false))
            {
                return;
            }

            var archivedObjects = new List<ObjectIdentity>();

            if (await DdlAuditTableExistsAsync(conn, cancellationToken).ConfigureAwait(false)
                && await DdlWatermarkTableExistsAsync(conn, cancellationToken).ConfigureAwait(false))
            {
                var lastId = await ReadWatermarkAsync(conn, cancellationToken).ConfigureAwait(false);
                var newRows = await ReadDdlAuditRowsAsync(conn, lastId, cancellationToken).ConfigureAwait(false);
                if (newRows.Count > 0)
                {
                    var maxId = lastId;
                    foreach (var row in newRows)
                    {
                        maxId = Math.Max(maxId, row.Id);
                        archivedObjects.AddRange(await ArchiveInsightsForDdlObjectAsync(conn, row, cancellationToken).ConfigureAwait(false));
                    }

                    await UpdateWatermarkAsync(conn, maxId, cancellationToken).ConfigureAwait(false);
                }
            }

            archivedObjects.AddRange(await ScanFingerprintsAndArchiveAsync(conn, take: 500, cancellationToken).ConfigureAwait(false));

            if (InsightsLayerEnvironment.IsAutoPopulationEnabled)
            {
                foreach (var obj in archivedObjects.DistinctBy(o => $"{o.ObjectType}|{o.SchemaName}|{o.ObjectName}"))
                {
                    _ = await EnsureBaselineForObjectAsync(obj.ObjectType, obj.SchemaName, obj.ObjectName, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ProcessDdlChangesAsync failed (non-fatal).");
        }
    }

    public async Task<DbOperationResult> ListInsightsAsync(string? schemaName, string? objectType, int take, CancellationToken cancellationToken = default)
    {
        if (!IsEnabled)
        {
            return await NoOpInsightsLayerService.Instance.ListInsightsAsync(schemaName, objectType, take, cancellationToken);
        }

        take = Math.Clamp(take, 1, 2000);
        try
        {
            await using var conn = await _connectionFactory.GetOpenConnectionAsync();
            await using var cmd = new SqlCommand(
                """
                SELECT TOP (@Take)
                    InsightID, ObjectType, SchemaName, ObjectName, ColumnName,
                    Description, BusinessPurpose, Confidence, LastAnalyzed, Version
                FROM AIInsights.SchemaInsights
                WHERE (@SchemaName IS NULL OR SchemaName = @SchemaName)
                  AND (@ObjectType IS NULL OR ObjectType = @ObjectType)
                ORDER BY LastAnalyzed DESC;
                """,
                conn);
            cmd.Parameters.AddWithValue("@Take", take);
            cmd.Parameters.AddWithValue("@SchemaName", string.IsNullOrWhiteSpace(schemaName) ? DBNull.Value : schemaName);
            cmd.Parameters.AddWithValue("@ObjectType", string.IsNullOrWhiteSpace(objectType) ? DBNull.Value : objectType);

            var list = new List<Dictionary<string, object?>>();
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                list.Add(ReadInsightSummary(reader));
            }

            return new DbOperationResult(success: true, data: list);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ListInsightsAsync failed.");
            return new DbOperationResult(success: false, error: ex.Message);
        }
    }

    public async Task<DbOperationResult> GetHistoryAsync(string? schemaName, string? objectName, int take, CancellationToken cancellationToken = default)
    {
        if (!IsEnabled)
        {
            return await NoOpInsightsLayerService.Instance.GetHistoryAsync(schemaName, objectName, take, cancellationToken);
        }

        take = Math.Clamp(take, 1, 2000);
        try
        {
            await using var conn = await _connectionFactory.GetOpenConnectionAsync();
            await using var cmd = new SqlCommand(
                """
                SELECT TOP (@Take)
                    HistoryID, OriginalInsightID, ObjectType, SchemaName, ObjectName, ColumnName,
                    Description, ArchiveReason, ArchivedByEvent, SourceDdlAuditID, ArchivedAt
                FROM AIInsights.InsightHistory
                WHERE (@SchemaName IS NULL OR SchemaName = @SchemaName)
                  AND (@ObjectName IS NULL OR ObjectName = @ObjectName)
                ORDER BY ArchivedAt DESC;
                """,
                conn);
            cmd.Parameters.AddWithValue("@Take", take);
            cmd.Parameters.AddWithValue("@SchemaName", string.IsNullOrWhiteSpace(schemaName) ? DBNull.Value : schemaName);
            cmd.Parameters.AddWithValue("@ObjectName", string.IsNullOrWhiteSpace(objectName) ? DBNull.Value : objectName);

            var list = new List<Dictionary<string, object?>>();
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                list.Add(new Dictionary<string, object?>
                {
                    ["historyId"] = reader.GetInt32(0),
                    ["originalInsightId"] = reader.IsDBNull(1) ? null : reader.GetInt32(1),
                    ["objectType"] = reader.GetString(2),
                    ["schemaName"] = reader.IsDBNull(3) ? null : reader.GetString(3),
                    ["objectName"] = reader.GetString(4),
                    ["columnName"] = reader.IsDBNull(5) ? null : reader.GetString(5),
                    ["description"] = reader.IsDBNull(6) ? null : reader.GetString(6),
                    ["archiveReason"] = reader.IsDBNull(7) ? null : reader.GetString(7),
                    ["archivedByEvent"] = reader.IsDBNull(8) ? null : reader.GetString(8),
                    ["sourceDdlAuditId"] = reader.IsDBNull(9) ? null : reader.GetInt32(9),
                    ["archivedAt"] = reader.GetDateTime(10)
                });
            }

            return new DbOperationResult(success: true, data: list);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetHistoryAsync failed.");
            return new DbOperationResult(success: false, error: ex.Message);
        }
    }

    public async Task<DbOperationResult> RefreshInsightsAsync(CancellationToken cancellationToken = default)
    {
        if (!IsEnabled)
        {
            return await NoOpInsightsLayerService.Instance.RefreshInsightsAsync(cancellationToken);
        }

        try
        {
            await ProcessDdlChangesAsync(cancellationToken).ConfigureAwait(false);
            await using var conn = await _connectionFactory.GetOpenConnectionAsync();
            var recent = await QueryRecentInsightsAsync(conn, cancellationToken).ConfigureAwait(false);
            var topPatterns = new List<Dictionary<string, object?>>();
            return new DbOperationResult(success: true, data: new { recentInsights = recent, topQueryPatterns = topPatterns });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RefreshInsightsAsync failed.");
            return new DbOperationResult(success: false, error: ex.Message);
        }
    }

    private static void AddUpsertParameters(SqlCommand cmd, SchemaInsight input, string schema, string? columnName, DateTime? modifyDate, int? objectId, string? fingerprint)
    {
        cmd.Parameters.AddWithValue("@ObjectType", input.ObjectType);
        cmd.Parameters.AddWithValue("@SchemaName", schema);
        cmd.Parameters.AddWithValue("@ObjectName", input.ObjectName);
        cmd.Parameters.AddWithValue("@ColumnName", columnName is null ? DBNull.Value : columnName);
        cmd.Parameters.AddWithValue("@Description", input.Description ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@BusinessPurpose", input.BusinessPurpose ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@DataPatterns", input.DataPatterns ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@UsageGuidelines", input.UsageGuidelines ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@RelatedObjects", input.RelatedObjects ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@LLMModel", input.LlmModel ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@Confidence", input.Confidence ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@AnalyzedBy", input.AnalyzedBy ?? (object)DBNull.Value);
        // Explicit DATETIME2: an inferred datetime parameter is re-rounded to 1/300s on the way into
        // the DATETIME2 column, which made the stored value differ from the live read.
        cmd.Parameters.Add("@ModifyDateAtAnalysis", SqlDbType.DateTime2).Value = modifyDate ?? (object)DBNull.Value;
        cmd.Parameters.AddWithValue("@ObjectIdAtAnalysis", objectId ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@SchemaFingerprint", fingerprint ?? (object)DBNull.Value);
    }

    private static Dictionary<string, object?> ReadInsightSummary(SqlDataReader reader) =>
        new()
        {
            ["insightId"] = reader.GetInt32(0),
            ["objectType"] = reader.GetString(1),
            ["schemaName"] = reader.IsDBNull(2) ? null : reader.GetString(2),
            ["objectName"] = reader.GetString(3),
            ["columnName"] = reader.IsDBNull(4) ? null : reader.GetString(4),
            ["description"] = reader.IsDBNull(5) ? null : reader.GetString(5),
            ["businessPurpose"] = reader.IsDBNull(6) ? null : reader.GetString(6),
            ["confidence"] = reader.IsDBNull(7) ? null : reader.GetDecimal(7),
            ["lastAnalyzed"] = reader.GetDateTime(8),
            ["version"] = reader.GetInt32(9)
        };

    private static bool IsAutoMechanical(SchemaInsight? insight) =>
        insight is not null
        && string.Equals(insight.LlmModel, AutoMechanicalModel, StringComparison.OrdinalIgnoreCase);

    private async Task<SchemaInsight> BuildMechanicalBaselineAsync(
        SqlConnection conn,
        string objectType,
        string schema,
        string objectName,
        LiveFingerprintResult live,
        CancellationToken cancellationToken)
    {
        var normalizedType = objectType.Trim();
        int? columnCount = null;
        long? approxRowCount = null;

        var relatedObjects = await ReadRelatedObjectsAsync(conn, normalizedType, schema, objectName, cancellationToken).ConfigureAwait(false);
        if (string.Equals(normalizedType, "Table", StringComparison.OrdinalIgnoreCase))
        {
            columnCount = await ReadTableColumnCountAsync(conn, schema, objectName, cancellationToken).ConfigureAwait(false);
            if (InsightsLayerEnvironment.IsAutoPopulationRowCountsEnabled)
            {
                approxRowCount = await ReadTableApproxRowCountAsync(conn, schema, objectName, cancellationToken).ConfigureAwait(false);
            }
        }

        var dataPatterns = JsonSerializer.Serialize(new
        {
            mode = AutoMechanicalModel,
            generatedAtUtc = DateTime.UtcNow,
            objectType = normalizedType,
            schemaName = schema,
            objectName,
            objectId = live.ObjectId,
            modifyDateAtAnalysis = live.ModifyDate,
            columnCount,
            approxRowCount
        });

        var shortName = $"{schema}.{objectName}";
        var description = string.Equals(normalizedType, "Table", StringComparison.OrdinalIgnoreCase)
            ? $"Auto-baseline for {shortName}: columns={columnCount?.ToString() ?? "?"}, approxRows={(approxRowCount?.ToString() ?? "n/a")}."
            : $"Auto-baseline for {shortName} ({normalizedType}) from live schema metadata.";

        return new SchemaInsight
        {
            InsightId = 0,
            ObjectType = normalizedType,
            SchemaName = schema,
            ObjectName = objectName,
            ColumnName = null,
            Description = description,
            BusinessPurpose = "Auto-generated baseline from schema metadata. Replace with domain-specific business purpose.",
            DataPatterns = dataPatterns,
            UsageGuidelines = "Auto-generated baseline. Enrich with preferred joins, filters, and known caveats after inspection.",
            RelatedObjects = JsonSerializer.Serialize(relatedObjects),
            LlmModel = AutoMechanicalModel,
            Confidence = 0.30m,
            LastAnalyzed = default,
            AnalyzedBy = "MssqlMcp-AutoBaseline",
            Version = 1,
            ModifyDateAtAnalysis = live.ModifyDate,
            ObjectIdAtAnalysis = live.ObjectId,
            SchemaFingerprint = live.Fingerprint
        };
    }

    private static async Task<int?> ReadTableColumnCountAsync(SqlConnection conn, string schema, string tableName, CancellationToken cancellationToken)
    {
        await using var cmd = new SqlCommand(
            """
            SELECT COUNT(*)
            FROM sys.columns c
            INNER JOIN sys.tables t ON c.object_id = t.object_id
            INNER JOIN sys.schemas s ON t.schema_id = s.schema_id
            WHERE s.name = @Schema AND t.name = @TableName;
            """,
            conn);
        cmd.Parameters.AddWithValue("@Schema", schema);
        cmd.Parameters.AddWithValue("@TableName", tableName);
        var scalar = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return scalar is int i ? i : null;
    }

    private static async Task<long?> ReadTableApproxRowCountAsync(SqlConnection conn, string schema, string tableName, CancellationToken cancellationToken)
    {
        await using var cmd = new SqlCommand(
            """
            SELECT SUM(CAST(ps.row_count AS BIGINT))
            FROM sys.dm_db_partition_stats ps
            INNER JOIN sys.tables t ON ps.object_id = t.object_id
            INNER JOIN sys.schemas s ON t.schema_id = s.schema_id
            WHERE s.name = @Schema
              AND t.name = @TableName
              AND ps.index_id IN (0, 1);
            """,
            conn);
        cmd.Parameters.AddWithValue("@Schema", schema);
        cmd.Parameters.AddWithValue("@TableName", tableName);
        var scalar = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (scalar is DBNull || scalar is null)
        {
            return null;
        }

        return Convert.ToInt64(scalar, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<List<string>> ReadRelatedObjectsAsync(
        SqlConnection conn,
        string objectType,
        string schema,
        string objectName,
        CancellationToken cancellationToken)
    {
        var related = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.Equals(objectType, "Table", StringComparison.OrdinalIgnoreCase))
        {
            await using var fkCmd = new SqlCommand(
                """
                SELECT DISTINCT QUOTENAME(SCHEMA_NAME(t2.schema_id)) + N'.' + QUOTENAME(t2.name) AS related_name
                FROM sys.foreign_keys fk
                INNER JOIN sys.tables t1 ON fk.parent_object_id = t1.object_id
                INNER JOIN sys.schemas s1 ON t1.schema_id = s1.schema_id
                INNER JOIN sys.tables t2 ON fk.referenced_object_id = t2.object_id
                WHERE s1.name = @Schema AND t1.name = @Name
                UNION
                SELECT DISTINCT QUOTENAME(SCHEMA_NAME(t1.schema_id)) + N'.' + QUOTENAME(t1.name) AS related_name
                FROM sys.foreign_keys fk
                INNER JOIN sys.tables t2 ON fk.referenced_object_id = t2.object_id
                INNER JOIN sys.schemas s2 ON t2.schema_id = s2.schema_id
                INNER JOIN sys.tables t1 ON fk.parent_object_id = t1.object_id
                WHERE s2.name = @Schema AND t2.name = @Name;
                """,
                conn);
            fkCmd.Parameters.AddWithValue("@Schema", schema);
            fkCmd.Parameters.AddWithValue("@Name", objectName);
            await using var fkReader = await fkCmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await fkReader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!fkReader.IsDBNull(0))
                {
                    _ = related.Add(fkReader.GetString(0));
                }
            }
        }
        else
        {
            await using var depCmd = new SqlCommand(
                """
                SELECT DISTINCT QUOTENAME(ISNULL(referenced_schema_name, N'dbo')) + N'.' + QUOTENAME(referenced_entity_name)
                FROM sys.sql_expression_dependencies
                WHERE referencing_id = OBJECT_ID(QUOTENAME(@Schema) + N'.' + QUOTENAME(@Name))
                  AND referenced_entity_name IS NOT NULL;
                """,
                conn);
            depCmd.Parameters.AddWithValue("@Schema", schema);
            depCmd.Parameters.AddWithValue("@Name", objectName);
            await using var depReader = await depCmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await depReader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!depReader.IsDBNull(0))
                {
                    _ = related.Add(depReader.GetString(0));
                }
            }
        }

        return related.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static async Task<List<Dictionary<string, object?>>> QueryRecentInsightsAsync(SqlConnection conn, CancellationToken cancellationToken)
    {
        var list = new List<(string Type, string Name, string? Description, DateTime Date, string? By)>();

        const string schemaSql = """
            SELECT TOP (50)
                CAST(N'Schema' AS NVARCHAR(20)),
                ObjectName,
                Description,
                LastAnalyzed,
                AnalyzedBy
            FROM AIInsights.SchemaInsights
            WHERE LastAnalyzed >= DATEADD(day, -7, GETDATE())
            ORDER BY LastAnalyzed DESC;
            """;

        await using (var cmd = new SqlCommand(schemaSql, conn))
        {
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                list.Add((
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.GetDateTime(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4)));
            }
        }

        return list
            .OrderByDescending(x => x.Date)
            .Select(x => new Dictionary<string, object?>
            {
                ["insightType"] = x.Type,
                ["name"] = x.Name,
                ["description"] = x.Description,
                ["date"] = x.Date,
                ["by"] = x.By
            })
            .ToList();
    }

    private async Task ExecuteScriptBatchesAsync(string script, CancellationToken cancellationToken)
    {
        await using var conn = await _connectionFactory.GetOpenConnectionAsync();
        conn.FireInfoMessageEventOnUserErrors = true;
        foreach (var batch in SqlBatchSplitter.SplitBatches(script))
        {
            if (string.IsNullOrWhiteSpace(batch))
            {
                continue;
            }

            await using var cmd = new SqlCommand(batch, conn) { CommandTimeout = 300 };
            _ = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<string> ReadEmbeddedResourceAsync(Assembly assembly, string resourceName, CancellationToken cancellationToken)
    {
        var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded resource not found: {resourceName}. Available: {string.Join(", ", assembly.GetManifestResourceNames())}");
        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: false);
        return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string NormalizeSchema(string? schemaName) =>
        string.IsNullOrWhiteSpace(schemaName) ? "dbo" : schemaName.Trim();

    private static async Task<bool> AiInsightsInstalledAsync(SqlConnection conn, CancellationToken cancellationToken)
    {
        await using var cmd = new SqlCommand("SELECT CASE WHEN SCHEMA_ID(N'AIInsights') IS NULL THEN 0 ELSE 1 END;", conn);
        var scalar = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return scalar is int i && i == 1;
    }

    private static async Task<bool> DdlAuditTableExistsAsync(SqlConnection conn, CancellationToken cancellationToken)
    {
        await using var cmd = new SqlCommand("SELECT CASE WHEN OBJECT_ID(N'dbo.DDL_AuditLog', N'U') IS NULL THEN 0 ELSE 1 END;", conn);
        var scalar = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return scalar is int i && i == 1;
    }

    private static async Task<bool> DdlWatermarkTableExistsAsync(SqlConnection conn, CancellationToken cancellationToken)
    {
        await using var cmd = new SqlCommand("SELECT CASE WHEN OBJECT_ID(N'AIInsights.DdlChangeWatermark', N'U') IS NULL THEN 0 ELSE 1 END;", conn);
        var scalar = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return scalar is int i && i == 1;
    }

    private static async Task<bool> DdlAuditTriggerExistsAsync(SqlConnection conn, CancellationToken cancellationToken)
    {
        await using var cmd = new SqlCommand(
            "SELECT CASE WHEN EXISTS (SELECT 1 FROM sys.triggers WHERE name = N'DDL_Audit' AND parent_class = 0) THEN 1 ELSE 0 END;",
            conn);
        var scalar = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return scalar is int i && i == 1;
    }

    private static async Task<int> ReadWatermarkAsync(SqlConnection conn, CancellationToken cancellationToken)
    {
        await using var cmd = new SqlCommand(
            "SELECT LastProcessedAuditID FROM AIInsights.DdlChangeWatermark WHERE SingletonId = 1;",
            conn);
        var scalar = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return scalar is int id ? id : 0;
    }

    private static async Task UpdateWatermarkAsync(SqlConnection conn, int lastId, CancellationToken cancellationToken)
    {
        await using var cmd = new SqlCommand(
            """
            UPDATE AIInsights.DdlChangeWatermark
            SET LastProcessedAuditID = @Id,
                LastProcessedAt = SYSUTCDATETIME()
            WHERE SingletonId = 1;
            """,
            conn);
        cmd.Parameters.AddWithValue("@Id", lastId);
        _ = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private sealed record DdlAuditRow(
        int Id,
        string? SchemaName,
        string? ObjectName,
        string? ObjectType,
        string? EventType);

    private static async Task<List<DdlAuditRow>> ReadDdlAuditRowsAsync(SqlConnection conn, int afterId, CancellationToken cancellationToken)
    {
        await using var cmd = new SqlCommand(
            """
            SELECT ID, SchemaName, ObjectName, ObjectType, EventType
            FROM dbo.DDL_AuditLog
            WHERE ID > @AfterId
            ORDER BY ID ASC;
            """,
            conn);
        cmd.Parameters.AddWithValue("@AfterId", afterId);
        var rows = new List<DdlAuditRow>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new DdlAuditRow(
                reader.GetInt32(0),
                reader.IsDBNull(1) ? null : reader.GetString(1).Trim(),
                reader.IsDBNull(2) ? null : reader.GetString(2).Trim(),
                reader.IsDBNull(3) ? null : reader.GetString(3).Trim(),
                reader.IsDBNull(4) ? null : reader.GetString(4).Trim()));
        }

        return rows;
    }

    private async Task<List<ObjectIdentity>> ArchiveInsightsForDdlObjectAsync(SqlConnection conn, DdlAuditRow row, CancellationToken cancellationToken)
    {
        var archived = new List<ObjectIdentity>();
        if (string.IsNullOrWhiteSpace(row.ObjectName))
        {
            return archived;
        }

        var schema = string.IsNullOrWhiteSpace(row.SchemaName) ? "dbo" : row.SchemaName.Trim();
        var objectName = row.ObjectName.Trim();
        var matchedInsightType = MapAuditTypeToInsightType(row.ObjectType, row.EventType);
        if (matchedInsightType is null)
        {
            return archived;
        }

        var liveObjectId = await TryResolveObjectIdForInsightTypeAsync(conn, matchedInsightType, schema, objectName, cancellationToken).ConfigureAwait(false);
        if (IsRenameEvent(row.EventType) && liveObjectId.HasValue)
        {
            await UpdateInsightNamesForRenameAsync(conn, matchedInsightType, liveObjectId.Value, schema, objectName, cancellationToken).ConfigureAwait(false);
        }

        await using var cmd = new SqlCommand(
            """
            SELECT InsightID, ObjectIdAtAnalysis FROM AIInsights.SchemaInsights
            WHERE ObjectName = @ObjectName
              AND ObjectType = @ObjectType
              AND (
                    SchemaName = @SchemaName
                    OR (SchemaName IS NULL AND @SchemaName = N'dbo')
                    OR (@SchemaName = N'dbo' AND SchemaName = N'dbo')
                  );
            """,
            conn);
        cmd.Parameters.AddWithValue("@ObjectName", objectName);
        cmd.Parameters.AddWithValue("@ObjectType", matchedInsightType);
        cmd.Parameters.AddWithValue("@SchemaName", schema);

        var ids = new List<(int Id, int? ObjectIdAtAnalysis)>();
        await using (var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                ids.Add((reader.GetInt32(0), reader.IsDBNull(1) ? null : reader.GetInt32(1)));
            }
        }

        foreach (var candidate in ids)
        {
            if (liveObjectId.HasValue && candidate.ObjectIdAtAnalysis.HasValue && candidate.ObjectIdAtAnalysis.Value != liveObjectId.Value)
            {
                // Rename or name reuse edge-case: this insight row points at a different object_id.
                continue;
            }

            await ArchiveInsightAsync(conn, candidate.Id, "DdlAudit", "ProcessDdlChanges", row.Id, cancellationToken).ConfigureAwait(false);
            archived.Add(new ObjectIdentity(matchedInsightType, schema, objectName));
        }

        return archived;
    }

    private static bool IsRenameEvent(string? eventType) =>
        !string.IsNullOrWhiteSpace(eventType)
        && eventType.Contains("RENAME", StringComparison.OrdinalIgnoreCase);

    private static string? MapAuditTypeToInsightType(string? objectType, string? eventType)
    {
        if (!string.IsNullOrWhiteSpace(objectType))
        {
            var t = objectType.Trim().ToUpperInvariant();
            if (t.Contains("TABLE"))
            {
                return "Table";
            }

            if (t.Contains("VIEW"))
            {
                return "View";
            }

            if (t.Contains("PROCEDURE") || t.Contains("PROC"))
            {
                return "Procedure";
            }

            if (t.Contains("FUNCTION"))
            {
                return "Function";
            }

            if (t.Contains("TRIGGER"))
            {
                return "Trigger";
            }
        }

        if (!string.IsNullOrWhiteSpace(eventType))
        {
            var e = eventType.Trim().ToUpperInvariant();
            if (e.Contains("_TABLE"))
            {
                return "Table";
            }

            if (e.Contains("_VIEW"))
            {
                return "View";
            }

            if (e.Contains("_PROCEDURE") || e.Contains("_PROC"))
            {
                return "Procedure";
            }

            if (e.Contains("_FUNCTION"))
            {
                return "Function";
            }

            if (e.Contains("_TRIGGER"))
            {
                return "Trigger";
            }
        }

        return null;
    }

    private static async Task<int?> TryResolveObjectIdForInsightTypeAsync(
        SqlConnection conn,
        string insightType,
        string schema,
        string objectName,
        CancellationToken cancellationToken)
    {
        if (insightType == "Trigger")
        {
            await using var triggerCmd = new SqlCommand(
                """
                SELECT TOP (1) tr.object_id
                FROM sys.triggers tr
                INNER JOIN sys.objects parent ON tr.parent_id = parent.object_id
                INNER JOIN sys.schemas s ON parent.schema_id = s.schema_id
                WHERE tr.name = @ObjectName
                  AND s.name = @Schema
                  AND tr.parent_class = 1;
                """,
                conn);
            triggerCmd.Parameters.AddWithValue("@ObjectName", objectName);
            triggerCmd.Parameters.AddWithValue("@Schema", schema);
            var triggerScalar = await triggerCmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return triggerScalar is int triggerObjectId ? triggerObjectId : null;
        }

        var sql = insightType switch
        {
            "Table" => """
                SELECT TOP (1) o.object_id
                FROM sys.objects o
                INNER JOIN sys.schemas s ON o.schema_id = s.schema_id
                WHERE o.name = @ObjectName
                  AND s.name = @Schema
                  AND o.type = 'U';
                """,
            "View" => """
                SELECT TOP (1) o.object_id
                FROM sys.objects o
                INNER JOIN sys.schemas s ON o.schema_id = s.schema_id
                WHERE o.name = @ObjectName
                  AND s.name = @Schema
                  AND o.type = 'V';
                """,
            "Procedure" => """
                SELECT TOP (1) o.object_id
                FROM sys.objects o
                INNER JOIN sys.schemas s ON o.schema_id = s.schema_id
                WHERE o.name = @ObjectName
                  AND s.name = @Schema
                  AND o.type = 'P';
                """,
            "Function" => """
                SELECT TOP (1) o.object_id
                FROM sys.objects o
                INNER JOIN sys.schemas s ON o.schema_id = s.schema_id
                WHERE o.name = @ObjectName
                  AND s.name = @Schema
                  AND o.type IN ('FN','IF','TF','FT');
                """,
            _ => """
                SELECT TOP (1) o.object_id
                FROM sys.objects o
                INNER JOIN sys.schemas s ON o.schema_id = s.schema_id
                WHERE o.name = @ObjectName
                  AND s.name = @Schema;
                """
        };

        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@ObjectName", objectName);
        cmd.Parameters.AddWithValue("@Schema", schema);
        var scalar = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return scalar is int objectId ? objectId : null;
    }

    private static async Task UpdateInsightNamesForRenameAsync(
        SqlConnection conn,
        string insightType,
        int objectId,
        string newSchema,
        string newObjectName,
        CancellationToken cancellationToken)
    {
        await using var cmd = new SqlCommand(
            """
            UPDATE AIInsights.SchemaInsights
            SET SchemaName = @NewSchema,
                ObjectName = @NewObjectName
            WHERE ObjectType = @ObjectType
              AND ObjectIdAtAnalysis = @ObjectId
              AND (SchemaName <> @NewSchema OR ObjectName <> @NewObjectName);
            """,
            conn);
        cmd.Parameters.AddWithValue("@NewSchema", newSchema);
        cmd.Parameters.AddWithValue("@NewObjectName", newObjectName);
        cmd.Parameters.AddWithValue("@ObjectType", insightType);
        cmd.Parameters.AddWithValue("@ObjectId", objectId);
        _ = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<List<ObjectIdentity>> ScanFingerprintsAndArchiveAsync(SqlConnection conn, int take, CancellationToken cancellationToken)
    {
        await using var cmd = new SqlCommand(
            """
            SELECT TOP (@Take) InsightID, ObjectType, SchemaName, ObjectName, ObjectIdAtAnalysis
            FROM AIInsights.SchemaInsights
            WHERE ColumnName IS NULL
            ORDER BY InsightID ASC;
            """,
            conn);
        cmd.Parameters.AddWithValue("@Take", take);

        var rows = new List<(int Id, string Type, string? Schema, string Name, int? ObjectIdAtAnalysis)>();
        var archived = new List<ObjectIdentity>();
        await using (var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                rows.Add((
                    reader.GetInt32(0),
                    reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetInt32(4)));
            }
        }

        foreach (var row in rows)
        {
            var schema = NormalizeSchema(row.Schema);
            var live = await TryComputeLiveFingerprintAsync(conn, row.Type, schema, row.Name, cancellationToken).ConfigureAwait(false);
            if (live.State == LiveObjectState.Missing)
            {
                await ArchiveInsightAsync(conn, row.Id, "ObjectMissing", "FingerprintScan", null, cancellationToken).ConfigureAwait(false);
                archived.Add(new ObjectIdentity(row.Type, schema, row.Name));
                continue;
            }

            if (live.State is LiveObjectState.AccessDenied or LiveObjectState.DefinitionUnavailable)
            {
                continue;
            }

            await using var readCmd = new SqlCommand(
                "SELECT ModifyDateAtAnalysis, SchemaFingerprint FROM AIInsights.SchemaInsights WHERE InsightID = @Id;",
                conn);
            readCmd.Parameters.AddWithValue("@Id", row.Id);
            await using var r2 = await readCmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await r2.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            var storedModify = r2.IsDBNull(0) ? (DateTime?)null : r2.GetDateTime(0);
            var storedFp = r2.IsDBNull(1) ? null : r2.GetString(1);
            await r2.CloseAsync().ConfigureAwait(false);

            var stale = IsStaleAgainstLive(
                row.ObjectIdAtAnalysis,
                storedModify,
                storedFp,
                live.ObjectId,
                live.ModifyDate,
                live.Fingerprint);
            if (stale)
            {
                await ArchiveInsightAsync(conn, row.Id, "FingerprintMismatch", "FingerprintScan", null, cancellationToken).ConfigureAwait(false);
                archived.Add(new ObjectIdentity(row.Type, schema, row.Name));
            }
        }

        return archived;
    }

    private async Task ArchiveInsightAsync(SqlConnection conn, int insightId, string reason, string archivedByEvent, int? sourceDdlAuditId, CancellationToken cancellationToken)
    {
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using (var insert = new SqlCommand(
                """
                INSERT INTO AIInsights.InsightHistory (
                    OriginalInsightID, ObjectType, SchemaName, ObjectName, ColumnName,
                    Description, BusinessPurpose, DataPatterns, UsageGuidelines, RelatedObjects,
                    LLMModel, Confidence, LastAnalyzed, AnalyzedBy, Version, ModifyDateAtAnalysis, ObjectIdAtAnalysis, SchemaFingerprint,
                    ArchiveReason, ArchivedByEvent, SourceDdlAuditID)
                SELECT
                    InsightID, ObjectType, SchemaName, ObjectName, ColumnName,
                    Description, BusinessPurpose, DataPatterns, UsageGuidelines, RelatedObjects,
                    LLMModel, Confidence, LastAnalyzed, AnalyzedBy, Version, ModifyDateAtAnalysis, ObjectIdAtAnalysis, SchemaFingerprint,
                    @Reason, @ArchivedBy, @SourceId
                FROM AIInsights.SchemaInsights
                WHERE InsightID = @InsightId;
                """,
                conn,
                tx))
            {
                insert.Parameters.AddWithValue("@InsightId", insightId);
                insert.Parameters.AddWithValue("@Reason", reason);
                insert.Parameters.AddWithValue("@ArchivedBy", archivedByEvent);
                insert.Parameters.AddWithValue("@SourceId", sourceDdlAuditId ?? (object)DBNull.Value);
                _ = await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using (var del = new SqlCommand("DELETE FROM AIInsights.SchemaInsights WHERE InsightID = @InsightId;", conn, tx))
            {
                del.Parameters.AddWithValue("@InsightId", insightId);
                _ = await del.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await tx.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    private static async Task<SchemaInsight?> TryLoadInsightRowAsync(SqlConnection conn, string objectType, string schema, string objectName, CancellationToken cancellationToken)
    {
        await using var cmd = new SqlCommand(
            """
            SELECT TOP (1)
                InsightID, ObjectType, SchemaName, ObjectName, ColumnName,
                Description, BusinessPurpose, DataPatterns, UsageGuidelines, RelatedObjects,
                LLMModel, Confidence, LastAnalyzed, AnalyzedBy, Version, ModifyDateAtAnalysis, ObjectIdAtAnalysis, SchemaFingerprint
            FROM AIInsights.SchemaInsights
            WHERE ObjectType = @ObjectType
              AND ObjectName = @ObjectName
              AND ColumnName IS NULL
              AND (SchemaName = @SchemaName OR (SchemaName IS NULL AND @SchemaName = N'dbo'))
            ORDER BY LastAnalyzed DESC;
            """,
            conn);
        cmd.Parameters.AddWithValue("@ObjectType", objectType);
        cmd.Parameters.AddWithValue("@ObjectName", objectName);
        cmd.Parameters.AddWithValue("@SchemaName", schema);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return MapInsight(reader);
    }

    private static SchemaInsight MapInsight(SqlDataReader reader) =>
        new()
        {
            InsightId = reader.GetInt32(0),
            ObjectType = reader.GetString(1),
            SchemaName = reader.IsDBNull(2) ? null : reader.GetString(2),
            ObjectName = reader.GetString(3),
            ColumnName = reader.IsDBNull(4) ? null : reader.GetString(4),
            Description = reader.IsDBNull(5) ? null : reader.GetString(5),
            BusinessPurpose = reader.IsDBNull(6) ? null : reader.GetString(6),
            DataPatterns = reader.IsDBNull(7) ? null : reader.GetString(7),
            UsageGuidelines = reader.IsDBNull(8) ? null : reader.GetString(8),
            RelatedObjects = reader.IsDBNull(9) ? null : reader.GetString(9),
            LlmModel = reader.IsDBNull(10) ? null : reader.GetString(10),
            Confidence = reader.IsDBNull(11) ? null : reader.GetDecimal(11),
            LastAnalyzed = reader.GetDateTime(12),
            AnalyzedBy = reader.IsDBNull(13) ? null : reader.GetString(13),
            Version = reader.GetInt32(14),
            ModifyDateAtAnalysis = reader.IsDBNull(15) ? null : reader.GetDateTime(15),
            ObjectIdAtAnalysis = reader.IsDBNull(16) ? null : reader.GetInt32(16),
            SchemaFingerprint = reader.IsDBNull(17) ? null : reader.GetString(17)
        };

    private static async Task<LiveFingerprintResult> TryComputeLiveFingerprintAsync(
        SqlConnection conn,
        string objectType,
        string schema,
        string objectName,
        CancellationToken cancellationToken)
    {
        var type = objectType.Trim();
        return type.ToUpperInvariant() switch
        {
            "TABLE" => await ComputeTableSignatureAsync(conn, schema, objectName, cancellationToken).ConfigureAwait(false),
            "VIEW" => await ComputeDefinitionSignatureAsync(conn, schema, objectName, "V", cancellationToken).ConfigureAwait(false),
            "PROCEDURE" or "PROC" => await ComputeDefinitionSignatureAsync(conn, schema, objectName, "P", cancellationToken).ConfigureAwait(false),
            "FUNCTION" => await ComputeDefinitionSignatureAsync(conn, schema, objectName, "FN,IF,TF,FT", cancellationToken).ConfigureAwait(false),
            "TRIGGER" => await ComputeTriggerSignatureAsync(conn, schema, objectName, cancellationToken).ConfigureAwait(false),
            _ => await ComputeDefinitionSignatureAsync(conn, schema, objectName, "U,V,P,FN,IF,TF,FT", cancellationToken).ConfigureAwait(false)
        };
    }

    private static async Task<LiveFingerprintResult> ComputeTableSignatureAsync(
        SqlConnection conn,
        string schema,
        string tableName,
        CancellationToken cancellationToken)
    {
        var sb = new StringBuilder();
        DateTime? modifyDate = null;
        int? objectId = null;

        await using (var cmd = new SqlCommand(
            """
            SELECT t.object_id, t.modify_date
            FROM sys.tables t
            INNER JOIN sys.schemas s ON t.schema_id = s.schema_id
            WHERE t.name = @Name AND s.name = @Schema;
            """,
            conn))
        {
            cmd.Parameters.AddWithValue("@Name", tableName);
            cmd.Parameters.AddWithValue("@Schema", schema);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                objectId = reader.GetInt32(0);
                modifyDate = reader.GetDateTime(1);
            }
            else
            {
                return new LiveFingerprintResult(LiveObjectState.Missing, null, null, null);
            }
        }

        await using (var cmd = new SqlCommand(
            """
            SELECT c.column_id, c.name, ty.name, c.max_length, c.precision, c.scale, c.is_nullable
            FROM sys.columns c
            INNER JOIN sys.tables t ON c.object_id = t.object_id
            INNER JOIN sys.schemas s ON t.schema_id = s.schema_id
            INNER JOIN sys.types ty ON c.user_type_id = ty.user_type_id
            WHERE t.name = @Name AND s.name = @Schema
            ORDER BY c.column_id;
            """,
            conn))
        {
            cmd.Parameters.AddWithValue("@Name", tableName);
            cmd.Parameters.AddWithValue("@Schema", schema);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                sb.Append(reader.GetInt32(0)).Append('|')
                    .Append(reader.GetString(1)).Append('|')
                    .Append(reader.GetString(2)).Append('|')
                    .Append(Convert.ToInt32(reader.GetValue(3), System.Globalization.CultureInfo.InvariantCulture)).Append('|')
                    .Append(Convert.ToInt32(reader.GetValue(4), System.Globalization.CultureInfo.InvariantCulture)).Append('|')
                    .Append(Convert.ToInt32(reader.GetValue(5), System.Globalization.CultureInfo.InvariantCulture)).Append('|')
                    .Append(reader.GetBoolean(6)).Append(';');
            }
        }

        return new LiveFingerprintResult(LiveObjectState.Found, modifyDate, objectId, ComputeSha256Hex(sb.ToString()));
    }

    private static async Task<LiveFingerprintResult> ComputeDefinitionSignatureAsync(
        SqlConnection conn,
        string schema,
        string objectName,
        string typesCsv,
        CancellationToken cancellationToken)
    {
        var types = typesCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var t in types)
        {
            await using var cmd = new SqlCommand(
                """
                SELECT o.object_id, o.modify_date, OBJECT_DEFINITION(o.object_id) AS definition,
                       HAS_PERMS_BY_NAME(QUOTENAME(s.name) + N'.' + QUOTENAME(o.name), N'OBJECT', N'VIEW DEFINITION') AS has_view_definition
                FROM sys.objects o
                INNER JOIN sys.schemas s ON o.schema_id = s.schema_id
                WHERE o.name = @Name AND s.name = @Schema AND o.type = @Type;
                """,
                conn);
            cmd.Parameters.AddWithValue("@Name", objectName);
            cmd.Parameters.AddWithValue("@Schema", schema);
            cmd.Parameters.AddWithValue("@Type", t);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var objectId = reader.GetInt32(0);
                var modify = reader.GetDateTime(1);
                var hasViewDefinition = !reader.IsDBNull(3) && Convert.ToInt32(reader.GetValue(3), System.Globalization.CultureInfo.InvariantCulture) == 1;
                if (!hasViewDefinition)
                {
                    return new LiveFingerprintResult(LiveObjectState.AccessDenied, modify, objectId, null);
                }

                if (reader.IsDBNull(2))
                {
                    return new LiveFingerprintResult(LiveObjectState.DefinitionUnavailable, modify, objectId, null);
                }

                var def = reader.GetString(2);
                return new LiveFingerprintResult(LiveObjectState.Found, modify, objectId, ComputeSha256Hex(def));
            }
        }

        return new LiveFingerprintResult(LiveObjectState.Missing, null, null, null);
    }

    private static async Task<LiveFingerprintResult> ComputeTriggerSignatureAsync(
        SqlConnection conn,
        string schema,
        string triggerName,
        CancellationToken cancellationToken)
    {
        await using var cmd = new SqlCommand(
            """
            SELECT tr.object_id, o.modify_date, OBJECT_DEFINITION(tr.object_id) AS definition,
                   HAS_PERMS_BY_NAME(QUOTENAME(s.name) + N'.' + QUOTENAME(parent.name), N'OBJECT', N'VIEW DEFINITION') AS has_view_definition
            FROM sys.triggers tr
            INNER JOIN sys.objects o ON tr.object_id = o.object_id
            INNER JOIN sys.objects parent ON tr.parent_id = parent.object_id
            INNER JOIN sys.schemas s ON parent.schema_id = s.schema_id
            WHERE tr.name = @Name AND s.name = @Schema AND tr.parent_class = 1;
            """,
            conn);
        cmd.Parameters.AddWithValue("@Name", triggerName);
        cmd.Parameters.AddWithValue("@Schema", schema);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return new LiveFingerprintResult(LiveObjectState.Missing, null, null, null);
        }

        var objectId = reader.GetInt32(0);
        var modify = reader.GetDateTime(1);
        var hasViewDefinition = !reader.IsDBNull(3) && Convert.ToInt32(reader.GetValue(3), System.Globalization.CultureInfo.InvariantCulture) == 1;
        if (!hasViewDefinition)
        {
            return new LiveFingerprintResult(LiveObjectState.AccessDenied, modify, objectId, null);
        }

        if (reader.IsDBNull(2))
        {
            return new LiveFingerprintResult(LiveObjectState.DefinitionUnavailable, modify, objectId, null);
        }

        var def = reader.GetString(2);
        return new LiveFingerprintResult(LiveObjectState.Found, modify, objectId, ComputeSha256Hex(def));
    }

    private static string ComputeSha256Hex(string input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return string.Empty;
        }

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        var hex = Convert.ToHexString(bytes);
        return hex.Length <= 64 ? hex : hex[..64];
    }
}

public static class InsightsLayerEnvironment
{
    private static bool IsDisabledByFalseyValue(string variableName)
    {
        var v = Environment.GetEnvironmentVariable(variableName);
        if (string.IsNullOrWhiteSpace(v))
        {
            return false;
        }

        var trimmed = v.Trim();
        return string.Equals(trimmed, "false", StringComparison.OrdinalIgnoreCase)
            || trimmed == "0"
            || string.Equals(trimmed, "no", StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, "off", StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, "disabled", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True unless <c>USE_INSIGHTS_LAYER</c> is explicitly set to a falsey value
    /// (<c>false</c>, <c>0</c>, <c>no</c>, <c>off</c>, <c>disabled</c>). The variable is
    /// therefore an opt-OUT switch: missing/empty == enabled.
    /// </summary>
    public static bool IsInsightsLayerEnabled => !IsDisabledByFalseyValue("USE_INSIGHTS_LAYER");

    /// <summary>
    /// True unless <c>INSIGHTS_AUTOPOPULATE</c> is explicitly set to a falsey value.
    /// </summary>
    public static bool IsAutoPopulationEnabled => !IsDisabledByFalseyValue("INSIGHTS_AUTOPOPULATE");

    /// <summary>
    /// Derived from the two public feature switches only:
    /// when insights + auto-population are enabled, enrichment directives are enabled.
    /// </summary>
    public static bool IsEnrichmentDirectiveEnabled => IsInsightsLayerEnabled && IsAutoPopulationEnabled;

    /// <summary>
    /// Derived from the two public feature switches only:
    /// when insights + auto-population are enabled, baseline row-count probing is enabled.
    /// </summary>
    public static bool IsAutoPopulationRowCountsEnabled => IsInsightsLayerEnabled && IsAutoPopulationEnabled;

    /// <summary>
    /// Derived from the two public feature switches only:
    /// when insights + auto-population are enabled, fresh auto-mechanical rows may be refreshed.
    /// </summary>
    public static bool IsAutoPopulationRefreshEnabled => IsInsightsLayerEnabled && IsAutoPopulationEnabled;
}
