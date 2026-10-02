namespace Mssql.McpServer.Connections;

/// <summary>Server-level guidance sent to the client in the MCP initialize response.</summary>
internal static class ServerInstructions
{
    public static string Build(ConnectionRegistry registry)
    {
        var text =
            "This server can hold several named SQL Server connections. Call list_connections first: it returns each connection's " +
            "name, server, database, whether it is read-only, and connectionRequired. When connectionRequired is true (more than one " +
            "connection), you MUST pass the 'connection' argument on every tool call; there is no default connection. Connections are " +
            "independent databases - never assume an object in one exists in another. Read-only connections reject all write tools. " +
            $"Current connections at startup: {registry.Count}.";
        if (registry.Count == 1)
        {
            text += " Only one connection exists now, so 'connection' may be omitted until more are opened.";
        }

        return InsightsLayer.InsightsLayerEnvironment.IsEnrichmentDirectiveEnabled
            ? text + " " + InsightEnrichmentRule
            : text;
    }

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
