namespace Mssql.McpServer.Connections;

public enum ConnectionSource
{
    Legacy,
    Configured,
    Adhoc,
}

/// <summary>A named SQL Server target. <see cref="ConnectionString"/> is secret: never return or log it unmasked.</summary>
public sealed record ConnectionProfile(
    string Name,
    string ConnectionString,
    bool ReadOnly,
    bool InsightsEnabled,
    ConnectionSource Source)
{
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append($"Name = {Name}, ReadOnly = {ReadOnly}, InsightsEnabled = {InsightsEnabled}, Source = {Source}, ConnectionString = {ConnectionStringMasker.Mask(ConnectionString)}");
        return true;
    }
}
