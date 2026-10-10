// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.

using System.Globalization;

namespace Mssql.McpServer.Scripting;

/// <summary>
/// SERVERPROPERTY('ProductVersion'), e.g. 10.50.6560.0 (2008 R2) or 16.0.1000.6 (2022), plus SERVERPROPERTY('EngineEdition').
/// Azure SQL Database (5) and Azure SQL Managed Instance (8) report 12.0.2000.x but support the current T-SQL surface.
/// </summary>
internal sealed record SqlServerVersion(int Major, int Minor, int Build, int EngineEdition = 0)
{
    public const int EngineEditionAzureSqlDatabase = 5;
    public const int EngineEditionAzureManagedInstance = 8;

    public static SqlServerVersion Parse(string productVersion, int engineEdition = 0)
    {
        var parts = productVersion.Split('.');
        int At(int i) => parts.Length > i && int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0;
        return new SqlServerVersion(At(0), At(1), At(2), engineEdition);
    }

    public bool IsAzure => EngineEdition is EngineEditionAzureSqlDatabase or EngineEditionAzureManagedInstance;

    /// <summary>CREATE OR ALTER shipped in SQL Server 2016 SP1 (13.0.4001).</summary>
    public bool SupportsCreateOrAlter => IsAzure || Major > 13 || (Major == 13 && Build >= 4001);

    public bool SupportsAlterRoleAddMember => IsAzure || Major >= 11;

    /// <summary>sys.server_permissions exists on SQL Server and Managed Instance, not on Azure SQL Database.</summary>
    public bool HasServerPermissions => EngineEdition != EngineEditionAzureSqlDatabase;

    public bool SupportsTemporal => IsAzure || Major >= 13;

    public bool SupportsMemoryOptimized => IsAzure || Major >= 12;
}
