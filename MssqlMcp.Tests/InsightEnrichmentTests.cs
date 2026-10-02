// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.

using System.Text.Json;
using Moq;
using Mssql.McpServer;
using Mssql.McpServer.InsightsLayer;
using Mssql.McpServer.InsightsLayer.Models;

namespace MssqlMcp.Tests;

[Collection(EnvVarLock.Name)]
public sealed class InsightEnrichmentTests
{
    [Theory]
    [InlineData(null, 500L, false)]
    [InlineData(50L, null, false)]
    [InlineData(50L, 120L, true)]
    [InlineData(50L, 99L, false)]
    [InlineData(0L, 100L, true)]
    [InlineData(99L, 100L, true)]
    [InlineData(100L, 1_000_000L, false)]
    [InlineData(500L, 10L, false)]
    public void Data_populated_only_when_written_below_threshold_and_now_at_or_above(long? before, long? now, bool expected)
    {
        Assert.Equal(expected, InsightsLayerService.IsDataPopulated(before, now));
    }

    [Theory]
    [InlineData("Table", true)]
    [InlineData("view", true)]
    [InlineData("Procedure", false)]
    [InlineData("Function", false)]
    [InlineData("Trigger", false)]
    public void Row_counts_tracked_for_tables_and_views_only(string type, bool expected)
    {
        Assert.Equal(expected, InsightsLayerService.IsRowCountTracked(type));
    }

    [Fact]
    public void Initial_baseline_directive_has_placeholders_and_no_previous_insight()
    {
        var ctx = new InsightEnrichmentContext(InsightEnrichmentTrigger.InitialBaselineOnly, null, [], null);
        var json = Serialize(Tools.BuildInsightEnrichmentDirective(Baseline(), ctx));

        Assert.Equal("InitialBaselineOnly", json.GetProperty("trigger").GetString());
        Assert.False(json.TryGetProperty("previousInsight", out _));
        Assert.False(json.TryGetProperty("structuralEvents", out _));
        var args = json.GetProperty("nextAction").GetProperty("args");
        Assert.StartsWith("<fill in", args.GetProperty("description").GetString());
        Assert.Equal("[\"[dbo].[Customers]\"]", args.GetProperty("relatedObjects").GetString());
    }

    [Fact]
    public void Structure_changed_directive_prefills_previous_insight_and_caps_events()
    {
        var events = Enumerable.Range(0, 8)
            .Select(i => new DdlEventSummary("ALTER_TABLE", new DateTime(2026, 10, 1, 10, i, 0), new string('x', 1000)))
            .ToList();
        var ctx = new InsightEnrichmentContext(InsightEnrichmentTrigger.StructureChanged, Authored(rows: 5000), events, null);
        var json = Serialize(Tools.BuildInsightEnrichmentDirective(Baseline(), ctx));

        Assert.Equal("StructureChanged", json.GetProperty("trigger").GetString());
        Assert.Equal("Orders placed by customers.", json.GetProperty("previousInsight").GetProperty("Description").GetString());
        var emitted = json.GetProperty("structuralEvents");
        Assert.Equal(InsightsLayerService.MaxStructuralEvents, emitted.GetArrayLength());
        Assert.All(emitted.EnumerateArray(), e =>
            Assert.True(e.GetProperty("commandText").GetString()!.Length <= InsightsLayerService.MaxEventCommandTextLength));
        AssertNoContentPlaceholders(json);
    }

    [Fact]
    public void Data_populated_directive_reports_rows_and_prefills()
    {
        var authored = Authored(rows: 3);
        var ctx = new InsightEnrichmentContext(InsightEnrichmentTrigger.DataPopulated, authored, [], 250);
        var json = Serialize(Tools.BuildInsightEnrichmentDirective(authored, ctx));

        Assert.Equal("DataPopulated", json.GetProperty("trigger").GetString());
        Assert.Equal(250, json.GetProperty("rowsNow").GetInt64());
        Assert.Equal(3, json.GetProperty("previousInsight").GetProperty("RowCountAtAnalysis").GetInt64());
        AssertNoContentPlaceholders(json);
    }

    [Fact]
    public void Directive_drops_the_old_protocol_and_fanout_fields()
    {
        var ctx = new InsightEnrichmentContext(InsightEnrichmentTrigger.InitialBaselineOnly, null, [], null);
        var json = Serialize(Tools.BuildInsightEnrichmentDirective(Baseline(), ctx));

        foreach (var removed in new[] { "required", "priority", "protocol", "contract", "consequenceOfSkipping", "instructions", "completionCriteria", "relatedObjectsToIntrospect" })
        {
            Assert.False(json.TryGetProperty(removed, out _), removed);
        }
    }

    [Fact]
    public void Compact_projection_omits_bookkeeping_fields()
    {
        var authored = Serialize(Tools.ProjectInsightForResponse(Authored(rows: 10), compact: true)!);
        var baseline = Serialize(Tools.ProjectInsightForResponse(Baseline(), compact: true)!);
        var full = Serialize(Tools.ProjectInsightForResponse(Authored(rows: 10))!);

        foreach (var json in new[] { authored, baseline })
        {
            Assert.False(json.TryGetProperty("SchemaFingerprint", out _));
            Assert.False(json.TryGetProperty("InsightId", out _));
            Assert.False(json.TryGetProperty("ObjectName", out _));
        }

        Assert.False(baseline.TryGetProperty("Description", out _));
        Assert.True(authored.TryGetProperty("Description", out _));
        Assert.True(full.TryGetProperty("SchemaFingerprint", out _));
    }

    [Fact]
    public async Task Context_failure_keeps_the_cached_insight_in_the_response()
    {
        var service = new Mock<IInsightsLayerService>();
        service.SetupGet(s => s.IsEnabled).Returns(true);
        service.Setup(s => s.GetInsightForObjectAsync("Table", "dbo", "Orders", It.IsAny<CancellationToken>()))
            .ReturnsAsync((Authored(rows: 5), InsightFreshness.Fresh));
        service.Setup(s => s.GetEnrichmentContextAsync(It.IsAny<SchemaInsight>(), It.IsAny<InsightFreshness>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("history unavailable"));
        var registry = new Mssql.McpServer.Connections.ConnectionRegistry(
            [new("default", "Server=a", false, true, Mssql.McpServer.Connections.ConnectionSource.Legacy)]);
        var tools = new Tools(
            new SqlConnectionFactory(registry),
            service.Object,
            NoOpInsightDdlProcessingQueue.Instance,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Tools>.Instance,
            registry);

        var result = new Dictionary<string, object?>();
        await tools.TryAttachInsightAsync(result, "Table", "dbo", "Orders");

        Assert.NotNull(result["insight"]);
        Assert.Equal("Fresh", result["insightFreshness"]);
        Assert.Equal(false, result["enrichmentSuggested"]);
        Assert.False(result.ContainsKey("insightEnrichment"));
    }

    private static void AssertNoContentPlaceholders(JsonElement directive)
    {
        var args = directive.GetProperty("nextAction").GetProperty("args");
        foreach (var name in new[] { "description", "businessPurpose", "dataPatterns", "usageGuidelines" })
        {
            Assert.DoesNotContain("<fill in", args.GetProperty(name).GetString(), StringComparison.Ordinal);
        }
    }

    private static JsonElement Serialize(object value) =>
        JsonSerializer.SerializeToElement(value);

    private static SchemaInsight Baseline() => new()
    {
        ObjectType = "Table",
        SchemaName = "dbo",
        ObjectName = "Orders",
        Description = "Auto-baseline for dbo.Orders",
        RelatedObjects = "[\"[dbo].[Customers]\"]",
        LlmModel = InsightsLayerService.AutoMechanicalModel,
        Confidence = 0.30m,
        SchemaFingerprint = new string('A', 64),
        Version = 1
    };

    private static SchemaInsight Authored(long rows) => new()
    {
        InsightId = 7,
        ObjectType = "Table",
        SchemaName = "dbo",
        ObjectName = "Orders",
        Description = "Orders placed by customers.",
        BusinessPurpose = "Sales pipeline.",
        DataPatterns = "Few rows.",
        UsageGuidelines = "Join on CustomerId.",
        RelatedObjects = "[\"[dbo].[Customers]\"]",
        LlmModel = "claude",
        Confidence = 0.85m,
        LastAnalyzed = new DateTime(2026, 9, 1),
        SchemaFingerprint = new string('B', 64),
        RowCountAtAnalysis = rows,
        Version = 2
    };
}
