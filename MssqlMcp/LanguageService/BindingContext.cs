// Ported from microsoft/sqltoolsservice (MIT), adapted to this server's connection types:
// https://github.com/microsoft/sqltoolsservice/blob/02de444481bccd4492cd4e35e44fb97738f3a770/src/Microsoft.SqlTools.LanguageService/LanguageServices/ConnectedBindingContext.cs
// https://github.com/microsoft/sqltoolsservice/blob/02de444481bccd4492cd4e35e44fb97738f3a770/src/Microsoft.SqlTools.LanguageService/LanguageServices/ConnectedBindingQueue.cs
// (ConnectedBindingContext parse options and compat level; ConnectedBindingQueue.PopulateConnectionContext.)
//
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.
//
// MIT License
//
// Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated
// documentation files (the "Software"), to deal in the Software without restriction, including without limitation
// the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to
// permit persons to whom the Software is furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all copies or substantial portions of
// the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE
// WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR
// COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
// OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

using Microsoft.Data.SqlClient;
using Microsoft.SqlServer.Management.Common;
using Microsoft.SqlServer.Management.SmoMetadataProvider;
using Microsoft.SqlServer.Management.SqlParser.Binder;
using Microsoft.SqlServer.Management.SqlParser.Common;
using Microsoft.SqlServer.Management.SqlParser.MetadataProvider;
using Microsoft.SqlServer.Management.SqlParser.Parser;
using SMO = Microsoft.SqlServer.Management.Smo;

namespace Mssql.McpServer.LanguageService;

/// <summary>
/// One connected SqlParser binding context: an SMO metadata provider and binder over a dedicated connection to one
/// database. Not thread-safe: callers serialise every use through the owning cache entry's lock. It only reads metadata.
/// </summary>
internal sealed class BindingContext : IDisposable
{
    private const string DefaultBatchSeparator = "GO";

    private readonly SqlConnection _connection;
    private readonly ServerConnection _serverConnection;

    private BindingContext(SqlConnection connection)
    {
        _connection = connection;
        DatabaseName = connection.Database;
        _serverConnection = new ServerConnection(connection);
        MetadataProvider = SmoMetadataProvider.CreateConnectedProvider(_serverConnection);
        DisplayInfoProvider = new MetadataDisplayInfoProvider { BuiltInCasing = CasingStyle.Uppercase };
        Binder = BinderProvider.CreateBinder(MetadataProvider);

        var server = MetadataProvider.SmoServer;
        ParseOptions = new ParseOptions(
            batchSeparator: DefaultBatchSeparator,
            isQuotedIdentifierSet: true,
            compatibilityLevel: GetDatabaseCompatibilityLevel(server),
            transactSqlVersion: GetTransactSqlVersion(server));
    }

    public string DatabaseName { get; }

    public SmoMetadataProvider MetadataProvider { get; }

    public MetadataDisplayInfoProvider DisplayInfoProvider { get; }

    public IBinder Binder { get; }

    public ParseOptions ParseOptions { get; }

    /// <summary>Builds the context over an open connection it then owns. Synchronous and slow (SMO queries): run it off the caller's thread.</summary>
    public static BindingContext Create(SqlConnection connection)
    {
        try
        {
            return new BindingContext(connection);
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    /// <summary>Parses <paramref name="text"/> and binds it against this context's database.</summary>
    public ParseResult ParseAndBind(string text)
    {
        var result = Parser.Parse(text, ParseOptions);
        Binder.Bind([result], DatabaseName, BindMode.Batch);
        return result;
    }

    private int _parserFaultLogged;

    /// <summary>True the first time it is called for this context: a known SqlParser fault is logged once per entry, not per keystroke.</summary>
    public bool FirstParserFault() => Interlocked.Exchange(ref _parserFaultLogged, 1) == 0;

    public void Dispose()
    {
        try
        {
            _serverConnection.Disconnect();
        }
        catch (Exception)
        {
            // The connection may already be broken; disposing it below is what matters.
        }

        _connection.Dispose();
    }

    private static DatabaseCompatibilityLevel GetDatabaseCompatibilityLevel(SMO.Server server)
    {
        if (server.DatabaseEngineType == DatabaseEngineType.SqlAzureDatabase)
        {
            return DatabaseCompatibilityLevel.Azure;
        }

        // SMO and SqlParser name their levels the same way (Version80 .. Version170); unknown ones map to Current.
        return Enum.TryParse<DatabaseCompatibilityLevel>(GetServerCompatibilityLevel(server).ToString(), out var level)
            ? level
            : DatabaseCompatibilityLevel.Current;
    }

    private static TransactSqlVersion GetTransactSqlVersion(SMO.Server server)
    {
        if (server.DatabaseEngineType == DatabaseEngineType.SqlAzureDatabase)
        {
            return TransactSqlVersion.Azure;
        }

        // Some engines (such as MI) support a higher language version than their engine version, so take the higher of
        // the server version and the compat level.
        var compatLevel = Math.Max(server.VersionMajor * 10, (int)GetServerCompatibilityLevel(server));
        if (compatLevel is 90 or 100)
        {
            // 10.0 uses 10.5, the closest available.
            return TransactSqlVersion.Version105;
        }

        return Enum.TryParse<TransactSqlVersion>($"Version{compatLevel}", out var version) ? version : TransactSqlVersion.Current;
    }

    /// <summary>
    /// The highest compat level of master (the instance's highest) and the connection's database. Adapted: one direct
    /// catalog query instead of SMO's Databases collection, which enumerates every database on the instance.
    /// </summary>
    private static SMO.CompatibilityLevel GetServerCompatibilityLevel(SMO.Server server)
    {
        try
        {
            var level = server.ConnectionContext.ExecuteScalar(
                "SELECT MAX(compatibility_level) FROM sys.databases WHERE name IN (N'master', DB_NAME());");
            if (level is not null and not DBNull)
            {
                return (SMO.CompatibilityLevel)Convert.ToInt32(level, System.Globalization.CultureInfo.InvariantCulture);
            }
        }
        catch (Exception)
        {
            // Fall through to the highest known level, like the original.
        }

        return Enum.GetValues<SMO.CompatibilityLevel>().Max();
    }
}
