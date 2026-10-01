using Microsoft.Data.SqlClient;

namespace Mssql.McpServer.Connections;

/// <summary>
/// Masks secrets in a connection string for logging and tool output. Parses with
/// <see cref="SqlConnectionStringBuilder"/> so quoted values containing ';' cannot leak;
/// unparsable strings are never echoed.
/// </summary>
internal static class ConnectionStringMasker
{
    public static string Mask(string connectionString)
    {
        if (string.IsNullOrEmpty(connectionString))
        {
            return string.Empty;
        }

        try
        {
            var builder = new SqlConnectionStringBuilder(connectionString);
            if (!string.IsNullOrEmpty(builder.Password))
            {
                builder.Password = "***MASKED***";
            }

            return builder.ConnectionString;
        }
        catch (Exception)
        {
            return "<unparsable connection string - not logged>";
        }
    }
}
