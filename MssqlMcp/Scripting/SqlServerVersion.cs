// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.

using System.Globalization;

namespace Mssql.McpServer.Scripting;

/// <summary>SERVERPROPERTY('ProductVersion'), e.g. 10.50.6560.0 (2008 R2) or 16.0.1000.6 (2022).</summary>
internal sealed record SqlServerVersion(int Major, int Minor, int Build)
{
    public static SqlServerVersion Parse(string productVersion)
    {
        var parts = productVersion.Split('.');
        int At(int i) => parts.Length > i && int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0;
        return new SqlServerVersion(At(0), At(1), At(2));
    }

    /// <summary>CREATE OR ALTER shipped in SQL Server 2016 SP1 (13.0.4001).</summary>
    public bool SupportsCreateOrAlter => Major > 13 || (Major == 13 && Build >= 4001);

    public bool SupportsAlterRoleAddMember => Major >= 11;

    public bool SupportsTemporal => Major >= 13;

    public bool SupportsMemoryOptimized => Major >= 12;
}
