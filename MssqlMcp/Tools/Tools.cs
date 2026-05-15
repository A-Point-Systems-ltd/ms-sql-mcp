// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.

using System.Data;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using Mssql.McpServer.InsightsLayer;
using Mssql.McpServer.InsightsLayer.Models;

namespace Mssql.McpServer;

// Register this class as a tool container
[McpServerToolType]
public partial class Tools(
    ISqlConnectionFactory connectionFactory,
    IInsightsLayerService insightsLayer,
    IInsightDdlProcessingQueue insightDdlProcessingQueue,
    ILogger<Tools> logger)
{
    private readonly ISqlConnectionFactory _connectionFactory = connectionFactory;
    private readonly IInsightsLayerService _insightsLayer = insightsLayer;
    private readonly IInsightDdlProcessingQueue _insightDdlProcessingQueue = insightDdlProcessingQueue;
    private readonly ILogger<Tools> _logger = logger;

    /// <summary>
    /// Best-effort: attaches cached AI insight metadata to introspection tool payloads.
    /// </summary>
    protected async Task TryAttachInsightAsync(
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

        try
        {
            var (insight, freshness) = await _insightsLayer
                .GetInsightForObjectAsync(objectType, schemaName, objectName, cancellationToken)
                .ConfigureAwait(false);
            if (InsightsLayerEnvironment.IsAutoPopulationEnabled
                && freshness is InsightFreshness.Absent or InsightFreshness.StaleArchived)
            {
                (insight, freshness) = await _insightsLayer
                    .EnsureBaselineForObjectAsync(objectType, schemaName, objectName, cancellationToken)
                    .ConfigureAwait(false);
            }

            var enrichmentSuggested = IsAutoMechanical(insight);
            result["insight"] = ProjectInsightForResponse(insight);
            result["insightFreshness"] = freshness.ToString();
            result["enrichmentSuggested"] = enrichmentSuggested;
            if (InsightsLayerEnvironment.IsEnrichmentDirectiveEnabled && enrichmentSuggested && insight is not null)
            {
                var schema = string.IsNullOrWhiteSpace(schemaName) ? "dbo" : schemaName!.Trim();
                var qualified = $"{schema}.{objectName}";
                result["_agentDirective"] =
                    $"MANDATORY: Call UpsertInsight for {qualified} (and any relatedObjectsToIntrospect) BEFORE producing your final answer. "
                    + "The args template is in insightEnrichment.nextAction.args. Skipping this is a MCP-Insight-Enrichment-v1 protocol violation.";
                result["pendingEnrichments"] = new[]
                {
                    new
                    {
                        tool = "UpsertInsight",
                        target = qualified,
                        objectType,
                        reason = "auto-mechanical baseline (confidence 0.30); enrich to upgrade cached insight."
                    }
                };
                result["insightEnrichment"] = BuildInsightEnrichmentDirective(insight);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Insight enrichment skipped for {ObjectType} {Schema}.{Object}", objectType, schemaName, objectName);
        }
    }

    /// <summary>
    /// Non-blocking post-write hook to process DDL audit rows / fingerprint drift.
    /// </summary>
    protected void QueueInsightDdlProcessing()
    {
        _insightDdlProcessingQueue.RequestProcessing();
    }

    private static object? ProjectInsightForResponse(SchemaInsight? insight)
    {
        if (insight is null)
        {
            return null;
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
            enrichmentSuggested = IsAutoMechanical(insight)
        };
    }

    private static bool IsAutoMechanical(SchemaInsight? insight) =>
        insight is not null
        && string.Equals(insight.LlmModel, InsightsLayerService.AutoMechanicalModel, StringComparison.OrdinalIgnoreCase);

    private static object BuildInsightEnrichmentDirective(SchemaInsight insight)
    {
        var schema = string.IsNullOrWhiteSpace(insight.SchemaName) ? "dbo" : insight.SchemaName!.Trim();
        var qualified = $"{schema}.{insight.ObjectName}";
        var related = ParseRelatedObjects(insight.RelatedObjects);
        return new
        {
            required = true,
            priority = "MUST",
            protocol = "MCP-Insight-Enrichment-v1",
            reason = $"Auto-mechanical baseline only (model='{insight.LlmModel}', confidence={insight.Confidence:0.00}). Cache will not help the next agent until you upgrade it.",
            contract = $"You MUST call UpsertInsight for {qualified} using nextAction.args (with every '<fill in: ...>' placeholder replaced) BEFORE producing your final answer to the user. This is a tool-call protocol obligation, not a suggestion.",
            consequenceOfSkipping = "Next agent inherits a placeholder insight and wastes tokens reinvestigating this object. Cache stays at confidence 0.30 indefinitely.",
            instructions = new[]
            {
                "1. Replace every '<fill in: ...>' placeholder in nextAction.args using the metadata you just received in this response.",
                "2. Call UpsertInsight with the completed args. Do NOT reuse llmModel='auto-mechanical' and do NOT pass confidence<=0.30 — those values indicate a placeholder, not a real authored insight.",
                "3. For each entry in relatedObjectsToIntrospect, call the matching introspection tool (DescribeTable / DescribeView / GetStoredProc / GetFunction / GetTrigger) and complete the same enrichment loop for it.",
                "4. Only AFTER every enrichment call, produce the final user-facing answer."
            },
            completionCriteria = $"AIInsights.SchemaInsights row for {qualified} updated with LLMModel != 'auto-mechanical' and Confidence > 0.30.",
            relatedObjectsToIntrospect = related,
            nextAction = new
            {
                tool = "UpsertInsight",
                args = new Dictionary<string, object?>
                {
                    ["objectType"] = insight.ObjectType,
                    ["schemaName"] = schema,
                    ["objectName"] = insight.ObjectName,
                    ["description"] = "<fill in: one short sentence>",
                    ["businessPurpose"] = "<fill in: why this object exists>",
                    ["dataPatterns"] = "<fill in: volume/keys/hot filters>",
                    ["usageGuidelines"] = "<fill in: preferred joins/filters and gotchas>",
                    ["relatedObjects"] = string.IsNullOrWhiteSpace(insight.RelatedObjects) ? "[]" : insight.RelatedObjects,
                    ["llmModel"] = "<fill in: model id (NOT 'auto-mechanical')>",
                    ["confidence"] = 0.85m,
                    ["analyzedBy"] = "<fill in: agent name>",
                    ["columnName"] = null
                }
            }
        };
    }

    private static IReadOnlyList<string> ParseRelatedObjects(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Array.Empty<string>();
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<string>();
            }

            var list = new List<string>(doc.RootElement.GetArrayLength());
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    var value = item.GetString();
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        list.Add(value!);
                    }
                }
            }
            return list;
        }
        catch (JsonException)
        {
            return Array.Empty<string>();
        }
    }

    // Helper to convert DataTable to a serializable list
    private static List<Dictionary<string, object>> DataTableToList(DataTable table)
    {
        var result = new List<Dictionary<string, object>>();
        foreach (DataRow row in table.Rows)
        {
            var dict = new Dictionary<string, object>();
            foreach (DataColumn col in table.Columns)
            {
                dict[col.ColumnName] = row[col];
            }
            result.Add(dict);
        }
        return result;
    }
}