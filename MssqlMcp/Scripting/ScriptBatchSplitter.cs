// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.

using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace Mssql.McpServer.Scripting;

/// <summary>One batch of a script, as SSMS would send it to the server.</summary>
/// <param name="Text">The batch text, exactly as it appears in the script (separator line excluded).</param>
/// <param name="StartLine">1-based script line of the batch's first character; a server error line maps to <c>StartLine + line - 1</c>.</param>
/// <param name="RepeatCount">How many times to run the batch (SSMS <c>GO n</c>); 1 when no count is given.</param>
public sealed record ScriptBatch(string Text, int StartLine, int RepeatCount);

/// <summary>
/// Splits a script on SSMS-style <c>GO</c> separators using the ScriptDom tokenizer, so <c>GO</c> inside strings,
/// comments or bracketed identifiers never splits. Never throws: text the tokenizer cannot read stays in one batch.
/// </summary>
internal static class ScriptBatchSplitter
{
    public static IReadOnlyList<ScriptBatch> Split(string script)
    {
        if (string.IsNullOrEmpty(script))
        {
            return [];
        }

        IList<TSqlParserToken> tokens;
        int parsedEnd;
        try
        {
            tokens = new TSql170Parser(true).GetTokenStream(new StringReader(script), out var errors)
                .Where(static t => t.TokenType != TSqlTokenType.EndOfFile)
                .ToList();

            // The tokenizer stops at the first error: everything from there on stays in the batch it starts in.
            parsedEnd = script.Length;
            if (errors.Count > 0)
            {
                var lastTokenEnd = tokens.Count == 0 ? 0 : tokens[^1].Offset + (tokens[^1].Text?.Length ?? 0);
                parsedEnd = Math.Min(lastTokenEnd, errors.Min(static e => e.Offset));
            }
        }
        catch (Exception)
        {
            tokens = [];
            parsedEnd = 0;
        }

        var batches = new List<ScriptBatch>();
        var lines = new LineCounter(script);
        var batchStart = 0;
        for (var i = 0; i < tokens.Count; i++)
        {
            if (tokens[i].TokenType != TSqlTokenType.Go
                || tokens[i].Offset >= parsedEnd
                || !StartsLine(tokens, i)
                || !TryReadSeparatorTail(tokens, i, script.Length, parsedEnd, out var repeatCount, out var nextStart, out var lastTail))
            {
                continue;
            }

            Add(batches, script, lines, batchStart, LineStartOffset(script, tokens[i].Offset), repeatCount);
            batchStart = nextStart;
            i = lastTail;
        }

        Add(batches, script, lines, batchStart, script.Length, 1);
        return batches;
    }

    /// <summary>True when only whitespace precedes the token on its line.</summary>
    private static bool StartsLine(IList<TSqlParserToken> tokens, int index)
    {
        for (var j = index - 1; j >= 0; j--)
        {
            var t = tokens[j];
            if (t.TokenType != TSqlTokenType.WhiteSpace)
            {
                return false;
            }

            if (t.Text.Contains('\n'))
            {
                return true;
            }
        }

        return true;
    }

    /// <summary>
    /// Validates what follows <c>GO</c> on its line: whitespace, an optional repeat count (n &gt;= 1) and an optional
    /// <c>--</c> comment. Returns where the next batch starts and the index of the last token of the separator line.
    /// </summary>
    private static bool TryReadSeparatorTail(
        IList<TSqlParserToken> tokens, int goIndex, int scriptLength, int parsedEnd,
        out int repeatCount, out int nextStart, out int lastTail)
    {
        repeatCount = 1;
        nextStart = scriptLength;
        lastTail = goIndex;
        var sawCount = false;
        var sawComment = false;
        for (var j = goIndex + 1; j < tokens.Count; j++)
        {
            var t = tokens[j];
            if (t.Offset >= parsedEnd)
            {
                return false;
            }

            lastTail = j;
            switch (t.TokenType)
            {
                case TSqlTokenType.WhiteSpace:
                    var newline = t.Text.IndexOf('\n');
                    if (newline >= 0)
                    {
                        nextStart = t.Offset + newline + 1;
                        return true;
                    }

                    break;
                case TSqlTokenType.Integer when !sawCount && !sawComment:
                    if (!int.TryParse(t.Text, out repeatCount) || repeatCount < 1)
                    {
                        return false;
                    }

                    sawCount = true;
                    break;
                case TSqlTokenType.SingleLineComment when !sawComment:
                    sawComment = true;
                    break;
                default:
                    return false;
            }
        }

        // The separator line runs to the end of the script; it counts only if the tokenizer read all of it.
        return parsedEnd >= scriptLength;
    }

    private static int LineStartOffset(string script, int offset)
    {
        var newline = offset == 0 ? -1 : script.LastIndexOf('\n', offset - 1);
        return newline + 1;
    }

    private static void Add(List<ScriptBatch> batches, string script, LineCounter lines, int start, int end, int repeatCount)
    {
        if (end <= start)
        {
            return;
        }

        var text = script[start..end];
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        batches.Add(new ScriptBatch(text, lines.LineOf(start), repeatCount));
    }

    /// <summary>1-based line numbers for increasing offsets, counted incrementally so long scripts stay linear.</summary>
    private sealed class LineCounter(string script)
    {
        private int _offset;
        private int _line = 1;

        public int LineOf(int offset)
        {
            for (; _offset < offset; _offset++)
            {
                if (script[_offset] == '\n')
                {
                    _line++;
                }
            }

            return _line;
        }
    }
}
