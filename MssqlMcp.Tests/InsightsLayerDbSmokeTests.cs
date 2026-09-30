// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.

using Microsoft.Extensions.Logging.Abstractions;
using Mssql.McpServer;
using Mssql.McpServer.InsightsLayer;
using Mssql.McpServer.InsightsLayer.Models;

namespace MssqlMcp.Tests;

/// <summary>
/// Optional live-database verification for the AI Insights layer.
/// Set environment variable <c>RUN_INSIGHTS_DB_TEST=1</c> and <c>CONNECTION_STRING</c> (e.g. from <c>sample_mcp.json</c>).
/// </summary>
public sealed class InsightsLayerDbSmokeTests
{
    private const string SmokeDescription = "MCP insights layer DB smoke test row (safe to delete).";

    [SkippableFact]
    public async Task Install_status_upsert_get_refresh_roundtrip()
    {
        Skip.IfNot(string.Equals(Environment.GetEnvironmentVariable("RUN_INSIGHTS_DB_TEST"), "1", StringComparison.OrdinalIgnoreCase));

        TestConnectionString.EnsureInitialized();
        Environment.SetEnvironmentVariable("USE_INSIGHTS_LAYER", "true");

        var factory = TestConnectionString.CreateFactory();
        var svc = new InsightsLayerService(factory, NullLogger<InsightsLayerService>.Instance);
        Assert.True(svc.IsEnabled);

        var (schema, tableName) = await TryGetFirstUserTableAsync(factory, CancellationToken.None).ConfigureAwait(false);
        Skip.If(schema is null || tableName is null, "No non-system table found in target database.");

        try
        {
            var install = await svc.InstallLayerAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.True(install.Success, install.Error ?? "InstallLayerAsync failed.");

            var status = await svc.GetStatusAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.True(status.LayerEnabledViaEnvironment);
            Assert.True(status.AiInsightsSchemaExists);
            Assert.True(status.DdlAuditTableExists);
            Assert.True(status.DdlAuditTriggerEnabled, "DDL_Audit trigger should exist and be enabled after install (requires DDL trigger permission).");

            var upsert = await svc.UpsertInsightAsync(
                new SchemaInsight
                {
                    InsightId = 0,
                    ObjectType = "Table",
                    SchemaName = schema,
                    ObjectName = tableName!,
                    ColumnName = null,
                    Description = SmokeDescription,
                    BusinessPurpose = null,
                    DataPatterns = null,
                    UsageGuidelines = null,
                    RelatedObjects = null,
                    LlmModel = "smoke-test",
                    Confidence = 0.5m,
                    LastAnalyzed = default,
                    AnalyzedBy = nameof(InsightsLayerDbSmokeTests),
                    Version = 1,
                    ModifyDateAtAnalysis = null,
                    ObjectIdAtAnalysis = null,
                    SchemaFingerprint = null,
                },
                CancellationToken.None).ConfigureAwait(false);
            Assert.True(upsert.Success, upsert.Error ?? "UpsertInsightAsync failed.");

            var (insight, freshness) = await svc.GetInsightForObjectAsync("Table", schema, tableName!, CancellationToken.None).ConfigureAwait(false);
            Assert.NotNull(insight);
            Assert.Equal(SmokeDescription, insight!.Description);
            Assert.True(
                freshness is InsightFreshness.Fresh or InsightFreshness.AccessDenied or InsightFreshness.DefinitionUnavailable,
                $"Unexpected freshness: {freshness}");

            await svc.ProcessDdlChangesAsync(CancellationToken.None).ConfigureAwait(false);

            var refresh = await svc.RefreshInsightsAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.True(refresh.Success, refresh.Error ?? "RefreshInsightsAsync failed.");
        }
        finally
        {
            await DeleteSmokeInsightRowsAsync(factory, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static async Task<(string? schema, string? table)> TryGetFirstUserTableAsync(
        ISqlConnectionFactory factory,
        CancellationToken cancellationToken)
    {
        await using var conn = await factory.GetOpenConnectionAsync().ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT TOP (1) SCHEMA_NAME(t.schema_id), t.name
            FROM sys.tables t
            WHERE t.is_ms_shipped = 0
              AND SCHEMA_NAME(t.schema_id) <> N'AIInsights'
            ORDER BY SCHEMA_NAME(t.schema_id), t.name;
            """;
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return (null, null);
        }

        return (reader.GetString(0), reader.GetString(1));
    }

    private static async Task DeleteSmokeInsightRowsAsync(ISqlConnectionFactory factory, CancellationToken cancellationToken)
    {
        await using var conn = await factory.GetOpenConnectionAsync().ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            IF OBJECT_ID(N'AIInsights.SchemaInsights', N'U') IS NOT NULL
                DELETE FROM AIInsights.SchemaInsights WHERE Description = @d;
            """;
        cmd.Parameters.AddWithValue("@d", SmokeDescription);
        _ = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
