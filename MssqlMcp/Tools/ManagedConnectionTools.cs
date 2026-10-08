using System.ComponentModel;
using ModelContextProtocol.Server;
using Mssql.McpServer.Connections.Managed;

namespace Mssql.McpServer;

/// <summary>
/// The connections view (MCP Apps) and its tools; registered only when MSSQL_MANAGED_CONNECTIONS_FILE is set.
/// <see cref="ToolNames.ManageConnections"/> is the only tool the model sees: it opens the view. The others are
/// app-only (<c>_meta.ui.visibility = ["app"]</c>), so only the person at the form can add hosts, change read-only
/// or send a password - the model cannot. Deliberately not an McpServerToolType class (WithToolsFromAssembly skips it).
/// </summary>
public sealed class ManagedConnectionTools(ManagedConnectionService service)
{
    public const string ResourceUri = "ui://apoint-ms-sql/connections";
    public const string EnvVar = "MSSQL_MANAGED_CONNECTIONS_FILE";

    private const string ModelMeta = """{"resourceUri":"ui://apoint-ms-sql/connections"}""";
    private const string AppOnlyMeta = """{"resourceUri":"ui://apoint-ms-sql/connections","visibility":["app"]}""";

    public static string? ConfiguredPath(Func<string, string?> getEnv) =>
        getEnv(EnvVar) is { } p && !string.IsNullOrWhiteSpace(p) ? Environment.ExpandEnvironmentVariables(p.Trim()) : null;

    [McpServerTool(Name = ToolNames.ManageConnections, Title = "Manage Connections", ReadOnly = true, Idempotent = true, Destructive = false),
        McpMeta("ui", JsonValue = ModelMeta),
        McpMeta("ui/resourceUri", ResourceUri), // legacy flat key read by older hosts
        Description("Opens the connection manager where the user can add, edit, test and remove this server's saved SQL Server connections. " +
                    "Call it when the user wants to add, change or delete a connection. You cannot change connections yourself; the user does it in the form. " +
                    "Returns the current connections (never credentials). After the user saves, call " + ToolNames.ListConnections + " to see the result.")]
    public object ManageConnections()
    {
        var list = service.List();
        return new
        {
            managed = list.Managed.Select(c => new { c.Name, c.Server, c.Database, c.Auth, c.ReadOnly, c.IsOpen, c.Error }),
            others = list.Others.Select(c => new { c.Name, c.Source, c.ReadOnly, editableHere = false }),
            list.FileError,
            note = "The user manages connections in the form shown with this result.",
        };
    }

    [McpServerTool(Name = ToolNames.ConnectionsUiList, Title = "Connections view: list", ReadOnly = true, Idempotent = true, Destructive = false),
        McpMeta("ui", JsonValue = AppOnlyMeta),
        Description("Connections view only: managed connections with their editable settings, plus read-only connections from other sources.")]
    public ManagedConnectionList List() => service.List();

    [McpServerTool(Name = ToolNames.ConnectionsUiSave, Title = "Connections view: save", ReadOnly = false, Idempotent = true, Destructive = false),
        McpMeta("ui", JsonValue = AppOnlyMeta),
        Description("Connections view only: adds (isNew=true) or updates a managed connection and applies it immediately.")]
    public ManagedSaveResult Save(
        [Description("The form values.")] ManagedConnectionInput connection,
        [Description("true to add, false to edit an existing managed connection.")] bool isNew) =>
        Guard(() => service.Save(connection, isNew));

    [McpServerTool(Name = ToolNames.ConnectionsUiRemove, Title = "Connections view: remove", ReadOnly = false, Idempotent = true, Destructive = true),
        McpMeta("ui", JsonValue = AppOnlyMeta),
        Description("Connections view only: removes a managed connection.")]
    public ManagedSaveResult Remove([Description("Managed connection name.")] string name) =>
        Guard(() => service.Remove(name ?? ""));

    [McpServerTool(Name = ToolNames.ConnectionsUiTest, Title = "Connections view: test", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = true),
        McpMeta("ui", JsonValue = AppOnlyMeta),
        Description("Connections view only: connects with the form values without saving.")]
    public Task<ManagedProbeResult> Test(
        [Description("The form values.")] ManagedConnectionInput connection,
        [Description("true when the form adds a connection; false when editing (an empty password uses the saved one).")] bool isNew,
        CancellationToken cancellationToken) =>
        service.TestAsync(connection, isNew, cancellationToken);

    [McpServerTool(Name = ToolNames.ConnectionsUiListDatabases, Title = "Connections view: list databases", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = true),
        McpMeta("ui", JsonValue = AppOnlyMeta),
        Description("Connections view only: lists every database of the form's server with its state (ONLINE, OFFLINE, ...).")]
    public Task<ManagedProbeResult> ListDatabases(
        [Description("The form values.")] ManagedConnectionInput connection,
        [Description("true when the form adds a connection; false when editing.")] bool isNew,
        CancellationToken cancellationToken) =>
        service.ListDatabasesAsync(connection, isNew, cancellationToken);

    // App-only and outside WriteTools on purpose: the person confirms it in the view; the model can never call it.
    [McpServerTool(Name = ToolNames.ConnectionsUiBringOnline, Title = "Connections view: bring database online", ReadOnly = false, Idempotent = true, Destructive = false, OpenWorld = true),
        McpMeta("ui", JsonValue = AppOnlyMeta),
        Description("Connections view only: ALTER DATABASE ... SET ONLINE for the form's database, only when it is OFFLINE, after the user confirmed it.")]
    public Task<ManagedProbeResult> BringOnline(
        [Description("The form values; database is the one to bring online.")] ManagedConnectionInput connection,
        [Description("true when the form adds a connection; false when editing.")] bool isNew,
        CancellationToken cancellationToken) =>
        service.BringOnlineAsync(connection, isNew, cancellationToken);

    private static ManagedSaveResult Guard(Func<ManagedSaveResult> action)
    {
        try
        {
            return action();
        }
        catch (Exception ex) when (ex is ManagedConnectionFileException or PlatformNotSupportedException)
        {
            return new ManagedSaveResult(false, new Dictionary<string, string>(), ex.Message);
        }
    }
}

/// <summary>Serves the connections view (built by apps/connections-ui, embedded in the exe).</summary>
public sealed class ManagedConnectionResources
{
    [McpServerResource(UriTemplate = ManagedConnectionTools.ResourceUri, Name = "apoint-ms-sql-connections", MimeType = AppViews.MimeType),
        Description("Connection manager view (MCP Apps).")]
    public static string Connections() => AppViews.Read("connections.html");
}
