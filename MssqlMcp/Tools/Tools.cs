// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.

using System.Data;
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
            result["insight"] = ProjectInsightForResponse(insight);
            result["insightFreshness"] = freshness.ToString();
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
            insight.SchemaFingerprint
        };
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