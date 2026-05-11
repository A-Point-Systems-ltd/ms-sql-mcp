// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Mssql.McpServer.InsightsLayer;
using System.Diagnostics;
using System.Reflection;

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
        // Setup log file path - allow user to specify via environment variable
        var customLogPath = Environment.GetEnvironmentVariable("LOG_FILE_PATH");
        string logFilePath;
        bool useCustomPath = false;
        
        if (!string.IsNullOrEmpty(customLogPath))
        {
            // User specified a custom log file path
            try
            {
                // If it's a directory path, append filename; otherwise use as-is
                if (Directory.Exists(customLogPath) || customLogPath.EndsWith(Path.DirectorySeparatorChar) || customLogPath.EndsWith(Path.AltDirectorySeparatorChar))
                {
                    logFilePath = Path.Combine(customLogPath, $"mssql-mcp-{DateTime.Now:yyyy-MM-dd-HHmmss}.log");
                }
                else
                {
                    logFilePath = customLogPath;
                }
                
                // Ensure directory exists
                var logDir = Path.GetDirectoryName(logFilePath);
                if (!string.IsNullOrEmpty(logDir))
                {
                    Directory.CreateDirectory(logDir);
                }
                
                useCustomPath = true;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Warning: Invalid LOG_FILE_PATH '{customLogPath}': {ex.Message}");
                Console.Error.WriteLine("Falling back to default log location.");
                logFilePath = string.Empty; // Will be set below
            }
        }
        else
        {
            logFilePath = string.Empty; // Will be set below
        }
        
        if (!useCustomPath || string.IsNullOrEmpty(logFilePath))
        {
            // Use default log location
            var logDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MssqlMcp",
                "Logs");
            Directory.CreateDirectory(logDirectory);
            logFilePath = Path.Combine(logDirectory, $"mssql-mcp-{DateTime.Now:yyyy-MM-dd-HHmmss}.log");
        }

        // Write startup information to log file
        try
        {
            var startupInfo = new List<string>
            {
                "=".PadRight(80, '='),
                $"MSSQL MCP Server Starting - {DateTime.Now:yyyy-MM-dd HH:mm:ss}",
                "=".PadRight(80, '='),
                $"Process ID: {Process.GetCurrentProcess().Id}",
                $"Working Directory: {Environment.CurrentDirectory}",
                $"Log File: {logFilePath}",
                $"Assembly Location: {Assembly.GetExecutingAssembly().Location}",
                $".NET Version: {Environment.Version}",
                $"OS Version: {Environment.OSVersion}",
                $"Machine Name: {Environment.MachineName}",
                $"User: {Environment.UserName}",
                $"Command Line Args: {string.Join(" ", args)}",
                ""
            };

            // Log connection string (masked)
            var connectionString = Environment.GetEnvironmentVariable("CONNECTION_STRING");
            if (!string.IsNullOrEmpty(connectionString))
            {
                var maskedConnStr = MaskConnectionString(connectionString);
                startupInfo.Add($"Connection String: {maskedConnStr}");
            }
            else
            {
                startupInfo.Add("Connection String: NOT SET - This will cause connection failures!");
            }

            startupInfo.Add("");
            File.WriteAllLines(logFilePath, startupInfo);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to create log file: {ex.Message}");
        }

        // Create the application host builder
        var builder = Host.CreateApplicationBuilder(args);

        // Configure console logging with Trace level
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
            File.AppendAllText(logFilePath, $"\n{DateTime.Now:yyyy-MM-dd HH:mm:ss} - {errorMsg}\n");
            Environment.ExitCode = 1;
            return;
        }

        File.AppendAllText(logFilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - Connection string validated (length: {connStr.Length})\n");

        // Test SQL connection before starting MCP server
        try
        {
            File.AppendAllText(logFilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - Testing SQL Server connection...\n");
            var testFactory = new SqlConnectionFactory();
            using var testConnection = await testFactory.GetOpenConnectionAsync();
            var successMsg = $"SQL Server connection test SUCCESSFUL - Server: {testConnection.DataSource}, Database: {testConnection.Database}";
            Console.Error.WriteLine(successMsg);
            File.AppendAllText(logFilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - {successMsg}\n");
        }
        catch (Exception ex)
        {
            var errorMsg = $"FATAL: SQL Server connection test FAILED: {ex.Message}";
            var detailMsg = $"Connection String (masked): {MaskConnectionString(connStr)}";
            var stackMsg = $"Stack trace: {ex.StackTrace}";
            
            Console.Error.WriteLine(errorMsg);
            Console.Error.WriteLine(detailMsg);
            
            File.AppendAllText(logFilePath, $"\n{DateTime.Now:yyyy-MM-dd HH:mm:ss} - {errorMsg}\n");
            File.AppendAllText(logFilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - {detailMsg}\n");
            File.AppendAllText(logFilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - {stackMsg}\n");
            
            Environment.ExitCode = 1;
            return;
        }

        File.AppendAllText(logFilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - Starting MCP server initialization...\n");

        // Register ISqlConnectionFactory and Tools for DI
        _ = builder.Services.AddSingleton<ISqlConnectionFactory, SqlConnectionFactory>();
        if (InsightsLayerEnvironment.IsInsightsLayerEnabled)
        {
            _ = builder.Services.AddSingleton<IInsightsLayerService, InsightsLayerService>();
        }
        else
        {
            _ = builder.Services.AddSingleton<IInsightsLayerService>(NoOpInsightsLayerService.Instance);
        }

        _ = builder.Services.AddSingleton<Tools>();

        // Register MCP server and tools (instance-based)
        _ = builder.Services
            .AddMcpServer()
            .WithStdioServerTransport()
            .WithToolsFromAssembly();

        File.AppendAllText(logFilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - Building host...\n");

        // Build the host
        var host = builder.Build();

        File.AppendAllText(logFilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - Host built successfully, MCP server starting...\n");

        // Setup cancellation token for graceful shutdown (Ctrl+C or SIGTERM)
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (sender, eventArgs) =>
        {
            eventArgs.Cancel = true; // Prevent the process from terminating immediately
            cts.Cancel();
        };

        try
        {
            File.AppendAllText(logFilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - MCP server is now running and accepting connections\n");
            
            // Run the host with cancellation support
            await host.RunAsync(cts.Token);
            
            File.AppendAllText(logFilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - MCP server shut down gracefully\n");
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

            File.AppendAllText(logFilePath, $"\n{DateTime.Now:yyyy-MM-dd HH:mm:ss} - FATAL ERROR: {ex.Message}\n");
            File.AppendAllText(logFilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - Stack: {ex.StackTrace}\n");

            // Set a non-zero exit code
            Environment.ExitCode = 1;
        }
        finally
        {
            File.AppendAllText(logFilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - Process exiting with code: {Environment.ExitCode}\n");
        }
    }

    /// <summary>
    /// Masks sensitive information in connection strings for logging
    /// </summary>
    private static string MaskConnectionString(string connectionString)
    {
        if (string.IsNullOrEmpty(connectionString))
        {
            return string.Empty;
        }

        var parts = connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries);
        var maskedParts = new List<string>();

        foreach (var part in parts)
        {
            var keyValue = part.Split('=', 2);
            if (keyValue.Length == 2)
            {
                var key = keyValue[0].Trim();
                var value = keyValue[1].Trim();

                // Mask password-related fields
                if (key.Contains("password", StringComparison.OrdinalIgnoreCase) ||
                    key.Contains("pwd", StringComparison.OrdinalIgnoreCase))
                {
                    maskedParts.Add($"{key}=***MASKED***");
                }
                else
                {
                    maskedParts.Add(part);
                }
            }
            else
            {
                maskedParts.Add(part);
            }
        }

        return string.Join("; ", maskedParts);
    }
}
