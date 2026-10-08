namespace Mssql.McpServer.Connections;

/// <summary>Server-level guidance sent to the client in the MCP initialize response.</summary>
internal static class ServerInstructions
{
    /// <param name="toonByDefault">MSSQL_TOON: whether row results are TOON when a call does not pass toon.</param>
    public static string Build(ConnectionRegistry registry, bool toonByDefault = true)
    {
        var text =
            "This server can hold several named SQL Server connections. Call list_connections first: it returns each connection's " +
            "name, server, database, whether it is read-only, and connectionRequired. When connectionRequired is true (more than one " +
            "connection), you MUST pass the 'connection' argument on every tool call; there is no default connection. Connections are " +
            "independent databases - never assume an object in one exists in another. Read-only connections reject all write tools. " +
            $"Current connections at startup: {registry.Count}. " + (toonByDefault ? ToonNote : ToonOffNote);
        if (registry.Count == 1)
        {
            text += " Only one connection exists now, so 'connection' may be omitted until more are opened.";
        }

        return InsightsLayer.InsightsLayerEnvironment.IsEnrichmentDirectiveEnabled
            ? text + " " + InsightEnrichmentRule
            : text;
    }

    /// <summary>How to read TOON row results (read_data, list_objects, list_insights, get_insight_history).</summary>
    internal const string ToonNote =
        "Row results of " + ToolNames.ReadData + ", " + ToolNames.ListObjects + ", " + ToolNames.ListInsights + " and " +
        ToolNames.GetInsightHistory + " come as TOON by default: data[N]{col1,col2}: followed by N lines, one comma-separated " +
        "row each, in header order; quoted values are JSON-escaped strings. Pass toon=false to get JSON instead.";

    /// <summary>The same tools when MSSQL_TOON turned the default off.</summary>
    internal const string ToonOffNote =
        "Row results of " + ToolNames.ReadData + ", " + ToolNames.ListObjects + ", " + ToolNames.ListInsights + " and " +
        ToolNames.GetInsightHistory + " are JSON by default on this server; pass toon=true for compact TOON " +
        "(data[N]{col1,col2}: followed by N comma-separated rows).";

    /// <summary>
    /// The insight-update rule, stated once here instead of in every introspection tool description.
    /// </summary>
    internal const string InsightEnrichmentRule =
        "AI insights: " + ToolNames.DescribeTable + ", " + ToolNames.DescribeView + " and " + ToolNames.GetObject +
        " may return insight, enrichmentSuggested and insightEnrichment. When insightEnrichment is present, call " +
        ToolNames.UpsertInsight + " with insightEnrichment.nextAction.args before your final answer; when it carries " +
        "previousInsight, edit that text instead of re-investigating. When enrichmentSuggested is false the cached insight " +
        "is current: use it and do not call " + ToolNames.UpsertInsight + ". Never introspect other objects only to enrich them.";
}
