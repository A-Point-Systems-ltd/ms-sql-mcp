// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.

namespace Mssql.McpServer;

/// <summary>
/// Parses trigger identifiers: name, schema.name, or schema.table.name.
/// </summary>
internal readonly record struct TriggerQualifiedName(string Name, string? Schema, string? TableName)
{
    public static TriggerQualifiedName Parse(string qualifiedName)
    {
        if (string.IsNullOrWhiteSpace(qualifiedName))
        {
            return new TriggerQualifiedName(string.Empty, null, null);
        }

        var trimmed = qualifiedName.Trim();
        if (!trimmed.Contains('.'))
        {
            return new TriggerQualifiedName(trimmed, null, null);
        }

        var parts = trimmed.Split('.', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return parts.Length switch
        {
            0 => new TriggerQualifiedName(trimmed, null, null),
            1 => new TriggerQualifiedName(parts[0], null, null),
            2 => new TriggerQualifiedName(parts[1], parts[0], null),
            _ => new TriggerQualifiedName(parts[^1], parts[0], parts[^2])
        };
    }

    public string DisplayName
    {
        get
        {
            if (Schema is null)
            {
                return Name;
            }

            if (TableName is null)
            {
                return $"{Schema}.{Name}";
            }

            return $"{Schema}.{TableName}.{Name}";
        }
    }
}
