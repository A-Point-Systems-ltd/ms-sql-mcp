// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.

using System.ComponentModel;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using Mssql.McpServer.InsightsLayer.Models;

namespace Mssql.McpServer;

public partial class Tools
{
    [McpServerTool(
        Title = "Upsert Insight",
        ReadOnly = false,
        Idempotent = false,
        Destructive = false),
        Description("Creates or updates a cached AI insight for a schema object (table-level when columnName is omitted). Uses UPDATE then INSERT when no row exists.")]
    public async Task<DbOperationResult> UpsertInsight(
        [Description("Object type: Table, View, Procedure, Function, Trigger, etc.")] string objectType,
        [Description("Schema name; omit or dbo for default schema")] string? schemaName,
        [Description("Object name")] string objectName,
        [Description("Short description")] string description,
        [Description("Business purpose (optional)")] string? businessPurpose = null,
        [Description("Data patterns (optional)")] string? dataPatterns = null,
        [Description("Usage guidelines (optional)")] string? usageGuidelines = null,
        [Description("Related objects JSON (optional)")] string? relatedObjects = null,
        [Description("LLM model label")] string llmModel = "unknown",
        [Description("Confidence 0..1")] decimal confidence = 0.8m,
        [Description("Who analyzed / wrote the insight")] string analyzedBy = "MCP",
        [Description("Column name for column-level insights (optional)")] string? columnName = null)
    {
        var row = new SchemaInsight
        {
            InsightId = 0,
            ObjectType = objectType,
            SchemaName = schemaName,
            ObjectName = objectName,
            ColumnName = columnName,
            Description = description,
            BusinessPurpose = businessPurpose,
            DataPatterns = dataPatterns,
            UsageGuidelines = usageGuidelines,
            RelatedObjects = relatedObjects,
            LlmModel = llmModel,
            Confidence = confidence,
            LastAnalyzed = default,
            AnalyzedBy = analyzedBy,
            Version = 1,
            ModifyDateAtAnalysis = null,
            SchemaFingerprint = null
        };

        return await _insightsLayer.UpsertInsightAsync(row).ConfigureAwait(false);
    }
}
