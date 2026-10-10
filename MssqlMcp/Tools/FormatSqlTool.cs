using System.ComponentModel;
using ModelContextProtocol.Server;
using Mssql.McpServer.LanguageService;
using Mssql.McpServer.LanguageService.Formatting;

namespace Mssql.McpServer;

public sealed partial class ScriptRunnerTools
{
    [McpServerTool(
        Name = ToolNames.FormatSql,
        Title = "Format SQL",
        ReadOnly = true,
        Idempotent = true,
        Destructive = false),
        Description("Internal tool of the MSSQL-MCP editor extension: formats T-SQL text (ScriptDom). Needs no connection and reads no database. Returns 1-based edits; with startLine..endColumn only edits in that range. Text that does not parse is refused with the error position.")]
    public DbOperationResult FormatSql(
        [Description("The document text.")] string text,
        [Description("Indent width in columns (the editor's tab size).")] int indentSize = 4,
        [Description("Indent with tabs instead of spaces.")] bool useTabs = false,
        [Description("lower (default), upper or preserve: keywords, system types and built-in functions.")] string keywordCase = "lower",
        [Description("Most items per row of column, value and SET lists.")] int maxItemsPerRow = 4,
        [Description("Range start line (1-based); omit for the whole document.")] int? startLine = null,
        [Description("Range start column (1-based).")] int? startColumn = null,
        [Description("Range end line (1-based).")] int? endLine = null,
        [Description("Range end column (1-based, exclusive).")] int? endColumn = null)
    {
        text ??= string.Empty;
        if (text.Length > MaxLanguageServiceTextLength)
        {
            return new DbOperationResult(success: false, error: $"text is longer than {MaxLanguageServiceTextLength} characters.");
        }

        if (!Enum.TryParse<KeywordCase>(keywordCase, ignoreCase: true, out var casing))
        {
            return new DbOperationResult(success: false, error: "keywordCase must be lower, upper or preserve.");
        }

        TextRange? range = null;
        if (startLine is not null || endLine is not null)
        {
            if (startLine is not >= 1 || endLine is not >= 1 || startColumn is not >= 1 || endColumn is not >= 1)
            {
                return new DbOperationResult(success: false, error: "startLine, startColumn, endLine and endColumn are 1-based and are given together.");
            }

            range = new TextRange(startLine.Value, startColumn.Value, endLine.Value, endColumn.Value);
        }

        var options = new SqlFormatOptions(indentSize, useTabs, casing, maxItemsPerRow);
        var result = SqlFormatter.Format(text, options, range);
        return result.Success
            ? new DbOperationResult(success: true, data: new { edits = result.Edits })
            : new DbOperationResult(success: false, error: result.Error, data: new { errorLine = result.ErrorLine, errorColumn = result.ErrorColumn });
    }
}
