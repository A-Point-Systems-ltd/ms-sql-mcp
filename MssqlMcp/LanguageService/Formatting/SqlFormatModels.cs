namespace Mssql.McpServer.LanguageService.Formatting;

/// <summary>How the formatter cases keywords, system data types and built-in function names.</summary>
public enum KeywordCase
{
    Lower,
    Upper,
    Preserve,
}

/// <summary>
/// Formatter options. <paramref name="IndentSize"/> and <paramref name="UseTabs"/> come from the editor;
/// <paramref name="MaxItemsPerRow"/> caps column / value / SET lists per row; <paramref name="MaxBlankLines"/> caps
/// kept blank lines; <paramref name="BaseIndent"/> (columns) is the indent of top-level statements (range fallback).
/// </summary>
public sealed record SqlFormatOptions(
    int IndentSize = 4,
    bool UseTabs = false,
    KeywordCase KeywordCase = KeywordCase.Lower,
    int MaxItemsPerRow = 4,
    int MaxBlankLines = 2,
    int BaseIndent = 0)
{
    /// <summary>Options with out-of-range values clamped (indent 1..16, items 1..50, blank lines 0..5).</summary>
    public SqlFormatOptions Normalized() => this with
    {
        IndentSize = Math.Clamp(IndentSize, 1, 16),
        MaxItemsPerRow = Math.Clamp(MaxItemsPerRow, 1, 50),
        MaxBlankLines = Math.Clamp(MaxBlankLines, 0, 5),
        BaseIndent = Math.Clamp(BaseIndent, 0, 200),
    };
}

/// <summary>A 1-based, end-exclusive replacement of the original text.</summary>
public sealed record SqlTextEdit(int StartLine, int StartColumn, int EndLine, int EndColumn, string NewText);

/// <summary>
/// The formatter's result. On success <paramref name="Text"/> is the whole formatted document and
/// <paramref name="Edits"/> the minimal edits (only those in the requested range, when one was given). On failure
/// nothing changes: <paramref name="Error"/> says why, with the 1-based position of a syntax error when there is one.
/// </summary>
public sealed record SqlFormatResult(
    bool Success,
    string? Text,
    IReadOnlyList<SqlTextEdit> Edits,
    string? Error = null,
    int? ErrorLine = null,
    int? ErrorColumn = null)
{
    public static SqlFormatResult Fail(string error, int? line = null, int? column = null) => new(false, null, [], error, line, column);
}
