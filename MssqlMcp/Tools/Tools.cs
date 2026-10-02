// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.

using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using Mssql.McpServer.Connections;
using Mssql.McpServer.InsightsLayer;
using Mssql.McpServer.InsightsLayer.Models;

namespace Mssql.McpServer;

// Register this class as a tool container
[McpServerToolType]
public partial class Tools(
    ISqlConnectionFactory connectionFactory,
    IInsightsLayerService insightsLayer,
    IInsightDdlProcessingQueue insightDdlProcessingQueue,
    ILogger<Tools> logger,
    ConnectionRegistry connections)
{
    private readonly ISqlConnectionFactory _connectionFactory = connectionFactory;
    private readonly IInsightsLayerService _insightsLayer = insightsLayer;
    private readonly IInsightDdlProcessingQueue _insightDdlProcessingQueue = insightDdlProcessingQueue;
    private readonly ILogger<Tools> _logger = logger;
    private readonly ConnectionRegistry _connections = connections;

    internal const string ConnectionParamDescription =
        "Name of the database connection to run against (see " + ToolNames.ListConnections + "). REQUIRED whenever the server has more than one connection - " +
        "there is no default connection, and omitting it returns an error listing the valid names. May be omitted only when exactly one connection exists.";

    internal const string InsightResponseNote =
        "May include insight, insightFreshness, enrichmentSuggested and insightEnrichment; call " + ToolNames.UpsertInsight +
        " only when insightEnrichment is present (see server instructions).";

    internal const string MultiConnectionNote = " With more than one connection, pass 'connection' (see " + ToolNames.ListConnections + ").";

    /// <summary>
    /// Best-effort: attaches cached AI insight metadata to introspection tool payloads.
    /// </summary>
    internal async Task TryAttachInsightAsync(
        Dictionary<string, object?> result,
        string objectType,
        string? schemaName,
        string objectName,
        CancellationToken cancellationToken = default)
    {
        if (!_insightsLayer.IsEnabled)
        {
            return;
        }

        SchemaInsight? insight;
        InsightFreshness freshness;
        try
        {
            (insight, freshness) = await _insightsLayer
                .GetInsightForObjectAsync(objectType, schemaName, objectName, cancellationToken)
                .ConfigureAwait(false);

            // A baseline also goes through EnsureBaseline: it restores a matching authored insight from history.
            if (InsightsLayerEnvironment.IsAutoPopulationEnabled
                && (freshness is InsightFreshness.Absent or InsightFreshness.StaleArchived || IsAutoMechanical(insight)))
            {
                (insight, freshness) = await _insightsLayer
                    .EnsureBaselineForObjectAsync(objectType, schemaName, objectName, cancellationToken)
                    .ConfigureAwait(false);
            }

            result["insight"] = ProjectInsightForResponse(insight, compact: true);
            result["insightFreshness"] = freshness.ToString();
            // false is the explicit "cached insight is current, do not call upsert_insight" signal.
            result["enrichmentSuggested"] = false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Insight lookup skipped for {ObjectType} {Schema}.{Object}", objectType, schemaName, objectName);
            return;
        }

        if (!InsightsLayerEnvironment.IsEnrichmentDirectiveEnabled || insight is null)
        {
            return;
        }

        // Separate from the lookup: a failure here must not drop the cached insight already attached.
        try
        {
            var context = await _insightsLayer
                .GetEnrichmentContextAsync(insight, freshness, cancellationToken)
                .ConfigureAwait(false);
            if (context is not null)
            {
                result["enrichmentSuggested"] = true;
                result["insightEnrichment"] = BuildInsightEnrichmentDirective(insight, context);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Insight enrichment context failed for {ObjectType} {Schema}.{Object}; the cached insight is returned without it.", objectType, schemaName, objectName);
        }
    }

    /// <summary>
    /// Non-blocking post-write hook to process DDL audit rows / fingerprint drift.
    /// </summary>
    protected void QueueInsightDdlProcessing()
    {
        _insightDdlProcessingQueue.RequestProcessing();
    }

    /// <summary>
    /// Insight as returned to the agent. <paramref name="compact"/> (introspection tools) leaves out identity
    /// fields the parent payload already carries and bookkeeping the agent never acts on (ids, version,
    /// fingerprint); an auto-mechanical baseline is reduced to the few facts it actually holds.
    /// </summary>
    internal static object? ProjectInsightForResponse(SchemaInsight? insight, bool compact = false)
    {
        if (insight is null)
        {
            return null;
        }

        if (compact)
        {
            if (IsAutoMechanical(insight))
            {
                return new
                {
                    llmModel = insight.LlmModel,
                    insight.Confidence,
                    insight.RelatedObjects,
                    insight.RowCountAtAnalysis
                };
            }

            return new
            {
                insight.Description,
                insight.BusinessPurpose,
                insight.DataPatterns,
                insight.UsageGuidelines,
                insight.RelatedObjects,
                llmModel = insight.LlmModel,
                insight.Confidence,
                insight.LastAnalyzed,
                insight.AnalyzedBy,
                insight.RowCountAtAnalysis
            };
        }

        return new
        {
            insight.InsightId,
            insight.ObjectType,
            insight.SchemaName,
            insight.ObjectName,
            insight.ColumnName,
            insight.Description,
            insight.BusinessPurpose,
            insight.DataPatterns,
            insight.UsageGuidelines,
            insight.RelatedObjects,
            llmModel = insight.LlmModel,
            insight.Confidence,
            insight.LastAnalyzed,
            insight.AnalyzedBy,
            insight.Version,
            insight.ModifyDateAtAnalysis,
            insight.SchemaFingerprint,
            insight.RowCountAtAnalysis,
            enrichmentSuggested = IsAutoMechanical(insight)
        };
    }

    private static bool IsAutoMechanical(SchemaInsight? insight) =>
        insight is not null
        && string.Equals(insight.LlmModel, InsightsLayerService.AutoMechanicalModel, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The single update request attached to an introspection response. Kept small: it says why, gives the
    /// previous authored insight to edit (so the agent does not re-investigate), and a ready upsert payload.
    /// </summary>
    internal static Dictionary<string, object?> BuildInsightEnrichmentDirective(SchemaInsight insight, InsightEnrichmentContext context)
    {
        var schema = string.IsNullOrWhiteSpace(insight.SchemaName) ? "dbo" : insight.SchemaName!.Trim();
        var previous = context.PreviousInsight;
        var directive = new Dictionary<string, object?>
        {
            ["trigger"] = context.Trigger.ToString()
        };

        switch (context.Trigger)
        {
            case InsightEnrichmentTrigger.StructureChanged:
                directive["reason"] = "The structure changed since the last authored insight.";
                directive["instruction"] = "Edit previousInsight for the changed structure, keeping what is still true, then call " + ToolNames.UpsertInsight + " with nextAction.args.";
                break;
            case InsightEnrichmentTrigger.DataPopulated:
                directive["reason"] = $"The insight was written with {previous?.RowCountAtAnalysis ?? 0} rows; the object now holds {context.RowsNow} or more.";
                directive["instruction"] = "Check dataPatterns and usageGuidelines against the real data (a small sample is enough), edit what changed, then call " + ToolNames.UpsertInsight + " with nextAction.args.";
                break;
            default:
                directive["reason"] = "Only an auto-generated baseline exists for this object.";
                directive["instruction"] = "Fill the placeholders in nextAction.args from this response, then call " + ToolNames.UpsertInsight + ".";
                break;
        }

        if (previous is not null)
        {
            directive["previousInsight"] = new
            {
                previous.Description,
                previous.BusinessPurpose,
                previous.DataPatterns,
                previous.UsageGuidelines,
                previous.RelatedObjects,
                previous.LastAnalyzed,
                previous.RowCountAtAnalysis
            };
        }

        if (context.StructuralEvents.Count > 0)
        {
            directive["structuralEvents"] = context.StructuralEvents
                .Take(InsightsLayerService.MaxStructuralEvents)
                .Select(e => new
                {
                    eventType = e.EventType,
                    postTime = e.PostTime,
                    commandText = e.CommandText is { Length: > InsightsLayerService.MaxEventCommandTextLength } text
                        ? text[..InsightsLayerService.MaxEventCommandTextLength]
                        : e.CommandText
                })
                .ToList();
        }

        if (context.RowsNow is { } rowsNow)
        {
            directive["rowsNow"] = rowsNow;
        }

        var relatedObjects = previous?.RelatedObjects ?? insight.RelatedObjects;
        directive["nextAction"] = new
        {
            tool = ToolNames.UpsertInsight,
            args = new Dictionary<string, object?>
            {
                ["objectType"] = insight.ObjectType,
                ["schemaName"] = schema,
                ["objectName"] = insight.ObjectName,
                ["description"] = previous?.Description ?? "<fill in: one short sentence>",
                ["businessPurpose"] = previous?.BusinessPurpose ?? "<fill in: why this object exists>",
                ["dataPatterns"] = previous?.DataPatterns ?? "<fill in: volume/keys/hot filters>",
                ["usageGuidelines"] = previous?.UsageGuidelines ?? "<fill in: preferred joins/filters and gotchas>",
                ["relatedObjects"] = string.IsNullOrWhiteSpace(relatedObjects) ? "[]" : relatedObjects,
                ["llmModel"] = "<your model id>",
                ["confidence"] = 0.85m,
                ["analyzedBy"] = "<agent name>",
                ["columnName"] = null
            }
        };

        return directive;
    }

    private static string? BuildNameLikePattern(string? partialName)
    {
        if (string.IsNullOrWhiteSpace(partialName))
        {
            return null;
        }

        return $"%{EscapeLikeLiteral(partialName.Trim())}%";
    }

    private static string EscapeLikeLiteral(string value) =>
        value
            .Replace("[", "[[]", StringComparison.Ordinal)
            .Replace("%", "[%]", StringComparison.Ordinal)
            .Replace("_", "[_]", StringComparison.Ordinal);

    private static void AddNamePatternParameter(SqlCommand cmd, string? partialName)
    {
        var pattern = BuildNameLikePattern(partialName);
        cmd.Parameters.AddWithValue("@NamePattern", pattern is null ? DBNull.Value : pattern);
    }

    /// <summary>
    /// Shared body of the write tools: validates the statement shape, runs it, and queues insight reconciliation.
    /// Every failure (including connection failures) becomes a <see cref="DbOperationResult"/> so the agent sees
    /// the real reason instead of the SDK's generic "An error occurred invoking" message.
    /// </summary>
    private async Task<DbOperationResult> ExecuteWriteAsync(
        string sql,
        SqlStatementKind kind,
        string toolName,
        bool includeRowsAffected,
        CancellationToken cancellationToken)
    {
        if (!SqlStatementClassifier.TryValidateWrite(sql, kind, out var validationError))
        {
            return new DbOperationResult(success: false, error: validationError);
        }

        try
        {
            await using var conn = await _connectionFactory.GetOpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var cmd = new SqlCommand(sql, conn);
            var rows = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            QueueInsightDdlProcessing();
            return includeRowsAffected
                ? new DbOperationResult(success: true, rowsAffected: rows)
                : new DbOperationResult(success: true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "{Tool} failed: {Message}", toolName, ex.Message);
            return new DbOperationResult(success: false, error: ex.Message);
        }
    }
}