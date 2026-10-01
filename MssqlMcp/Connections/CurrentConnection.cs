namespace Mssql.McpServer.Connections;

/// <summary>
/// The connection bound to the executing tool call. Set by the connection routing filter (tool calls)
/// and by the Insights queue (background work); read by <see cref="SqlConnectionFactory"/>.
/// </summary>
public static class CurrentConnection
{
    private static readonly AsyncLocal<ConnectionProfile?> Current = new();

    public static ConnectionProfile? Value => Current.Value;

    public static IDisposable Use(ConnectionProfile profile)
    {
        var previous = Current.Value;
        Current.Value = profile;
        return new Scope(previous);
    }

    private sealed class Scope(ConnectionProfile? previous) : IDisposable
    {
        public void Dispose() => Current.Value = previous;
    }
}
