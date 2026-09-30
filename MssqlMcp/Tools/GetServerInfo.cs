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
            sqlserver_start_time AS SQLServerStartTime
        FROM sys.dm_os_sys_info";

    private const string ProcessMemoryQuery = @"
        SELECT
            physical_memory_in_use_kb / 1024 AS PhysicalMemoryMB,
            virtual_address_space_committed_kb / 1024 AS VirtualMemoryMB
        FROM sys.dm_os_process_memory";

    private const string DatabaseStatsQuery = @"
        SELECT 
            COUNT(*) AS TotalDatabases,
            SUM(CASE WHEN state = 0 THEN 1 ELSE 0 END) AS OnlineDatabases,
            SUM(CASE WHEN state != 0 THEN 1 ELSE 0 END) AS OfflineDatabases
        FROM sys.databases
        WHERE database_id > 4"; // Exclude system databases for counts

    [McpServerTool(
        Name = ToolNames.GetServerInfo,
        Title = "Get Server Info",
        ReadOnly = true,
        Idempotent = true,
        Destructive = false),
        Description("Returns SQL Server metadata in three sections: 'server' (ProductVersion/ProductLevel/Edition/EngineEdition/ServerName/MachineName/InstanceName/IsClustered/IsFullTextInstalled/IsIntegratedSecurityOnly/Collation/@@VERSION), 'hardware' (cpuCount, hyperthreadRatio, physicalMemoryMB, virtualMemoryMB, sqlServerStartTime, optional 'warning' string), and 'databases' (totalDatabases/onlineDatabases/offlineDatabases excluding system DBs). Compatible with SQL Server 2008 R2 through 2022 and Azure SQL. When VIEW SERVER STATE is restricted or a DMV column does not exist on the target version, hardware fields are returned as null and 'hardware.warning' explains why; the call still succeeds.")]
    public async Task<DbOperationResult> GetServerInfo(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var conn = await _connectionFactory.GetOpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            {
                var result = new Dictionary<string, object>();

                // Query 1: Server Properties
                await using (var cmd = new SqlCommand(ServerPropertiesQuery, conn))
                {
                    await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                    if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
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

                // Query 2: Hardware / runtime info.
                // Keep this section resilient across SQL Server versions + permission boundaries:
                // - sys.dm_os_sys_info shape changed between versions.
                // - VIEW SERVER STATE may be restricted for some users/tiers.
                object? cpuCount = null;
                object? hyperthreadRatio = null;
                object? sqlServerStartTime = null;
                object? physicalMemoryMB = null;
                object? virtualMemoryMB = null;
                string? hardwareWarning = null;

                try
                {
                    await using var cmd = new SqlCommand(HardwareInfoQuery, conn);
                    await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                    if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        cpuCount = reader["CPUCount"] is DBNull ? null : reader["CPUCount"];
                        hyperthreadRatio = reader["HyperthreadRatio"] is DBNull ? null : reader["HyperthreadRatio"];
                        sqlServerStartTime = reader["SQLServerStartTime"] is DBNull ? null : reader["SQLServerStartTime"];
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    hardwareWarning = $"sys.dm_os_sys_info unavailable: {ex.Message}";
                    _logger.LogWarning(ex, "{Tool}: unable to read sys.dm_os_sys_info.", ToolNames.GetServerInfo);
                }

                try
                {
                    await using var cmd = new SqlCommand(ProcessMemoryQuery, conn);
                    await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                    if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        physicalMemoryMB = reader["PhysicalMemoryMB"] is DBNull ? null : reader["PhysicalMemoryMB"];
                        virtualMemoryMB = reader["VirtualMemoryMB"] is DBNull ? null : reader["VirtualMemoryMB"];
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    hardwareWarning = hardwareWarning is null
                        ? $"sys.dm_os_process_memory unavailable: {ex.Message}"
                        : $"{hardwareWarning}; sys.dm_os_process_memory unavailable: {ex.Message}";
                    _logger.LogWarning(ex, "{Tool}: unable to read sys.dm_os_process_memory.", ToolNames.GetServerInfo);
                }

                result["hardware"] = new
                {
                    cpuCount,
                    hyperthreadRatio,
                    physicalMemoryMB,
                    virtualMemoryMB,
                    sqlServerStartTime,
                    warning = hardwareWarning
                };

                // Query 3: Database Statistics
                await using (var cmd = new SqlCommand(DatabaseStatsQuery, conn))
                {
                    await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                    if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
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
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "{Tool} failed: {Message}", ToolNames.GetServerInfo, ex.Message);
            return new DbOperationResult(success: false, error: ex.Message);
        }
    }
}