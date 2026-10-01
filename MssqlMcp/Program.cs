// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.

using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Mssql.McpServer.Connections;
using Mssql.McpServer.InsightsLayer;
using System.Diagnostics;

namespace Mssql.McpServer;

internal class Program
{
    /// <summary>
    /// Entry point for the MCP server application.
    /// Sets up logging, configures the MCP server with standard I/O transport and tool discovery,
    /// builds the host, and runs the server asynchronously.
    /// </summary>
    /// <param name="args">Command-line arguments.</param>
    private static async Task Main(string[] args)
    {
        var log = new StartupLog(ResolveLogFilePath());

        log.Header(
        [
            "=".PadRight(80, '='),
            $"MSSQL MCP Server Starting - {DateTime.Now:yyyy-MM-dd HH:mm:ss}",
            "=".PadRight(80, '='),
            $"Process ID: {Environment.ProcessId}",
            $"Working Directory: {Environment.CurrentDirectory}",
            $"Log File: {log.FilePath}",
            $"App Base Directory: {AppContext.BaseDirectory}",
            $".NET Version: {Environment.Version}",
            $"OS Version: {Environment.OSVersion}",
            $"Machine Name: {Environment.MachineName}",
            $"User: {Environment.UserName}",
            $"Command Line Args: {string.Join(" ", args)}",
        ]);

        // Create the application host builder
        var builder = Host.CreateApplicationBuilder(args);

        // stdout carries the MCP protocol, so every log line must go to stderr.
        _ = builder.Logging.AddConsole(consoleLogOptions =>
        {
            consoleLogOptions.LogToStandardErrorThreshold = LogLevel.Trace;
        });

        IReadOnlyList<ConnectionProfile> profiles;
        try
        {
            profiles = ConnectionConfigLoader.Load(Environment.GetEnvironmentVariable, File.ReadAllText);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"FATAL: invalid connection configuration: {ex.Message}");
            log.Append($"FATAL: invalid connection configuration: {ex.Message}");
            Environment.ExitCode = 1;
            return;
        }

        var adhocAllowed = string.Equals(Environment.GetEnvironmentVariable("MSSQL_ALLOW_ADHOC_CONNECTIONS"), "true", StringComparison.OrdinalIgnoreCase);
        if (profiles.Count == 0 && !adhocAllowed)
        {
            const string errorMsg = "FATAL: no connection configured. Set CONNECTION_STRING, MSSQL_CONNECTIONS or MSSQL_CONNECTIONS_FILE (or MSSQL_ALLOW_ADHOC_CONNECTIONS=true).";
            Console.Error.WriteLine(errorMsg);
            log.Append(errorMsg);
            Environment.ExitCode = 1;
            return;
        }

        var legacyMode = profiles.Count == 1 && profiles[0].Source == ConnectionSource.Legacy;
        var registry = new ConnectionRegistry(profiles);
        foreach (var profile in profiles)
        {
            log.Append($"Connection '{profile.Name}'{(profile.ReadOnly ? " [read-only]" : "")}: {ConnectionStringMasker.Mask(profile.ConnectionString)}");
        }

        if (legacyMode)
        {
            // Unchanged legacy behavior: original connection string and timeout, fail fast.
            try
            {
                await using var test = new SqlConnection(profiles[0].ConnectionString);
                await test.OpenAsync();
                log.Append($"SQL Server connection test SUCCESSFUL - Server: {test.DataSource}, Database: {test.Database}");
            }
            catch (Exception ex)
            {
                var errorMsg = $"FATAL: SQL Server connection test FAILED: {ex.Message}";
                var detailMsg = $"Connection String (masked): {ConnectionStringMasker.Mask(profiles[0].ConnectionString)}";
                Console.Error.WriteLine(errorMsg);
                Console.Error.WriteLine(detailMsg);
                log.Append(errorMsg);
                log.Append(detailMsg);
                log.Append($"Stack trace: {ex.StackTrace}");
                Environment.ExitCode = 1;
                return;
            }
        }
        else
        {
            // Probe all targets at once with a short timeout so unreachable ones cannot delay MCP initialize by N x 15 s.
            await Task.WhenAll(profiles.Select(p => ProbeAsync(p, log)));
        }

        log.Append(registry.ConnectionArgumentRequired
            ? $"{registry.Count} connections: tools require the 'connection' argument."
            : "Single connection: the 'connection' argument is optional.");

        log.Append("Starting MCP server initialization...");

        _ = builder.Services.AddSingleton(registry);
        _ = builder.Services.AddSingleton<ISqlConnectionFactory, SqlConnectionFactory>();
        if (InsightsLayerEnvironment.IsInsightsLayerEnabled)
        {
            _ = builder.Services.AddSingleton<IInsightsLayerService, InsightsLayerService>();
            _ = builder.Services.AddSingleton<InsightDdlProcessingQueue>();
            _ = builder.Services.AddSingleton<IInsightDdlProcessingQueue>(
                static sp => sp.GetRequiredService<InsightDdlProcessingQueue>());
            _ = builder.Services.AddHostedService(
                static sp => sp.GetRequiredService<InsightDdlProcessingQueue>());
        }
        else
        {
            _ = builder.Services.AddSingleton<IInsightsLayerService>(NoOpInsightsLayerService.Instance);
            _ = builder.Services.AddSingleton<IInsightDdlProcessingQueue>(NoOpInsightDdlProcessingQueue.Instance);
        }

        // Opt-in, for the VS Code extension's private runner process only: agents must never see run_script.
        var scriptRunnerEnabled = ScriptRunnerTools.IsEnabled(Environment.GetEnvironmentVariable);

        // The SDK creates a Tools instance per call via ActivatorUtilities, so Tools must stay stateless.
        var mcp = builder.Services
            .AddMcpServer(options => options.ServerInstructions = ServerInstructions.Build(registry))
            .WithStdioServerTransport()
            .WithRequestFilters(filters => filters.AddCallToolFilter(next => ConnectionRoutingFilter.Create(next, scriptRunnerEnabled)))
            .WithToolsFromAssembly();

        if (scriptRunnerEnabled)
        {
            _ = mcp.WithTools<ScriptRunnerTools>();
            log.Append("Script runner tool enabled (MSSQL_SCRIPT_RUNNER) - intended for the VS Code extension only.");
        }

        log.Append("Building host...");
        var host = builder.Build();
        log.Append("Host built successfully, MCP server starting...");

        // Setup cancellation token for graceful shutdown (Ctrl+C or SIGTERM)
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (sender, eventArgs) =>
        {
            eventArgs.Cancel = true; // Prevent the process from terminating immediately
            cts.Cancel();
        };

        try
        {
            log.Append("MCP server is now running and accepting connections");
            await host.RunAsync(cts.Token);
            log.Append("MCP server shut down gracefully");
        }
        catch (Exception ex)
        {
            // Attempt to log the exception using the host's logger
            if (host.Services.GetService(typeof(ILogger<Program>)) is ILogger<Program> logger)
            {
                logger.LogCritical(ex, "Unhandled exception occurred during host execution.");
            }
            else
            {
                Console.Error.WriteLine($"Unhandled exception: {ex}");
            }

            log.Append($"FATAL ERROR: {ex.Message}");
            log.Append($"Stack: {ex.StackTrace}");

            // Set a non-zero exit code
            Environment.ExitCode = 1;
        }
        finally
        {
            log.Append($"Process exiting with code: {Environment.ExitCode}");
        }
    }

    /// <summary>Startup reachability check for one multi-connection profile. Logs the outcome and never throws.</summary>
    private static async Task ProbeAsync(ConnectionProfile profile, StartupLog log)
    {
        try
        {
            var probe = new SqlConnectionStringBuilder(profile.ConnectionString) { ConnectTimeout = 5 };
            await using var test = new SqlConnection(probe.ConnectionString);
            await test.OpenAsync();
            log.Append($"Connection '{profile.Name}' test SUCCESSFUL - Server: {test.DataSource}, Database: {test.Database}");
        }
        catch (Exception ex)
        {
            var msg = $"Connection '{profile.Name}' test FAILED: {ex.Message}";
            Console.Error.WriteLine(msg);
            log.Append(msg);
        }
    }

    /// <summary>
    /// <c>LOG_FILE_PATH</c> may be a file or a directory; falls back to %LOCALAPPDATA%\MssqlMcp\Logs.
    /// </summary>
    private static string ResolveLogFilePath()
    {
        var fileName = $"mssql-mcp-{DateTime.Now:yyyy-MM-dd-HHmmss}.log";
        var customLogPath = Environment.GetEnvironmentVariable("LOG_FILE_PATH");
        if (!string.IsNullOrEmpty(customLogPath))
        {
            try
            {
                var isDirectory = Directory.Exists(customLogPath)
                    || customLogPath.EndsWith(Path.DirectorySeparatorChar)
                    || customLogPath.EndsWith(Path.AltDirectorySeparatorChar);
                var path = isDirectory ? Path.Combine(customLogPath, fileName) : customLogPath;
                var logDir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(logDir))
                {
                    Directory.CreateDirectory(logDir);
                }

                return path;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Warning: Invalid LOG_FILE_PATH '{customLogPath}': {ex.Message}");
                Console.Error.WriteLine("Falling back to default log location.");
            }
        }

        var logDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MssqlMcp",
            "Logs");
        try
        {
            Directory.CreateDirectory(logDirectory);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Warning: cannot create log directory '{logDirectory}': {ex.Message}");
        }

        return Path.Combine(logDirectory, fileName);
    }
}

/// <summary>
/// Best-effort startup/shutdown log. Several server instances may share one LOG_FILE_PATH, so writes append
/// with shared access, carry the PID, and never throw: a locked or read-only log must not kill the server.
/// </summary>
internal sealed class StartupLog(string filePath)
{
    private readonly int _pid = Environment.ProcessId;

    public string FilePath { get; } = filePath;

    public void Header(IEnumerable<string> lines)
    {
        foreach (var line in lines)
        {
            Write(line);
        }

        Write(string.Empty);
    }

    public void Append(string message) => Write($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{_pid}] - {message}");

    private void Write(string line)
    {
        try
        {
            using var stream = new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            using var writer = new StreamWriter(stream);
            writer.WriteLine(line);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Console.Error.WriteLine($"[log unavailable: {ex.Message}] {line}");
        }
    }
}
