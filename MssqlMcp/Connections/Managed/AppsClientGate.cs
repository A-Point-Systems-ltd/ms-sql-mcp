using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Mssql.McpServer.Connections.Managed;

/// <summary>
/// Server-side backstop for the connections view's app-only tools (<c>connections_ui_*</c>). Hiding them from the model
/// is the host's job (<c>_meta.ui.visibility = ["app"]</c>), so a client without MCP Apps support would list them to
/// its model - including save (which can make a connection writable) and bring online. This gate lists and runs them
/// only for clients that advertise the MCP Apps extension (<c>io.modelcontextprotocol/ui</c>) in their capabilities.
/// <c>MSSQL_APPS_REQUIRE_UI_CAPABILITY=false</c> turns the gate off for a host that supports MCP Apps without
/// advertising it; every refusal is logged at Warning so such a host is easy to diagnose.
/// </summary>
internal static class AppsClientGate
{
    /// <summary>The MCP Apps extension id (ext-apps <c>EXTENSION_ID</c>).</summary>
    public const string UiExtensionId = "io.modelcontextprotocol/ui";
    public const string EnableVariable = "MSSQL_APPS_REQUIRE_UI_CAPABILITY";

    /// <summary>The connections view's tools that only the view may call; manage_connections (model-visible) is not one.</summary>
    public static readonly IReadOnlySet<string> AppOnlyTools = new HashSet<string>(StringComparer.Ordinal)
    {
        ToolNames.ConnectionsUiList, ToolNames.ConnectionsUiSave, ToolNames.ConnectionsUiRemove, ToolNames.ConnectionsUiTest,
        ToolNames.ConnectionsUiListDatabases, ToolNames.ConnectionsUiBringOnline,
    };

    private static readonly JsonSerializerOptions ErrorJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>On unless set to false, 0, no, off or disabled (case-insensitive).</summary>
    public static bool IsRequired(Func<string, string?> getEnvironmentVariable) =>
        getEnvironmentVariable(EnableVariable)?.Trim().ToLowerInvariant() is not ("false" or "0" or "no" or "off" or "disabled");

    /// <summary>True when the client advertises the MCP Apps extension (under <c>extensions</c>, or <c>experimental</c> for older hosts).</summary>
    public static bool SupportsApps(ClientCapabilities? capabilities) =>
        capabilities?.Extensions?.ContainsKey(UiExtensionId) == true
        || capabilities?.Experimental?.ContainsKey(UiExtensionId) == true;

    public static McpRequestHandler<CallToolRequestParams, CallToolResult> CallFilter(
        McpRequestHandler<CallToolRequestParams, CallToolResult> next) =>
        async (context, cancellationToken) =>
        {
            var toolName = context.Params?.Name ?? string.Empty;
            if (!AppOnlyTools.Contains(toolName) || SupportsApps(context.Server.ClientCapabilities))
            {
                return await next(context, cancellationToken).ConfigureAwait(false);
            }

            context.Services?.GetService<ILoggerFactory>()?.CreateLogger(typeof(AppsClientGate).FullName!).LogWarning(
                "Refused {Tool}: the client does not advertise the MCP Apps extension ({Extension}). Set {Variable}=false if this host supports MCP Apps without advertising it.",
                toolName, UiExtensionId, EnableVariable);
            var error = $"{toolName} is only available to the connection manager view of an MCP Apps host. Ask the user to manage connections there.";
            return new CallToolResult
            {
                IsError = true,
                Content = [new TextContentBlock { Text = JsonSerializer.Serialize(new { success = false, error }, ErrorJson) }],
            };
        };

    public static McpRequestHandler<ListToolsRequestParams, ListToolsResult> ListFilter(
        McpRequestHandler<ListToolsRequestParams, ListToolsResult> next) =>
        async (context, cancellationToken) =>
        {
            var result = await next(context, cancellationToken).ConfigureAwait(false);
            if (!SupportsApps(context.Server.ClientCapabilities))
            {
                result.Tools = [.. result.Tools.Where(t => !AppOnlyTools.Contains(t.Name))];
                LogHiddenOnce(context.Server, context.Services);
            }

            return result;
        };

    // stdio serves one client per process, so "once per process" is once per session.
    private static int _hiddenLogged;

    /// <summary>
    /// A host that checks the view's calls against tools/list never forwards them, so the call filter's Warning would
    /// never appear: say here, once, which client was refused and what it advertised.
    /// </summary>
    private static void LogHiddenOnce(ModelContextProtocol.Server.McpServer server, IServiceProvider? services)
    {
        if (Interlocked.Exchange(ref _hiddenLogged, 1) == 1)
        {
            return;
        }

        var caps = server.ClientCapabilities;
        var advertised = (caps?.Extensions?.Keys ?? []).Select(k => "extensions." + k)
            .Concat((caps?.Experimental?.Keys ?? []).Select(k => "experimental." + k))
            .ToList();
        services?.GetService<ILoggerFactory>()?.CreateLogger(typeof(AppsClientGate).FullName!).LogWarning(
            "Connection-view tools hidden from client {Client} {Version}: it does not advertise {Extension} (advertised: {Advertised}). " +
            "If its connection manager form shows but its buttons fail, set {Variable}=false.",
            server.ClientInfo?.Name ?? "(unknown)", server.ClientInfo?.Version ?? "", UiExtensionId,
            advertised.Count > 0 ? string.Join(", ", advertised) : "none", EnableVariable);
    }
}
