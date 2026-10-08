namespace MssqlMcp.Tests.Connections;

/// <summary>
/// Tests that take LocalDB databases OFFLINE and back ONLINE. Bringing a database online runs crash recovery, which
/// briefly blocks new logins and CREATE / DROP DATABASE server-wide; on a slow CI runner, parallel tests waiting on the
/// shared master connection pool then time out ("timeout ... obtaining a connection from the pool"). xUnit runs a
/// collection with DisableParallelization after the parallel ones, alone.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class DatabaseStateCollection
{
    public const string Name = "DatabaseStateChanges";
}
