using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Mssql.McpServer.Connections;

/// <summary>
/// The single place where a tool call is bound to a connection: reads the 'connection' argument, applies the
/// "mandatory when more than one" rule and the read-only gate, and runs the tool inside a CurrentConnection scope.
/// </summary>
internal static class ConnectionRoutingFilter
{
    public const string ArgumentName = "connection";

    // Relaxed escaping keeps apostrophes in names readable for the LLM (default would emit 0027).
    private static readonly JsonSerializerOptions ErrorJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Extracts the 'connection' value. Absent or JSON null means "not given"; any other non-string kind is an error.</summary>
    public static string? ReadConnectionArgument(JsonElement? raw, out string? connectionArg)
    {
        connectionArg = null;
        switch (raw?.ValueKind)
        {
            case null:
            case JsonValueKind.Undefined:
            case JsonValueKind.Null:
                return null;
            case JsonValueKind.String:
                connectionArg = raw.Value.GetString();
                return null;
            default:
                return $"'{ArgumentName}' must be a string (a name from {ToolNames.ListConnections}).";
        }
    }

    public static string? Route(ConnectionRegistry registry, string toolName, string? connectionArg, out ConnectionProfile? profile)
    {
        profile = null;
        try
        {
            profile = registry.Resolve(connectionArg);
        }
        catch (ConnectionResolutionException ex)
        {
            return ex.Message;
        }

        if (profile.ReadOnly && ToolNames.WriteTools.Contains(toolName))
        {
            var name = profile.Name;
            profile = null;
            return $"Connection '{name}' is read-only; {toolName} is not allowed on it. Use a read/write connection or {ToolNames.ReadData} for queries.";
        }

        return null;
    }

    /// <summary>
    /// True for tools bound to a connection. Connection-management tools and names that are not tools at all pass
    /// straight through, so an unknown tool gets the SDK's unknown-tool error instead of a connection error.
    /// </summary>
    /// <param name="scriptRunnerEnabled">
    /// True when MSSQL_SCRIPT_RUNNER registered the extension-only tools; otherwise they are unknown tools and pass through.
    /// </param>
    public static bool IsRouted(string toolName, bool scriptRunnerEnabled = false) =>
        (ToolNames.All.Contains(toolName, StringComparer.Ordinal)
            || (scriptRunnerEnabled && ToolNames.ExtensionOnlyTools.Contains(toolName)))
        && !ToolNames.ConnectionManagementTools.Contains(toolName);

    public static McpRequestHandler<CallToolRequestParams, CallToolResult> Create(
        McpRequestHandler<CallToolRequestParams, CallToolResult> next, bool scriptRunnerEnabled) =>
        async (context, cancellationToken) =>
        {
            var toolName = context.Params?.Name ?? string.Empty;
            if (!IsRouted(toolName, scriptRunnerEnabled))
            {
                return await next(context, cancellationToken).ConfigureAwait(false);
            }

            var registry = context.Services!.GetRequiredService<ConnectionRegistry>();
            JsonElement? rawArg = context.Params?.Arguments is { } args && args.TryGetValue(ArgumentName, out var el) ? el : null;
            var error = ReadConnectionArgument(rawArg, out var connectionArg);
            ConnectionProfile? profile = null;
            error ??= Route(registry, toolName, connectionArg, out profile);
            if (error is not null)
            {
                return new CallToolResult
                {
                    IsError = true,
                    Content = [new TextContentBlock { Text = JsonSerializer.Serialize(new { success = false, error }, ErrorJson) }],
                };
            }

            using (CurrentConnection.Use(profile!))
            {
                return await next(context, cancellationToken).ConfigureAwait(false);
            }
        };
}
