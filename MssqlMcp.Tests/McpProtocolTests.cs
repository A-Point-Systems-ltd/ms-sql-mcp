// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.

using ModelContextProtocol.Client;
using Mssql.McpServer;

namespace MssqlMcp.Tests;

/// <summary>
/// End-to-end over stdio against the built server exe: pins the wire contract (tool names + annotations)
/// that an SDK upgrade can silently change. Skipped when the exe is not built or no SQL Server is reachable.
/// </summary>
public sealed class McpProtocolTests
{
    private static readonly string[] ReadOnlyTools =
    [
        ToolNames.ListObjects, ToolNames.DescribeTable, ToolNames.DescribeView, ToolNames.GetObject,
        ToolNames.ReadData, ToolNames.ScriptObject, ToolNames.GetServerInfo, ToolNames.GetInsight, ToolNames.ListInsights,
        ToolNames.GetInsightHistory, ToolNames.InsightsCheck, ToolNames.ListConnections,
    ];

    private static readonly string[] DestructiveTools =
        [ToolNames.ExecuteSql, ToolNames.UpdateData, ToolNames.DropTable, ToolNames.InstallInsightsLayer];

    [SkippableFact]
    public async Task Tools_list_exposes_exactly_the_pinned_names_and_annotations()
    {
        await using var client = await StartClientAsync();

        var tools = await client.ListToolsAsync();

        Assert.Equal(23, tools.Count);
        Assert.Equal(ToolNames.All.OrderBy(n => n, StringComparer.Ordinal), tools.Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal));
        foreach (var tool in tools)
        {
            var annotations = tool.ProtocolTool.Annotations;
            Assert.NotNull(annotations);
            Assert.True(ReadOnlyTools.Contains(tool.Name) == (annotations.ReadOnlyHint == true), $"{tool.Name} readOnlyHint={annotations.ReadOnlyHint}");
            if (annotations.ReadOnlyHint != true)
            {
                Assert.True(DestructiveTools.Contains(tool.Name) == (annotations.DestructiveHint == true), $"{tool.Name} destructiveHint={annotations.DestructiveHint}");
            }

            Assert.DoesNotContain("cancellationToken", tool.ProtocolTool.InputSchema.GetRawText(), StringComparison.OrdinalIgnoreCase);
        }
    }

    [SkippableFact]
    public async Task Script_object_returns_create_table_over_the_wire()
    {
        await using var client = await StartClientAsync();

        try
        {
            var created = await client.CallToolAsync(
                ToolNames.ExecuteSql,
                new Dictionary<string, object?> { ["sql"] = "CREATE TABLE dbo.mcp_protocol_probe (id int NOT NULL PRIMARY KEY)" });
            Assert.Contains("\"success\":true", Text(created), StringComparison.OrdinalIgnoreCase);

            var scripted = await client.CallToolAsync(
                ToolNames.ScriptObject,
                new Dictionary<string, object?> { ["objectType"] = "Table", ["name"] = "dbo.mcp_protocol_probe" });
            var text = Text(scripted);
            Assert.Contains("\"success\":true", text, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("CREATE TABLE", text, StringComparison.Ordinal);
        }
        finally
        {
            await client.CallToolAsync(
                ToolNames.DropTable,
                new Dictionary<string, object?> { ["sql"] = "DROP TABLE IF EXISTS dbo.mcp_protocol_probe" });
        }
    }

    [SkippableFact]
    public async Task Read_data_rejects_smuggled_statement_over_the_wire()
    {
        await using var client = await StartClientAsync();

        var result = await client.CallToolAsync(
            ToolNames.ReadData,
            new Dictionary<string, object?> { ["sql"] = "SELECT 1 AS a WAITFOR DELAY '00:00:05'" });

        var text = string.Concat(result.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>().Select(c => c.Text));
        Assert.Contains("\"success\":false", text, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task Multi_connection_requires_argument_routes_and_enforces_read_only()
    {
        await using var client = await StartClientAsync(multiConnection: true);

        var missing = await client.CallToolAsync(ToolNames.ReadData, new Dictionary<string, object?> { ["sql"] = "SELECT 1 AS x" });
        Assert.Contains("'connection' argument is required", Text(missing), StringComparison.Ordinal);

        var ok = await client.CallToolAsync(ToolNames.ReadData, new Dictionary<string, object?> { ["sql"] = "SELECT DB_NAME() AS db", ["connection"] = "ro" });
        Assert.Contains("\"success\":true", Text(ok), StringComparison.OrdinalIgnoreCase);

        var refused = await client.CallToolAsync(ToolNames.ExecuteSql, new Dictionary<string, object?> { ["sql"] = "CREATE TABLE dbo.never_created (id int)", ["connection"] = "ro" });
        Assert.Contains("read-only", Text(refused), StringComparison.OrdinalIgnoreCase);

        Assert.Contains("MUST pass the 'connection' argument", client.ServerInstructions ?? string.Empty, StringComparison.Ordinal);

        var tools = await client.ListToolsAsync();
        Assert.All(tools.Where(t => !ToolNames.ConnectionManagementTools.Contains(t.Name)), t =>
        {
            var schema = t.ProtocolTool.InputSchema.GetRawText();
            Assert.Contains("\"connection\"", schema, StringComparison.Ordinal);
            Assert.Contains("REQUIRED whenever the server has more than one connection", schema, StringComparison.Ordinal);
        });
    }

    [SkippableFact]
    public async Task Unknown_tool_in_multi_connection_mode_gets_unknown_tool_error_not_connection_error()
    {
        await using var client = await StartClientAsync(multiConnection: true);

        string text;
        try
        {
            text = Text(await client.CallToolAsync("no_such_tool", new Dictionary<string, object?>()));
        }
        catch (ModelContextProtocol.McpException ex)
        {
            text = ex.Message;
        }

        Assert.DoesNotContain("connection", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("no_such_tool", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// Two throwaway LocalDB databases, the real server over stdio, the Insights layer ON:
    /// concurrent calls land on the database they name, and a read-only profile writes nothing.
    /// </summary>
    [SkippableFact]
    public async Task Two_databases_route_by_name_and_read_only_profile_writes_nothing_with_insights_on()
    {
        await using var scratch = await ScratchDatabases.CreateAsync(2);
        var (csA, csB) = (scratch.ConnectionStrings[0], scratch.ConnectionStrings[1]);
        var connections = System.Text.Json.JsonSerializer.Serialize(new object[]
        {
            new { name = "a", connectionString = csA, insights = true },
            new { name = "b", connectionString = csB, insights = true },
            new { name = "ro", connectionString = csA, readOnly = true, insights = true },
        });

        await using var client = await StartClientAsync(connections, insights: true);

        // (1) Isolation: every concurrent call returns the database it named.
        var calls = Enumerable.Range(0, 20).SelectMany(_ => new[] { "a", "b" }).Select(async name =>
        {
            var r = await client.CallToolAsync(ToolNames.ReadData, new Dictionary<string, object?> { ["sql"] = "SELECT DB_NAME() AS db", ["connection"] = name });
            return (name, text: Text(r));
        });
        foreach (var (name, text) in await Task.WhenAll(calls))
        {
            var expected = name == "a" ? scratch.Names[0] : scratch.Names[1];
            var other = name == "a" ? scratch.Names[1] : scratch.Names[0];
            Assert.Contains(expected, text, StringComparison.Ordinal);
            Assert.DoesNotContain(other, text, StringComparison.Ordinal);
        }

        // (2) Zero writes: the layer is installed and populated through the writable profile only.
        var install = await client.CallToolAsync(ToolNames.InstallInsightsLayer, new Dictionary<string, object?> { ["connection"] = "a" });
        Assert.Contains("\"success\":true", Text(install), StringComparison.OrdinalIgnoreCase);
        await ScratchDatabases.ExecAsync(csA, "CREATE TABLE dbo.Orders (Id INT NOT NULL PRIMARY KEY, Amount DECIMAL(10,2) NULL);");
        await ScratchDatabases.ExecAsync(csA, "CREATE VIEW dbo.vOrders AS SELECT Id, Amount FROM dbo.Orders;");
        await ScratchDatabases.ExecAsync(csA, "CREATE PROCEDURE dbo.GetOrders AS SELECT Id FROM dbo.Orders;");
        var seeded = await client.CallToolAsync(ToolNames.DescribeTable, new Dictionary<string, object?> { ["name"] = "dbo.Orders", ["connection"] = "a" });
        Assert.Contains("\"success\":true", Text(seeded), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, await ScratchDatabases.ScalarAsync<int>(csA, "SELECT COUNT(*) FROM AIInsights.SchemaInsights WHERE ObjectName = N'Orders';"));

        // The table insight is now stale (archived on read before the fix); the view and procedure have
        // none (a baseline was inserted on read before the fix).
        await ScratchDatabases.ExecAsync(csA, "ALTER TABLE dbo.Orders ADD Note NVARCHAR(20) NULL;");
        var before = await CountInsightRowsAsync(csA);

        foreach (var (tool, args) in new (string Tool, Dictionary<string, object?> Args)[]
        {
            (ToolNames.DescribeTable, new() { ["name"] = "dbo.Orders" }),
            (ToolNames.DescribeView, new() { ["name"] = "dbo.vOrders" }),
            (ToolNames.GetObject, new() { ["objectType"] = "StoredProcedure", ["name"] = "dbo.GetOrders" }),
            (ToolNames.GetInsight, new() { ["objectName"] = "Orders", ["schemaName"] = "dbo" }),
            (ToolNames.GetInsight, new() { ["objectName"] = "vOrders", ["schemaName"] = "dbo", ["objectType"] = "View" }),
        })
        {
            args["connection"] = "ro";
            var r = await client.CallToolAsync(tool, args);
            Assert.True(Text(r).Contains("\"success\":true", StringComparison.OrdinalIgnoreCase), $"{tool}: {Text(r)}");
        }

        Assert.Equal(before, await CountInsightRowsAsync(csA));
    }

    private static async Task<(int Insights, int History, int Watermark)> CountInsightRowsAsync(string cs) => (
        await ScratchDatabases.ScalarAsync<int>(cs, "SELECT COUNT(*) FROM AIInsights.SchemaInsights;"),
        await ScratchDatabases.ScalarAsync<int>(cs, "SELECT COUNT(*) FROM AIInsights.InsightHistory;"),
        await ScratchDatabases.ScalarAsync<int>(cs, "SELECT ISNULL(MAX(LastProcessedAuditID), 0) FROM AIInsights.DdlChangeWatermark;"));

    private static string Text(ModelContextProtocol.Protocol.CallToolResult r) =>
        string.Concat(r.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>().Select(c => c.Text));

    private static Task<McpClient> StartClientAsync(bool multiConnection = false)
    {
        TestConnectionString.EnsureInitialized();
        var connections = multiConnection
            ? System.Text.Json.JsonSerializer.Serialize(new object[]
            {
                new { name = "main", connectionString = Environment.GetEnvironmentVariable("CONNECTION_STRING") },
                new { name = "ro", connectionString = Environment.GetEnvironmentVariable("CONNECTION_STRING"), readOnly = true },
            })
            : null;
        return StartClientAsync(connections, insights: false);
    }

    /// <param name="connectionsJson">MSSQL_CONNECTIONS value; null runs the single CONNECTION_STRING profile.</param>
    /// <param name="insights">Value of USE_INSIGHTS_LAYER for the server process.</param>
    private static async Task<McpClient> StartClientAsync(string? connectionsJson, bool insights)
    {
        TestConnectionString.EnsureInitialized();
        var exe = FindServerExe();
        Skip.If(exe is null, "MssqlMcp.exe not built.");

        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "mssql-mcp-under-test",
            Command = exe!,
            EnvironmentVariables = new Dictionary<string, string?>
            {
                ["CONNECTION_STRING"] = connectionsJson is null ? Environment.GetEnvironmentVariable("CONNECTION_STRING") : null,
                ["MSSQL_CONNECTIONS"] = connectionsJson,
                ["MSSQL_CONNECTIONS_FILE"] = null,
                ["USE_INSIGHTS_LAYER"] = insights ? "true" : "false",
                ["LOG_FILE_PATH"] = Path.Combine(Path.GetTempPath(), "MssqlMcpTests", "protocol.log"),
            },
        });

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            return await McpClient.CreateAsync(transport, cancellationToken: timeout.Token);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or OperationCanceledException)
        {
            // The server exits at startup when SQL Server is unreachable.
            throw new SkipException($"MCP server did not start (SQL Server unreachable?): {ex.Message}");
        }
    }

    private static string? FindServerExe()
    {
        // Tests run from MssqlMcp.Tests/bin/<Configuration>/net10.0/; the server builds next door.
        var testBin = new DirectoryInfo(AppContext.BaseDirectory);
        var configuration = testBin.Parent?.Name ?? "Debug";
        var repoRoot = testBin.Parent?.Parent?.Parent?.Parent?.FullName;
        if (repoRoot is null)
        {
            return null;
        }

        var serverBin = Path.Combine(repoRoot, "MssqlMcp", "bin", configuration, "net10.0");
        return new[] { Path.Combine(serverBin, "win-x64", "MssqlMcp.exe"), Path.Combine(serverBin, "MssqlMcp.exe") }
            .FirstOrDefault(File.Exists);
    }
}
