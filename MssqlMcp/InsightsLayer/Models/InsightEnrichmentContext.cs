namespace Mssql.McpServer.InsightsLayer.Models;

/// <summary>Why an introspection response asks the agent to call upsert_insight.</summary>
public enum InsightEnrichmentTrigger
{
    /// <summary>Only the auto-mechanical baseline exists; no authored insight was ever written.</summary>
    InitialBaselineOnly,

    /// <summary>An authored insight existed but the object's structure changed since it was written.</summary>
    StructureChanged,

    /// <summary>The authored insight was written while the object held fewer than 100 rows; it now holds 100 or more.</summary>
    DataPopulated
}

/// <summary>One DDL event recorded for the object since the previous authored insight.</summary>
public sealed record DdlEventSummary(string? EventType, DateTime PostTime, string? CommandText);

/// <summary>
/// Everything the agent needs to update an insight without re-investigating from scratch.
/// Null from <see cref="IInsightsLayerService.GetEnrichmentContextAsync"/> means no update is needed.
/// </summary>
public sealed record InsightEnrichmentContext(
    InsightEnrichmentTrigger Trigger,
    SchemaInsight? PreviousInsight,
    IReadOnlyList<DdlEventSummary> StructuralEvents,
    long? RowsNow);
