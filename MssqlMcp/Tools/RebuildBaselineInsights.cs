// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.

using System.ComponentModel;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using Mssql.McpServer.InsightsLayer;
using Mssql.McpServer.InsightsLayer.Models;

namespace Mssql.McpServer;

public partial class Tools
{
    [McpServerTool(
        Name = ToolNames.RebuildBaselineInsights,
        Title = "Rebuild Baseline Insights",
        ReadOnly = false,
        Idempotent = true,
        Destructive = false),
        Description("Bulk warms AIInsights baselines for existing objects. Scans sys.objects by optional schema/objectType filters and ensures each object has an insight row: an earlier authored insight for the same structure is restored, otherwise an auto-mechanical baseline is created. Baselines are enriched later, when " + ToolNames.DescribeTable + "/" + ToolNames.DescribeView + "/" + ToolNames.GetObject + " returns insightEnrichment for an object the agent actually works on." + MultiConnectionNote)]
    public async Task<DbOperationResult> RebuildBaselineInsights(
        [Description("Optional schema filter. Pass null for all schemas.")] string? schemaName = null,
        [Description("Optional object type filter: 'Table' | 'View' | 'Procedure' | 'Function'. Pass null for all supported types. Triggers are not covered by baseline scans.")] string? objectType = null,
        [Description("Maximum objects to scan (1..2000).")] int take = 200,
        [Description(ConnectionParamDescription)] string? connection = null,
        CancellationToken cancellationToken = default)
    {
        if (!_insightsLayer.IsEnabled || !InsightsLayerEnvironment.IsAutoPopulationEnabled)
        {
            return new DbOperationResult(success: false, error: "Insights auto-population is disabled.");
        }

        take = Math.Clamp(take, 1, 2000);
        var wantedSchema = string.IsNullOrWhiteSpace(schemaName) ? null : schemaName.Trim();
        string? wantedType = null;
        if (!string.IsNullOrWhiteSpace(objectType))
        {
            wantedType = NormalizeBaselineObjectType(objectType);
            if (wantedType is null)
            {
                return new DbOperationResult(
                    success: false,
                    error: "Unsupported objectType. Use one of: Table, View, Procedure, Function.");
            }
        }

        var scanned = 0;
        var created = 0;
        var skipped = 0;
        var errors = new List<string>();

        const string sql = """
            SELECT TOP (@Take)
                CASE
                    WHEN o.type = 'U' THEN N'Table'
                    WHEN o.type = 'V' THEN N'View'
                    WHEN o.type = 'P' THEN N'Procedure'
                    WHEN o.type IN ('FN','IF','TF','FT') THEN N'Function'
                    ELSE NULL
                END AS ObjectType,
                s.name AS SchemaName,
                o.name AS ObjectName
            FROM sys.objects o
            INNER JOIN sys.schemas s ON o.schema_id = s.schema_id
            WHERE o.is_ms_shipped = 0
              AND o.type IN ('U','V','P','FN','IF','TF','FT')
              AND (@SchemaName IS NULL OR s.name = @SchemaName)
              AND (@ObjectType IS NULL OR @ObjectType = CASE
                    WHEN o.type = 'U' THEN N'Table'
                    WHEN o.type = 'V' THEN N'View'
                    WHEN o.type = 'P' THEN N'Procedure'
                    WHEN o.type IN ('FN','IF','TF','FT') THEN N'Function'
                  END)
            ORDER BY s.name, o.name;
            """;

        try
        {
            await using var conn = await _connectionFactory.GetOpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            {
                var targets = new List<(string Type, string Schema, string Name)>();
                await using (var cmd = new SqlCommand(sql, conn))
                {
                    cmd.Parameters.AddWithValue("@Take", take);
                    cmd.Parameters.AddWithValue("@SchemaName", wantedSchema is null ? DBNull.Value : wantedSchema);
                    cmd.Parameters.AddWithValue("@ObjectType", wantedType is null ? DBNull.Value : wantedType);
                    await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        if (reader.IsDBNull(0))
                        {
                            continue;
                        }

                        targets.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
                    }
                }

                foreach (var target in targets)
                {
                    scanned++;
                    try
                    {
                        var (before, beforeFreshness) = await _insightsLayer
                            .GetInsightForObjectAsync(target.Type, target.Schema, target.Name, cancellationToken)
                            .ConfigureAwait(false);
                        var hadBefore = before is not null || beforeFreshness is InsightFreshness.AccessDenied or InsightFreshness.DefinitionUnavailable;

                        var (_, afterFreshness) = await _insightsLayer
                            .EnsureBaselineForObjectAsync(target.Type, target.Schema, target.Name, cancellationToken)
                            .ConfigureAwait(false);
                        if (!hadBefore && afterFreshness != InsightFreshness.Absent)
                        {
                            created++;
                        }
                        else
                        {
                            skipped++;
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        errors.Add($"{target.Type} {target.Schema}.{target.Name}: {ex.Message}");
                    }
                }
            }

            return new DbOperationResult(success: true, data: new { scanned, created, skipped, errors });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "{Tool} failed: {Message}", ToolNames.RebuildBaselineInsights, ex.Message);
            return new DbOperationResult(success: false, error: ex.Message);
        }
    }

    /// <summary>
    /// Maps a caller-supplied object type to the canonical label produced by the baseline scan query,
    /// or null when the type is not covered by baseline scans.
    /// </summary>
    internal static string? NormalizeBaselineObjectType(string objectType)
    {
        var compact = objectType
            .Trim()
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .ToLowerInvariant();

        return compact switch
        {
            "table" or "tables" => "Table",
            "view" or "views" => "View",
            "procedure" or "procedures" or "proc" or "procs" or "storedprocedure" or "storedprocedures" => "Procedure",
            "function" or "functions" or "scalarfunction" or "tablefunction" or "tvf" => "Function",
            _ => null
        };
    }
}
