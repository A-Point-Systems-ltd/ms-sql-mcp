using ModelContextProtocol.Protocol;
using Mssql.McpServer;
using Mssql.McpServer.Connections.Managed;

namespace MssqlMcp.Tests.Connections.Managed;

public sealed class AppsClientGateTests
{
    [Fact]
    public void Supports_apps_only_when_the_ui_extension_is_advertised()
    {
        Assert.False(AppsClientGate.SupportsApps(null));
        Assert.False(AppsClientGate.SupportsApps(new ClientCapabilities()));
        Assert.False(AppsClientGate.SupportsApps(new ClientCapabilities { Extensions = new Dictionary<string, object> { ["io.modelcontextprotocol/other"] = new { } } }));
        Assert.True(AppsClientGate.SupportsApps(new ClientCapabilities { Extensions = new Dictionary<string, object> { [AppsClientGate.UiExtensionId] = new { } } }));
        // Older hosts announced extensions under experimental.
        Assert.True(AppsClientGate.SupportsApps(new ClientCapabilities { Experimental = new Dictionary<string, object> { [AppsClientGate.UiExtensionId] = new { } } }));
        Assert.False(AppsClientGate.SupportsApps(new ClientCapabilities { Experimental = new Dictionary<string, object> { ["other"] = new { } } }));
    }

    [Fact]
    public void Gate_is_on_unless_switched_off()
    {
        Assert.True(AppsClientGate.IsRequired(_ => null));
        Assert.True(AppsClientGate.IsRequired(_ => "true"));
        foreach (var off in new[] { "false", "0", "OFF", " no ", "disabled" })
        {
            Assert.False(AppsClientGate.IsRequired(_ => off), off);
        }
    }

    [Fact]
    public void Every_app_only_tool_is_gated_and_the_model_facing_one_is_not()
    {
        Assert.Contains(ToolNames.ConnectionsUiBringOnline, AppsClientGate.AppOnlyTools);
        Assert.Contains(ToolNames.ConnectionsUiSave, AppsClientGate.AppOnlyTools);
        Assert.Equal(6, AppsClientGate.AppOnlyTools.Count);
        Assert.DoesNotContain(ToolNames.ManageConnections, AppsClientGate.AppOnlyTools);
    }
}
