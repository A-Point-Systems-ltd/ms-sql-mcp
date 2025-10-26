// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.

using System.ComponentModel;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace Mssql.McpServer;

public partial class Tools
{
    private const string ServerPropertiesQuery = @"
        SELECT 
            SERVERPROPERTY('ProductVersion') AS ProductVersion,
            SERVERPROPERTY('ProductLevel') AS ProductLevel,
            SERVERPROPERTY('Edition') AS Edition,
            SERVERPROPERTY('EngineEdition') AS EngineEdition,
            SERVERPROPERTY('ServerName') AS ServerName,
            SERVERPROPERTY('MachineName') AS MachineName,
            SERVERPROPERTY('InstanceName') AS InstanceName,
            SERVERPROPERTY('IsClustered') AS IsClustered,
            SERVERPROPERTY('IsFullTextInstalled') AS IsFullTextInstalled,
            SERVERPROPERTY('IsIntegratedSecurityOnly') AS IsIntegratedSecurityOnly,
            SERVERPROPERTY('Collation') AS Collation,
            @@VERSION AS VersionString";

    private const string HardwareInfoQuery = @"
        SELECT 
            cpu_count AS CPUCount,
            hyperthread_ratio AS HyperthreadRatio,
            physical_memory_kb / 1024 AS PhysicalMemoryMB,
            virtual_memory_kb / 1024 AS VirtualMemoryMB,
            sqlserver_start_time AS SQLServerStartTime
        FROM sys.dm_os_sys_info";

    private const string DatabaseStatsQuery = @"
        SELECT 
            COUNT(*) AS TotalDatabases,
            SUM(CASE WHEN state = 0 THEN 1 ELSE 0 END) AS OnlineDatabases,
            SUM(CASE WHEN state != 0 THEN 1 ELSE 0 END) AS OfflineDatabases
        FROM sys.databases
        WHERE database_id > 4"; // Exclude system databases for counts

    [McpServerTool(
        Title = "Get Server Info",
        ReadOnly = true,
        Idempotent = true,
        Destructive = false),
        Description("Returns comprehensive SQL Server metadata including version, edition, hardware information, and database statistics")]
    public async Task<DbOperationResult> GetServerInfo()
    {
        var conn = await _connectionFactory.GetOpenConnectionAsync();
        try
        {
            using (conn)
            {
                var result = new Dictionary<string, object>();

                // Query 1: Server Properties
                using (var cmd = new SqlCommand(ServerPropertiesQuery, conn))
                {
                    using var reader = await cmd.ExecuteReaderAsync();
                    if (await reader.ReadAsync())
                    {
                        result["server"] = new
                        {
                            productVersion = reader["ProductVersion"] is DBNull ? null : reader["ProductVersion"],
                            productLevel = reader["ProductLevel"] is DBNull ? null : reader["ProductLevel"],
                            edition = reader["Edition"] is DBNull ? null : reader["Edition"],
                            engineEdition = reader["EngineEdition"] is DBNull ? null : reader["EngineEdition"],
                            serverName = reader["ServerName"] is DBNull ? null : reader["ServerName"],
                            machineName = reader["MachineName"] is DBNull ? null : reader["MachineName"],
                            instanceName = reader["InstanceName"] is DBNull ? null : reader["InstanceName"],
                            isClustered = reader["IsClustered"] is DBNull ? null : reader["IsClustered"],
                            isFullTextInstalled = reader["IsFullTextInstalled"] is DBNull ? null : reader["IsFullTextInstalled"],
                            isIntegratedSecurityOnly = reader["IsIntegratedSecurityOnly"] is DBNull ? null : reader["IsIntegratedSecurityOnly"],
                            collation = reader["Collation"] is DBNull ? null : reader["Collation"],
                            versionString = reader["VersionString"] is DBNull ? null : reader["VersionString"]
                        };
                    }
                }

                // Query 2: Hardware Info
                using (var cmd = new SqlCommand(HardwareInfoQuery, conn))
                {
                    using var reader = await cmd.ExecuteReaderAsync();
                    if (await reader.ReadAsync())
                    {
                        result["hardware"] = new
                        {
                            cpuCount = reader["CPUCount"],
                            hyperthreadRatio = reader["HyperthreadRatio"],
                            physicalMemoryMB = reader["PhysicalMemoryMB"],
                            virtualMemoryMB = reader["VirtualMemoryMB"],
                            sqlServerStartTime = reader["SQLServerStartTime"]
                        };
                    }
                }

                // Query 3: Database Statistics
                using (var cmd = new SqlCommand(DatabaseStatsQuery, conn))
                {
                    using var reader = await cmd.ExecuteReaderAsync();
                    if (await reader.ReadAsync())
                    {
                        result["databases"] = new
                        {
                            totalDatabases = reader["TotalDatabases"],
                            onlineDatabases = reader["OnlineDatabases"],
                            offlineDatabases = reader["OfflineDatabases"]
                        };
                    }
                }

                return new DbOperationResult(success: true, data: result);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetServerInfo failed: {Message}", ex.Message);
            return new DbOperationResult(success: false, error: ex.Message);
        }
    }
}