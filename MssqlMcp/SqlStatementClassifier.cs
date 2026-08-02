// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.

using System.Text.RegularExpressions;

namespace Mssql.McpServer;

/// <summary>
/// Classifies T-SQL for routing between ReadData (read-only queries) and ExecuteSQL (DDL/DML).
/// </summary>
internal static partial class SqlStatementClassifier
{
    public const string ReadDataRejectedMessage =
        "ReadData accepts only read-only SELECT queries (including WITH ... SELECT). Use ExecuteSQL for DDL/DML and SELECT ... INTO.";

    public const string ExecuteSqlSelectRejectedMessage =
        "ExecuteSQL does not allow SELECT or other read-only queries. Use ReadData for all SELECT statements, including sys.*, INFORMATION_SCHEMA, and DMVs.";

    private static readonly string[] ExecutableFirstKeywords =
    [
        "INSERT", "UPDATE", "DELETE", "MERGE", "CREATE", "ALTER", "DROP",
        "TRUNCATE", "EXEC", "EXECUTE", "GRANT", "REVOKE", "DENY", "BACKUP", "RESTORE"
    ];

    private static readonly string[] MutatingKeywordsAfterWith =
    [
        "INSERT", "UPDATE", "DELETE", "MERGE", "CREATE", "ALTER", "DROP",
        "TRUNCATE", "EXEC", "EXECUTE"
    ];

    public static bool IsReadOnlyQuery(string sql) =>
        TryValidateReadOnly(sql, out _);

    public static bool TryValidateReadOnly(string sql, out string? error)
    {
        error = null;
        if (!TryPrepare(sql, out var normalized, out error))
        {
            return false;
        }

        if (!TryGetFirstKeyword(normalized, out var firstKeyword))
        {
            error = "Could not determine the SQL statement type.";
            return false;
        }

        if (firstKeyword == "SELECT")
        {
            if (ContainsSelectInto(normalized))
            {
                error = "SELECT ... INTO is not allowed in ReadData. Use ExecuteSQL.";
                return false;
            }

            return true;
        }

        if (firstKeyword == "WITH")
        {
            if (ContainsMutatingKeyword(normalized))
            {
                error = ReadDataRejectedMessage;
                return false;
            }

            if (!ContainsSelectKeyword(normalized))
            {
                error = "WITH queries in ReadData must culminate in a SELECT.";
                return false;
            }

            return true;
        }

        error = ReadDataRejectedMessage;
        return false;
    }

    public static bool TryValidateExecutable(string sql, out string? error)
    {
        error = null;
        if (!TryPrepare(sql, out var normalized, out error))
        {
            return false;
        }

        if (IsReadOnlyQuery(sql))
        {
            error = ExecuteSqlSelectRejectedMessage;
            return false;
        }

        if (!TryGetFirstKeyword(normalized, out var firstKeyword))
        {
            error = "Could not determine the SQL statement type.";
            return false;
        }

        if (firstKeyword is "SELECT" or "WITH")
        {
            error = ExecuteSqlSelectRejectedMessage;
            return false;
        }

        if (ExecutableFirstKeywords.Contains(firstKeyword, StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        error = $"Unsupported or unrecognized statement type '{firstKeyword}'. Use ReadData for SELECT queries.";
        return false;
    }

    private static bool TryPrepare(string sql, out string normalized, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(sql))
        {
            normalized = string.Empty;
            error = "SQL is required.";
            return false;
        }

        normalized = StripComments(sql).Trim();
        if (normalized.Length == 0)
        {
            error = "SQL is required.";
            return false;
        }

        if (ContainsMultipleStatements(normalized))
        {
            error = "Only a single T-SQL statement is allowed (no batch separators or multiple statements).";
            return false;
        }

        return true;
    }

    private static bool ContainsMultipleStatements(string sql)
    {
        var trimmed = sql.TrimEnd();
        if (trimmed.EndsWith(';'))
        {
            trimmed = trimmed[..^1].TrimEnd();
        }

        return trimmed.Contains(';');
    }

    private static bool TryGetFirstKeyword(string normalized, out string keyword)
    {
        keyword = string.Empty;
        var match = FirstKeywordRegex().Match(normalized);
        if (!match.Success)
        {
            return false;
        }

        keyword = match.Groups[1].Value.ToUpperInvariant();
        return true;
    }

    private static bool ContainsSelectInto(string sql) =>
        SelectIntoRegex().IsMatch(sql);

    private static bool ContainsSelectKeyword(string sql) =>
        SelectKeywordRegex().IsMatch(sql);

    private static bool ContainsMutatingKeyword(string sql)
    {
        foreach (var word in MutatingKeywordsAfterWith)
        {
            if (MutatingKeywordRegex(word).IsMatch(sql))
            {
                return true;
            }
        }

        return false;
    }

    private static string StripComments(string sql)
    {
        var withoutBlock = BlockCommentRegex().Replace(sql, " ");
        return LineCommentRegex().Replace(withoutBlock, " ");
    }

    [GeneratedRegex(@"\b(SELECT|WITH|INSERT|UPDATE|DELETE|MERGE|CREATE|ALTER|DROP|TRUNCATE|EXEC|EXECUTE|GRANT|REVOKE|DENY|BACKUP|RESTORE)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FirstKeywordRegex();

    [GeneratedRegex(@"\bSELECT\b[\s\S]*?\bINTO\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SelectIntoRegex();

    [GeneratedRegex(@"\bSELECT\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SelectKeywordRegex();

    [GeneratedRegex(@"/\*.*?\*/", RegexOptions.Singleline)]
    private static partial Regex BlockCommentRegex();

    [GeneratedRegex(@"--[^\r\n]*")]
    private static partial Regex LineCommentRegex();

    private static Regex MutatingKeywordRegex(string word) =>
        new($@"\b{Regex.Escape(word)}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
}
