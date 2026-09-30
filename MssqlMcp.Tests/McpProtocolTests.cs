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
        ToolNames.ReadData, ToolNames.GetServerInfo, ToolNames.GetInsight, ToolNames.ListInsights,
        ToolNames.GetInsightHistory, ToolNames.InsightsCheck,
    ];

    private static readonly string[] DestructiveTools =
        [ToolNames.ExecuteSql, ToolNames.UpdateData, ToolNames.DropTable, ToolNames.InstallInsightsLayer];

    [SkippableFact]
    public async Task Tools_list_exposes_exactly_the_pinned_names_and_annotations()
    {
        await using var client = await StartClientAsync();

        var tools = await client.ListToolsAsync();

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
    public async Task Read_data_rejects_smuggled_statement_over_the_wire()
    {
        await using var client = await StartClientAsync();

        var result = await client.CallToolAsync(
            ToolNames.ReadData,
            new Dictionary<string, object?> { ["sql"] = "SELECT 1 AS a WAITFOR DELAY '00:00:05'" });

        var text = string.Concat(result.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>().Select(c => c.Text));
        Assert.Contains("\"success\":false", text, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<McpClient> StartClientAsync()
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
                ["CONNECTION_STRING"] = Environment.GetEnvironmentVariable("CONNECTION_STRING"),
                ["USE_INSIGHTS_LAYER"] = "false",
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
