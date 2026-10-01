// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.

using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace Mssql.McpServer.Scripting;

/// <summary>
/// Rewrites the leading CREATE / CREATE OR ALTER / ALTER keyword of a stored module definition.
/// Uses the T-SQL tokenizer so comments and string literals are never touched.
/// </summary>
internal static class ModuleFormRewriter
{
    public static DdlForm ProgrammableFormFor(SqlServerVersion version) =>
        version.SupportsCreateOrAlter ? DdlForm.CreateOrAlter : DdlForm.Alter;

    public static string Rewrite(string definition, DdlForm target, bool quotedIdentifier, out string? warning)
    {
        warning = null;
        var parser = new TSql170Parser(quotedIdentifier);
        var tokens = parser.GetTokenStream(new StringReader(definition), out _)
            .Where(static t => t.TokenType is not (TSqlTokenType.WhiteSpace or TSqlTokenType.SingleLineComment or TSqlTokenType.MultilineComment))
            .Take(3)
            .ToList();

        if (tokens.Count == 0 || tokens[0].TokenType is not (TSqlTokenType.Create or TSqlTokenType.Alter))
        {
            warning = "Definition does not start with CREATE/ALTER; returned unchanged.";
            return definition;
        }

        var start = tokens[0].Offset;
        var end = start + tokens[0].Text.Length;
        if (tokens[0].TokenType == TSqlTokenType.Create && tokens.Count == 3
            && tokens[1].TokenType == TSqlTokenType.Or && tokens[2].TokenType == TSqlTokenType.Alter)
        {
            end = tokens[2].Offset + tokens[2].Text.Length;
        }

        var keyword = target switch
        {
            DdlForm.CreateOrAlter => "CREATE OR ALTER",
            DdlForm.Alter => "ALTER",
            _ => "CREATE",
        };

        // Keep a stored CREATE untouched when the target is Create (as-stored).
        if (target == DdlForm.Create && tokens[0].TokenType == TSqlTokenType.Create && end == start + tokens[0].Text.Length)
        {
            return definition;
        }

        return string.Concat(definition.AsSpan(0, start), keyword, definition.AsSpan(end));
    }
}
