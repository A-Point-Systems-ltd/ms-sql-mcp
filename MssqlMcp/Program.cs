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

        // Validate connection string exists
        var connStr = Environment.GetEnvironmentVariable("CONNECTION_STRING");
        if (string.IsNullOrEmpty(connStr))
        {
            var errorMsg = "FATAL: CONNECTION_STRING environment variable is not set!";
            Console.Error.WriteLine(errorMsg);
            log.Append(errorMsg);
            Environment.ExitCode = 1;
            return;
        }

        log.Append($"Connection String: {ConnectionStringMasker.Mask(connStr)}");

        // Test SQL connection before starting MCP server
        try
        {
            log.Append("Testing SQL Server connection...");
            ISqlConnectionFactory testFactory = new SqlConnectionFactory();
            await using var testConnection = await testFactory.GetOpenConnectionAsync(CancellationToken.None);
            var successMsg = $"SQL Server connection test SUCCESSFUL - Server: {testConnection.DataSource}, Database: {testConnection.Database}";
            Console.Error.WriteLine(successMsg);
            log.Append(successMsg);
        }
        catch (Exception ex)
        {
            var errorMsg = $"FATAL: SQL Server connection test FAILED: {ex.Message}";
            var detailMsg = $"Connection String (masked): {ConnectionStringMasker.Mask(connStr)}";

            Console.Error.WriteLine(errorMsg);
            Console.Error.WriteLine(detailMsg);

            log.Append(errorMsg);
            log.Append(detailMsg);
            log.Append($"Stack trace: {ex.StackTrace}");

            Environment.ExitCode = 1;
            return;
        }

        log.Append("Starting MCP server initialization...");

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

        // The SDK creates a Tools instance per call via ActivatorUtilities, so Tools must stay stateless.
        _ = builder.Services
            .AddMcpServer()
            .WithStdioServerTransport()
            .WithToolsFromAssembly();

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
