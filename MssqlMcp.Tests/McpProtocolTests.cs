// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.

using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
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
        Assert.DoesNotContain(tools, t => t.Name == ToolNames.RunScript);
        Assert.DoesNotContain(tools, t => t.Name == ToolNames.DdlHistory);
        Assert.DoesNotContain(tools, t => t.Name == ToolNames.LanguageService);
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

    /// <summary>Cursor shows every stderr line as an error, so a normal session must not write info-level logs there.</summary>
    [SkippableFact]
    public async Task A_normal_session_writes_no_info_lines_to_stderr()
    {
        var stderr = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var client = await StartClientAsync(null, insights: false, stderrLines: stderr.Enqueue);
        await using (client)
        {
            Assert.Equal(23, (await client.ListToolsAsync()).Count);
        }

        Assert.DoesNotContain(stderr, line => line.StartsWith("info:", StringComparison.Ordinal));
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
    public async Task Row_results_are_toon_by_default_and_json_with_toon_false()
    {
        await using var client = await StartClientAsync();
        const string sql = "SELECT TOP 3 name, database_id FROM sys.databases ORDER BY database_id";

        var toon = Text(await client.CallToolAsync(ToolNames.ReadData, new Dictionary<string, object?> { ["sql"] = sql }));
        Assert.StartsWith("success: true\ndata[3]{name,database_id}:\n  master,1\n", toon, StringComparison.Ordinal);

        var json = Text(await client.CallToolAsync(ToolNames.ReadData, new Dictionary<string, object?> { ["sql"] = sql, ["toon"] = false }));
        Assert.Contains("\"success\":true", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"name\":\"master\"", json, StringComparison.Ordinal);

        var tools = await client.ListToolsAsync();
        Assert.All(tools, t => Assert.Equal(
            ToolNames.ToonTools.Contains(t.Name),
            t.ProtocolTool.InputSchema.GetRawText().Contains("\"toon\"", StringComparison.Ordinal)));
        Assert.Contains("toon=false", client.ServerInstructions ?? string.Empty, StringComparison.Ordinal);

        // The default is the operator's (MSSQL_TOON), so the schema must not promise one.
        var toonSchema = tools.Single(t => t.Name == ToolNames.ReadData).ProtocolTool.InputSchema.GetProperty("properties").GetProperty("toon");
        Assert.False(toonSchema.TryGetProperty("default", out var d) && d.ValueKind == System.Text.Json.JsonValueKind.True, toonSchema.GetRawText());
    }

    [SkippableFact]
    public async Task Connection_view_tools_are_listed_and_callable_only_for_mcp_apps_clients()
    {
        var file = Path.Combine(Path.GetTempPath(), "MssqlMcpTests", $"managed-{Guid.NewGuid():N}.json");
        var apps = new ClientCapabilities
        {
            Extensions = new Dictionary<string, object> { [Mssql.McpServer.Connections.Managed.AppsClientGate.UiExtensionId] =
                System.Text.Json.JsonDocument.Parse("""{"mimeTypes":["text/html;profile=mcp-app"]}""").RootElement.Clone() },
        };

        await using (var plain = await StartClientAsync(null, insights: false, managedConnectionsFile: file))
        {
            var names = (await plain.ListToolsAsync()).Select(t => t.Name).ToList();
            Assert.Contains(ToolNames.ManageConnections, names);
            Assert.DoesNotContain(names, n => n.StartsWith("connections_ui_", StringComparison.Ordinal));

            var refused = await plain.CallToolAsync(ToolNames.ConnectionsUiBringOnline, new Dictionary<string, object?>());
            Assert.True(refused.IsError);
            Assert.Contains("only available to the connection manager view", Text(refused), StringComparison.Ordinal);
        }

        // The operator switch: with the gate off, a plain client sees and can call them (host-side visibility only).
        await using (var ungated = await StartClientAsync(null, insights: false, managedConnectionsFile: file, appsGate: false))
        {
            Assert.Contains(ToolNames.ConnectionsUiBringOnline, (await ungated.ListToolsAsync()).Select(t => t.Name));
            Assert.NotEqual(true, (await ungated.CallToolAsync(ToolNames.ConnectionsUiList, new Dictionary<string, object?>())).IsError);
        }

        await using var host = await StartClientAsync(null, insights: false, managedConnectionsFile: file, capabilities: apps);
        var hostNames = (await host.ListToolsAsync()).Select(t => t.Name).ToList();
        Assert.Contains(ToolNames.ConnectionsUiBringOnline, hostNames);
        Assert.Contains(ToolNames.ConnectionsUiSave, hostNames);
        var list = await host.CallToolAsync(ToolNames.ConnectionsUiList, new Dictionary<string, object?>());
        Assert.NotEqual(true, list.IsError);
    }

    [SkippableFact]
    public async Task Probe_tools_are_listed_only_with_the_probe_flag()
    {
        await using (var plain = await StartClientAsync())
        {
            Assert.DoesNotContain(await plain.ListToolsAsync(), t => ToolNames.ProbeOnlyTools.Contains(t.Name));
        }

        await using var probe = await StartClientAsync(null, insights: false, probeTools: true);
        var tools = await probe.ListToolsAsync();
        Assert.Equal(23 + ToolNames.ProbeOnlyTools.Count, tools.Count);

        var list = Text(await probe.CallToolAsync(ToolNames.ProbeListDatabases, new Dictionary<string, object?>()));
        Assert.Contains("\"name\":\"master\",\"state\":\"ONLINE\"", list, StringComparison.Ordinal);
        var state = Text(await probe.CallToolAsync(ToolNames.ProbeDatabaseState, new Dictionary<string, object?> { ["database"] = "master" }));
        Assert.Contains("\"state\":\"ONLINE\"", state, StringComparison.Ordinal);
        var refused = Text(await probe.CallToolAsync(ToolNames.ProbeBringOnline, new Dictionary<string, object?> { ["database"] = "master" }));
        Assert.Contains("not OFFLINE", refused, StringComparison.Ordinal);
        var test = Text(await probe.CallToolAsync(ToolNames.ProbeTest, new Dictionary<string, object?>()));
        Assert.Contains("\"ok\":true", test, StringComparison.Ordinal);
        Assert.Contains("\"state\":\"ONLINE\"", test, StringComparison.Ordinal);

        // The VS Code form falls back to its legacy Test when an older exe lacks probe_test; it detects that by this text.
        var unknown = await plain_unknown_tool_text(probe);
        Assert.Contains("unknown tool", unknown, StringComparison.OrdinalIgnoreCase);

        static async Task<string> plain_unknown_tool_text(McpClient c)
        {
            try
            {
                return Text(await c.CallToolAsync("probe_tool_that_does_not_exist", new Dictionary<string, object?>()));
            }
            catch (McpException ex)
            {
                return ex.Message;
            }
        }
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

    [SkippableFact]
    public async Task Run_script_without_its_env_var_gets_unknown_tool_error_in_multi_connection_mode()
    {
        await using var client = await StartClientAsync(multiConnection: true);

        string text;
        try
        {
            text = Text(await client.CallToolAsync(ToolNames.RunScript, new Dictionary<string, object?> { ["script"] = "SELECT 1" }));
        }
        catch (ModelContextProtocol.McpException ex)
        {
            text = ex.Message;
        }

        Assert.DoesNotContain("connection", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(ToolNames.RunScript, text, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task Ddl_history_without_the_runner_env_var_gets_unknown_tool_error_in_multi_connection_mode()
    {
        await using var client = await StartClientAsync(multiConnection: true);

        string text;
        try
        {
            text = Text(await client.CallToolAsync(ToolNames.DdlHistory, new Dictionary<string, object?> { ["action"] = "status" }));
        }
        catch (ModelContextProtocol.McpException ex)
        {
            text = ex.Message;
        }

        Assert.DoesNotContain("connection", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(ToolNames.DdlHistory, text, StringComparison.Ordinal);
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
        // install_insights_layer goes through the shared trigger install: the trigger runs as the loginless writer.
        Assert.Equal(
            "DDL_Audit_Writer",
            await ScratchDatabases.ScalarAsync<string>(csA, "SELECT USER_NAME(m.execute_as_principal_id) FROM sys.triggers t JOIN sys.sql_modules m ON m.object_id = t.object_id WHERE t.parent_class = 0 AND t.name = N'DDL_Audit'"));
        await ScratchDatabases.ExecAsync(csA, "CREATE TABLE dbo.Orders (Id INT NOT NULL PRIMARY KEY, Amount DECIMAL(10,2) NULL);");
        await ScratchDatabases.ExecAsync(csA, "CREATE VIEW dbo.vOrders AS SELECT Id, Amount FROM dbo.Orders;");
        await ScratchDatabases.ExecAsync(csA, "CREATE PROCEDURE dbo.GetOrders AS SELECT Id FROM dbo.Orders;");
        Assert.Equal(1, await ScratchDatabases.ScalarAsync<int>(csA, "SELECT COUNT(*) FROM dbo.DDL_AuditLog WHERE ObjectName = 'GetOrders'"));
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

    /// <summary>
    /// MSSQL_SCRIPT_RUNNER=true adds run_script, ddl_history and language_service (26 tools); run_script runs batches on a read/write profile and refuses writes
    /// on a read-only one. A scratch database stands in for CONNECTION_STRING so a regression cannot leave a table behind.
    /// </summary>
    [SkippableFact]
    public async Task Script_runner_env_var_adds_run_script_which_honours_read_only_profiles()
    {
        await using var scratch = await ScratchDatabases.CreateAsync(1);
        var cs = scratch.ConnectionStrings[0];
        await using var client = await StartClientAsync(TwoProfiles(cs), insights: false, scriptRunner: true);

        var tools = await client.ListToolsAsync();
        Assert.Equal(26, tools.Count);
        var runScript = Assert.Single(tools, t => t.Name == ToolNames.RunScript);
        Assert.True(runScript.ProtocolTool.Annotations?.DestructiveHint);
        Assert.Single(tools, t => t.Name == ToolNames.DdlHistory);

        var main = await client.CallToolAsync(
            ToolNames.RunScript,
            new Dictionary<string, object?> { ["script"] = "SELECT 1 AS n UNION ALL SELECT 2\nGO\nSELECT 3 AS m", ["connection"] = "main" });
        using (var doc = System.Text.Json.JsonDocument.Parse(Text(main)))
        {
            var root = doc.RootElement;
            Assert.True(root.GetProperty("success").GetBoolean(), Text(main));
            var data = root.GetProperty("data");
            Assert.False(data.GetProperty("hadErrors").GetBoolean());
            Assert.Equal(2, data.GetProperty("batches").GetInt32());
            var sets = data.GetProperty("resultSets");
            Assert.Equal(2, sets.GetArrayLength());
            Assert.Equal("[[1],[2]]", sets[0].GetProperty("rows").GetRawText());
            Assert.Equal("n", sets[0].GetProperty("columns")[0].GetProperty("name").GetString());
            Assert.Equal(2, sets[1].GetProperty("batch").GetInt32());
        }

        var ro = await client.CallToolAsync(
            ToolNames.RunScript,
            new Dictionary<string, object?> { ["script"] = "CREATE TABLE dbo.never_created (id int)", ["connection"] = "ro" });
        using (var doc = System.Text.Json.JsonDocument.Parse(Text(ro)))
        {
            var data = doc.RootElement.GetProperty("data");
            Assert.True(data.GetProperty("hadErrors").GetBoolean());
            Assert.Contains(
                data.GetProperty("messages").EnumerateArray(),
                m => m.GetProperty("kind").GetString() == "error"
                    && m.GetProperty("text").GetString()!.StartsWith("Read-only connection:", StringComparison.Ordinal));
        }

        Assert.Equal(1, await ScratchDatabases.ScalarAsync<int>(cs, "SELECT CASE WHEN OBJECT_ID(N'dbo.never_created') IS NULL THEN 1 ELSE 0 END"));

        var status = await client.CallToolAsync(
            ToolNames.DdlHistory,
            new Dictionary<string, object?> { ["action"] = "status", ["connection"] = "ro" });
        using (var doc = System.Text.Json.JsonDocument.Parse(Text(status)))
        {
            var root = doc.RootElement;
            Assert.True(root.GetProperty("success").GetBoolean(), Text(status));
            var data = root.GetProperty("data");
            Assert.False(data.GetProperty("tableExists").GetBoolean());
            Assert.False(data.GetProperty("tableCompatible").GetBoolean());
            Assert.False(data.GetProperty("triggerExists").GetBoolean());
            Assert.False(data.GetProperty("triggerEnabled").GetBoolean());
            Assert.False(data.GetProperty("canInstall").GetBoolean());
            Assert.False(string.IsNullOrWhiteSpace(data.GetProperty("serverName").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(data.GetProperty("databaseName").GetString()));
            Assert.False(data.TryGetProperty("warnings", out _));
        }
    }

    /// <summary>
    /// language_service over stdio, on a read-only profile: SqlParser and SMO load inside the server exe and a completion
    /// returns the scratch table's columns. Set MSSQL_MCP_TEST_EXE to run this against a single-file publish.
    /// </summary>
    [SkippableFact]
    public async Task Language_service_completion_works_through_the_server_exe()
    {
        await using var scratch = await ScratchDatabases.CreateAsync(1);
        var cs = scratch.ConnectionStrings[0];
        await ScratchDatabases.ExecAsync(cs, "CREATE TABLE dbo.T (a int, b nvarchar(10));");
        await using var client = await StartClientAsync(TwoProfiles(cs), insights: false, scriptRunner: true);

        var tool = Assert.Single(await client.ListToolsAsync(), t => t.Name == ToolNames.LanguageService);
        Assert.True(tool.ProtocolTool.Annotations?.ReadOnlyHint);
        Assert.DoesNotContain("cache", tool.ProtocolTool.InputSchema.GetRawText(), StringComparison.OrdinalIgnoreCase);

        // The first call may hit the 2 s binding timeout on a cold cache; warm, then poll until the cache is ready.
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (true)
        {
            var result = await client.CallToolAsync(
                ToolNames.LanguageService,
                new Dictionary<string, object?> { ["action"] = "completion", ["text"] = "SELECT t. FROM dbo.T t", ["line"] = 1, ["column"] = 10, ["connection"] = "ro" });
            using var doc = System.Text.Json.JsonDocument.Parse(Text(result));
            var root = doc.RootElement;
            Assert.True(root.GetProperty("success").GetBoolean(), Text(result));
            var data = root.GetProperty("data");
            if (data.GetProperty("cacheState").GetString() == "warm")
            {
                var items = data.GetProperty("items").EnumerateArray().ToList();
                Assert.Contains(items, i => i.GetProperty("label").GetString() == "a" && i.GetProperty("kind").GetString() == "column");
                Assert.Contains(items, i => i.GetProperty("label").GetString() == "b");
                Assert.False(data.GetProperty("isIncomplete").GetBoolean());
                break;
            }

            Assert.True(DateTime.UtcNow < deadline, "language_service never became warm: " + Text(result));
            await Task.Delay(250);
        }
    }

    /// <summary>
    /// notifications/cancelled for a running run_script call must stop the script on the server: the open transaction
    /// is rolled back and its locks are released while the server process keeps running. The cancel is sent the way the
    /// extension's runner client sends it (mcpStdioClient.ts <c>abandon</c>): cancel the call locally, then send the
    /// notification for its request id. The SDK 2.2.0 client only does the first part: cancelling the CallToolAsync token
    /// ends the call locally but sends no notification (its trace log shows none), so the test sends it explicitly.
    /// </summary>
    [SkippableTheory]
    [InlineData("2024-11-05", true)] // the extension's runner client: mcpStdioClient.ts PROTOCOL_VERSION, integer ids
    [InlineData("2024-11-05", false)]
    [InlineData(null, false)] // the SDK's default revision
    public async Task Cancelling_a_run_script_call_rolls_back_on_the_server_and_releases_locks(string? protocolVersion, bool integerId)
    {
        await using var scratch = await ScratchDatabases.CreateAsync(1);
        var cs = scratch.ConnectionStrings[0];
        await ScratchDatabases.ExecAsync(cs, "CREATE TABLE dbo.T (i int)");
        var connections = System.Text.Json.JsonSerializer.Serialize(new object[] { new { name = "main", connectionString = cs } });
        await using var client = await StartClientAsync(connections, insights: false, scriptRunner: true, protocolVersion);

        // Far above the SDK's own sequential ids (initialize / discover), so it cannot collide with them.
        var requestId = integerId ? new RequestId(9001L) : new RequestId("run-script-cancel-probe");
        using var cts = new CancellationTokenSource();
        var call = client.SendRequestAsync(
            new JsonRpcRequest
            {
                Id = requestId,
                Method = RequestMethods.ToolsCall,
                Params = new System.Text.Json.Nodes.JsonObject
                {
                    ["name"] = ToolNames.RunScript,
                    ["arguments"] = new System.Text.Json.Nodes.JsonObject
                    {
                        ["script"] = "BEGIN TRAN; INSERT dbo.T VALUES (1); WAITFOR DELAY '00:00:30'; COMMIT",
                        ["connection"] = "main",
                    },
                },
            },
            cts.Token);

        // Cancel only once the server holds the uncommitted row (visible to a dirty read).
        await WaitUntilAsync(
            async () => await ScratchDatabases.ScalarAsync<int>(cs, "SELECT COUNT(*) FROM dbo.T WITH (NOLOCK)") == 1,
            TimeSpan.FromSeconds(15),
            "the script never inserted its row");
        Assert.False(call.IsCompleted);

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
        await client.SendNotificationAsync(
            NotificationMethods.CancelledNotification,
            new CancelledNotificationParams { RequestId = requestId, Reason = "Cancelled by the user." });

        // On a separate connection, within 10 s: the row is gone and the table can be read without waiting on a lock.
        int? count = null;
        await WaitUntilAsync(
            async () =>
            {
                try
                {
                    count = await ScratchDatabases.ScalarAsync<int>(cs, "SET LOCK_TIMEOUT 2000; SELECT COUNT(*) FROM dbo.T");
                    return true;
                }
                catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 1222)
                {
                    return false;
                }
            },
            TimeSpan.FromSeconds(10),
            "dbo.T stayed locked after the cancel");
        Assert.Equal(0, count);

        // The server process is still serving: the cancel ended the call, not the runner.
        var after = await client.CallToolAsync(ToolNames.RunScript, new Dictionary<string, object?> { ["script"] = "SELECT 1 AS alive", ["connection"] = "main" });
        Assert.Contains("\"success\":true", Text(after), StringComparison.OrdinalIgnoreCase);
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout, string failure)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow < deadline, failure);
            await Task.Delay(100);
        }
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
        var connections = multiConnection ? TwoProfiles(Environment.GetEnvironmentVariable("CONNECTION_STRING")) : null;
        return StartClientAsync(connections, insights: false);
    }

    /// <summary>The two-profile config: 'main' (read/write) and 'ro' (read-only) on the same database.</summary>
    private static string TwoProfiles(string? cs) => System.Text.Json.JsonSerializer.Serialize(new object[]
    {
        new { name = "main", connectionString = cs },
        new { name = "ro", connectionString = cs, readOnly = true },
    });

    /// <param name="connectionsJson">MSSQL_CONNECTIONS value; null runs the single CONNECTION_STRING profile.</param>
    /// <param name="insights">Value of USE_INSIGHTS_LAYER for the server process.</param>
    /// <param name="scriptRunner">True sets MSSQL_SCRIPT_RUNNER=true (registers run_script); false removes it.</param>
    /// <param name="probeTools">True sets MSSQL_PROBE_TOOLS=true (registers the connection-form probe tools); false removes it.</param>
    /// <param name="protocolVersion">MCP revision the client requests; null keeps the SDK default.</param>
    /// <param name="stderrLines">Receives each line the server writes to stderr; null discards them.</param>
    private static async Task<McpClient> StartClientAsync(
        string? connectionsJson, bool insights, bool scriptRunner = false, string? protocolVersion = null, Action<string>? stderrLines = null,
        bool probeTools = false, string? managedConnectionsFile = null, ClientCapabilities? capabilities = null, bool appsGate = true)
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
                ["MSSQL_SCRIPT_RUNNER"] = scriptRunner ? "true" : null,
                ["MSSQL_PROBE_TOOLS"] = probeTools ? "true" : null,
                ["MSSQL_MANAGED_CONNECTIONS_FILE"] = managedConnectionsFile,
                ["MSSQL_APPS_REQUIRE_UI_CAPABILITY"] = appsGate ? null : "false",
                ["MSSQL_CONSOLE_LOG_LEVEL"] = null,
                ["LOG_FILE_PATH"] = Path.Combine(Path.GetTempPath(), "MssqlMcpTests", "protocol.log"),
            },
            StandardErrorLines = stderrLines,
        });

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            return await McpClient.CreateAsync(transport, new McpClientOptions { ProtocolVersion = protocolVersion, Capabilities = capabilities }, cancellationToken: timeout.Token);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or OperationCanceledException)
        {
            // The server exits at startup when SQL Server is unreachable.
            throw new SkipException($"MCP server did not start (SQL Server unreachable?): {ex.Message}");
        }
    }

    private static string? FindServerExe()
    {
        // Lets a run target a published single-file exe instead of the build output.
        var overridePath = Environment.GetEnvironmentVariable("MSSQL_MCP_TEST_EXE");
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            return File.Exists(overridePath) ? overridePath : null;
        }

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
