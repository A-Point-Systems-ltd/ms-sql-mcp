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
        return registry.Count == 1
            ? text + " Only one connection exists now, so 'connection' may be omitted until more are opened."
            : text;
    }
}
