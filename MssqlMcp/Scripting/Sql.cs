// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.

namespace Mssql.McpServer.Scripting;

/// <summary>Identifier and literal quoting for generated T-SQL. Every name in generated DDL goes through <see cref="Q"/>.</summary>
internal static class Sql
{
    public static string Q(string identifier) => "[" + identifier.Replace("]", "]]", StringComparison.Ordinal) + "]";

    public static string N(string? value) => value is null ? "NULL" : "N'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    public static string Qualified(string schema, string name) => Q(schema) + "." + Q(name);
}
