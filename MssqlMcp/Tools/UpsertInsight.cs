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
        Description("Creates or updates a cached AI insight in AIInsights.SchemaInsights for a database object. Table-level row when columnName is null; column-level row when columnName is set. Implementation runs UPDATE first and only INSERTs when no row matched (no IF EXISTS round-trip). The schema fingerprint and object_id of the live object are captured automatically. Optional string parameters appear as 'required' in the generated MCP schema but accept JSON null when not used.")]
    public async Task<DbOperationResult> UpsertInsight(
        [Description("Object type label stored in AIInsights: 'Table' | 'View' | 'Procedure' | 'Function' | 'Trigger'.")] string objectType,
        [Description("Schema name. Pass 'dbo' for default schema; pass null only when unknown.")] string? schemaName,
        [Description("Object name without schema (e.g. 'TableProblems').")] string objectName,
        [Description("Short single-sentence description of what this object is.")] string description,
        [Description("Why this object exists from a business/operational perspective. Pass null to skip.")] string? businessPurpose = null,
        [Description("Data patterns: typical volume, keys, hot filters, partitioning hints. Pass null to skip.")] string? dataPatterns = null,
        [Description("Usage guidelines for analysts/agents (preferred joins, filters, gotchas). Pass null to skip.")] string? usageGuidelines = null,
        [Description("Related objects as a JSON array string, e.g. '[\"Buildings\",\"Suppliers\"]'. Pass null to skip.")] string? relatedObjects = null,
        [Description("Identifier of the LLM (or 'manual-validation') that produced the insight. Stored for provenance.")] string llmModel = "unknown",
        [Description("Confidence score between 0 and 1. Use 0.8 as a sensible default.")] decimal confidence = 0.8m,
        [Description("Who/what authored the insight (agent name, ticket id, etc.).")] string analyzedBy = "MCP",
        [Description("Column name when authoring a column-level insight. Pass null for table/object-level insights.")] string? columnName = null)
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
