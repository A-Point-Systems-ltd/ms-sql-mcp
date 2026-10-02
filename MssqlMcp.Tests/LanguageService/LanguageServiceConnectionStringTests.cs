using Microsoft.Data.SqlClient;
using Mssql.McpServer.LanguageService;

namespace MssqlMcp.Tests.LanguageService;

/// <summary>The dedicated SMO session's connection string; no database needed.</summary>
public sealed class LanguageServiceConnectionStringTests
{
    [Theory]
    [InlineData("Server=srv;Database=db;User ID=u;Password=p;TrustServerCertificate=True")]
    [InlineData("Server=tcp:x.database.windows.net;Database=db;Authentication=Active Directory Default")]
    [InlineData("Server=srv;Database=db;Integrated Security=true;Persist Security Info=false;Pooling=true")]
    public void The_smo_session_keeps_credentials_and_is_unpooled_while_the_profile_string_is_unchanged(string profileString)
    {
        var profile = new Mssql.McpServer.Connections.ConnectionProfile("p", profileString, true, false, Mssql.McpServer.Connections.ConnectionSource.Configured);

        var smo = new SqlConnectionStringBuilder(LanguageServiceCache.MetadataConnectionString(profile));

        Assert.Equal(profileString, profile.ConnectionString);

        Assert.True(smo.PersistSecurityInfo);
        Assert.False(smo.Pooling);
        var source = new SqlConnectionStringBuilder(profileString);
        Assert.Equal(source.DataSource, smo.DataSource);
        Assert.Equal(source.InitialCatalog, smo.InitialCatalog);
        Assert.Equal(source.Password, smo.Password);
        Assert.Equal(source.Authentication, smo.Authentication);
    }
}
