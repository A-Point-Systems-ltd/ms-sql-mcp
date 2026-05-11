namespace Mssql.McpServer.InsightsLayer.Models;

/// <summary>
/// Indicates whether a cached schema insight is still aligned with the live object definition.
/// </summary>
public enum InsightFreshness
{
    LayerDisabled,
    Absent,
    Fresh,
    StaleArchived,
    AccessDenied,
    DefinitionUnavailable
}
