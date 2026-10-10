using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace Mssql.McpServer.LanguageService;

/// <summary>Where the caret is, for the enhanced completions.</summary>
public enum CaretContext
{
    Other,

    /// <summary>Right after JOIN / APPLY: a table to join (FK and name-match suggestions).</summary>
    JoinTable,

    /// <summary>After FROM (or a comma in a FROM list): a table name, completed with a generated alias.</summary>
    FromTable,

    /// <summary>Right after a join's ON: a join condition.</summary>
    OnCondition,

    /// <summary>In a SELECT list: the column picker is offered.</summary>
    SelectList,
}

/// <summary>A table source of the statement: <paramref name="Schema"/> may be null; <paramref name="Alias"/> is null when none was written.</summary>
public sealed record ScopeTable(string? Schema, string Name, string? Alias, bool IsVariable, int Position)
{
    /// <summary>How columns of this source are qualified in SQL: its alias, else its name.</summary>
    public string Qualifier => Alias ?? Name;
}

/// <summary>The caret context, the statement's table sources, and for ON the table just joined.</summary>
public sealed record SqlScopeInfo(CaretContext Context, IReadOnlyList<ScopeTable> Tables, ScopeTable? JoinedTable, bool AliasFollows);

/// <summary>
/// A tokenizer-only reading of the statement around the caret (ScriptDom's lexer: it works on unfinished SQL, unlike the
/// parser). Finds the caret context and the table sources (FROM / JOIN / APPLY / UPDATE targets with their aliases) of
/// the innermost statement or subquery that holds the caret. Approximate by design: it never needs the SQL to be valid.
/// </summary>
public static partial class SqlScope
{
    private static readonly HashSet<string> StatementStarts = new(StringComparer.OrdinalIgnoreCase)
    {
        "select", "insert", "update", "delete", "merge", "declare", "if", "while", "return", "exec", "execute", "print",
        "create", "alter", "drop", "truncate", "use", "open", "fetch", "close", "deallocate", "raiserror", "throw",
        "commit", "rollback", "go", "waitfor", "goto", "break", "continue",
    };

    private static readonly HashSet<string> SetOptions = new(StringComparer.OrdinalIgnoreCase)
    {
        "nocount", "xact_abort", "ansi_nulls", "quoted_identifier", "transaction", "identity_insert", "rowcount",
        "datefirst", "dateformat", "language", "deadlock_priority", "lock_timeout", "statistics", "noexec", "arithabort",
        "concat_null_yields_null", "ansi_warnings", "ansi_padding", "numeric_roundabort", "context_info",
        "implicit_transactions", "fmtonly", "showplan_xml", "showplan_all", "showplan_text", "parseonly",
    };

    /// <summary>Tokens that end a table source's alias position (never taken as an alias).</summary>
    private static readonly HashSet<string> NotAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        "apply", "cross", "outer", "inner", "left", "right", "full", "join", "on", "where", "group", "order", "having",
        "with", "union", "except", "intersect", "for", "option", "set", "output", "pivot", "unpivot", "tablesample",
        "when", "then", "else", "end", "select", "from", "into", "values", "go", "as",
    };

    /// <summary>Reads the scope at a 1-based line and column.</summary>
    public static SqlScopeInfo Analyze(string text, int line, int column)
    {
        var caret = OffsetOf(text, line, column);
        // Only the GO batch around the caret (capped) is read, so the cost does not grow with the document.
        var (start, end) = Window(text, caret);
        text = text[start..end];
        caret -= start;
        var parser = new TSql170Parser(initialQuotedIdentifiers: true);
        using var reader = new StringReader(text);
        var stream = parser.GetTokenStream(reader, out _);
        var tokens = new List<TSqlParserToken>();
        var inCommentOrString = false;
        foreach (var t in stream)
        {
            if (string.IsNullOrEmpty(t.Text) || t.TokenType is TSqlTokenType.WhiteSpace or TSqlTokenType.EndOfFile)
            {
                continue;
            }

            var isComment = t.TokenType is TSqlTokenType.SingleLineComment or TSqlTokenType.MultilineComment;
            var isString = t.TokenType is TSqlTokenType.AsciiStringLiteral or TSqlTokenType.UnicodeStringLiteral;
            if ((isComment && t.Offset < caret && caret <= End(t)) || (isString && t.Offset < caret && caret < End(t)))
            {
                inCommentOrString = true;
            }

            if (!isComment)
            {
                tokens.Add(t);
            }
        }

        var info = Analyze(tokens, caret);
        return inCommentOrString ? info with { Context = CaretContext.Other } : info;
    }

    /// <summary>Most characters read on each side of the caret.</summary>
    internal const int MaxWindow = 64 * 1024;

    /// <summary>
    /// The GO batch that holds the caret, at most <see cref="MaxWindow"/> characters on each side, cut at line starts.
    /// A GO inside a string or comment is taken as a separator too: the scope is approximate by design.
    /// </summary>
    internal static (int Start, int End) Window(string text, int caret)
    {
        var start = 0;
        var end = text.Length;
        foreach (System.Text.RegularExpressions.Match m in GoLine().Matches(text))
        {
            if (m.Index + m.Length <= caret)
            {
                start = m.Index + m.Length;
                start += start < text.Length && text[start] == '\r' ? 1 : 0;
                start += start < text.Length && text[start] == '\n' ? 1 : 0;
            }
            else if (m.Index >= caret)
            {
                end = m.Index;
                break;
            }
        }

        if (caret - start > MaxWindow)
        {
            var cut = text.IndexOf('\n', caret - MaxWindow);
            start = cut >= 0 && cut < caret ? cut + 1 : caret - MaxWindow;
        }

        if (end - caret > MaxWindow)
        {
            var cut = text.LastIndexOf('\n', caret + MaxWindow);
            end = cut > caret ? cut : caret + MaxWindow;
        }

        return (start, end);
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^[ \t]*go[ \t]*(?:\d+[ \t]*)?(?:--[^\n]*)?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Multiline)]
    private static partial System.Text.RegularExpressions.Regex GoLine();

    internal static int OffsetOf(string text, int line, int column)
    {
        var offset = 0;
        for (var l = 1; l < line && offset < text.Length; l++)
        {
            var nl = text.IndexOf('\n', offset);
            if (nl < 0)
            {
                return text.Length;
            }

            offset = nl + 1;
        }

        return Math.Min(text.Length, offset + Math.Max(0, column - 1));
    }

    private static SqlScopeInfo Analyze(List<TSqlParserToken> tokens, int caret)
    {
        // Tokens wholly before the caret; a word ending at the caret is the one being typed.
        var before = 0;
        while (before < tokens.Count && tokens[before].Offset + tokens[before].Text.Length <= caret)
        {
            before++;
        }

        var typed = before;
        if (typed > 0 && End(tokens[typed - 1]) == caret && (IsNamePart(tokens[typed - 1]) || tokens[typed - 1].TokenType == TSqlTokenType.Dot))
        {
            // Back over the name being typed and its qualifiers (dbo.Bu, dbo.).
            while (typed > 0 && (IsNamePart(tokens[typed - 1]) || tokens[typed - 1].TokenType == TSqlTokenType.Dot)
                && (typed == before || tokens[typed].TokenType == TSqlTokenType.Dot || tokens[typed - 1].TokenType == TSqlTokenType.Dot))
            {
                typed--;
            }
        }

        var (start, end) = Region(tokens, typed);
        var tables = Sources(tokens, start, end);
        var prev = typed > 0 ? tokens[typed - 1] : null;
        var context = CaretContext.Other;
        ScopeTable? joined = null;
        if (prev is not null)
        {
            if (prev.TokenType == TSqlTokenType.Join || Is(prev, "apply"))
            {
                context = CaretContext.JoinTable;
            }
            else if (prev.TokenType == TSqlTokenType.From)
            {
                // DELETE FROM t and BULK INSERT t FROM 'file' take no alias.
                context = NoAliasFrom(tokens, typed - 1) ? CaretContext.Other : CaretContext.FromTable;
            }
            else if (prev.TokenType == TSqlTokenType.On)
            {
                joined = tables.LastOrDefault(t => t.Position < typed - 1);
                if (joined is not null && JoinBefore(tokens, start, typed - 1))
                {
                    context = CaretContext.OnCondition;
                }
            }
            else if (InSelectList(tokens, start, typed))
            {
                context = CaretContext.SelectList;
            }
        }

        // An alias already written after the name being typed (FROM Bui|ldings B): no generated alias then.
        var next = before < tokens.Count ? tokens[before] : null;
        if (next is not null && next.Offset == caret && IsNamePart(next))
        {
            next = before + 1 < tokens.Count ? tokens[before + 1] : null;
        }

        var aliasFollows = next is not null && (next.TokenType == TSqlTokenType.As || (IsNamePart(next) && !NotAliases.Contains(next.Text)));
        return new SqlScopeInfo(context, tables, joined, aliasFollows);
    }

    private static int End(TSqlParserToken t) => t.Offset + t.Text.Length;

    /// <summary>True for the FROM of DELETE FROM and of BULK INSERT name FROM.</summary>
    private static bool NoAliasFrom(List<TSqlParserToken> tokens, int from)
    {
        var before = from - 1;
        if (before >= 0 && tokens[before].TokenType == TSqlTokenType.Delete)
        {
            return true;
        }

        var i = before;
        while (i >= 0 && (IsNamePart(tokens[i]) || tokens[i].TokenType == TSqlTokenType.Dot))
        {
            i--;
        }

        return i >= 1 && i < before && tokens[i].TokenType == TSqlTokenType.Insert && Is(tokens[i - 1], "bulk");
    }

    private static bool Is(TSqlParserToken t, string word) => string.Equals(t.Text, word, StringComparison.OrdinalIgnoreCase);

    private static bool IsNamePart(TSqlParserToken t) => t.TokenType is TSqlTokenType.Identifier or TSqlTokenType.QuotedIdentifier;

    private static bool IsStop(List<TSqlParserToken> tokens, int i)
    {
        var t = tokens[i];
        if (t.TokenType is TSqlTokenType.Semicolon or TSqlTokenType.Go)
        {
            return true;
        }

        if (Is(t, "set"))
        {
            // SET @x / SET NOCOUNT ON start statements; UPDATE ... SET does not.
            var n = i + 1 < tokens.Count ? tokens[i + 1] : null;
            return n is not null && (n.TokenType == TSqlTokenType.Variable || SetOptions.Contains(n.Text));
        }

        return StatementStarts.Contains(t.Text) && t.TokenType is not (TSqlTokenType.QuotedIdentifier or TSqlTokenType.Variable);
    }

    /// <summary>The token range [start, end) of the statement or subquery that holds token index <paramref name="at"/>.</summary>
    private static (int Start, int End) Region(List<TSqlParserToken> tokens, int at)
    {
        var start = 0;
        var depth = 0;
        for (var i = at - 1; i >= 0; i--)
        {
            var t = tokens[i];
            if (t.TokenType == TSqlTokenType.RightParenthesis)
            {
                depth++;
                continue;
            }

            if (t.TokenType == TSqlTokenType.LeftParenthesis)
            {
                if (depth == 0)
                {
                    start = i + 1;
                    break;
                }

                depth--;
                continue;
            }

            if (depth == 0 && IsStop(tokens, i))
            {
                start = t.TokenType is TSqlTokenType.Semicolon or TSqlTokenType.Go ? i + 1 : i;
                break;
            }
        }

        var end = tokens.Count;
        depth = 0;
        for (var i = Math.Max(at, start + 1); i < tokens.Count; i++)
        {
            var t = tokens[i];
            if (t.TokenType == TSqlTokenType.LeftParenthesis)
            {
                depth++;
                continue;
            }

            if (t.TokenType == TSqlTokenType.RightParenthesis)
            {
                if (depth == 0)
                {
                    end = i;
                    break;
                }

                depth--;
                continue;
            }

            if (depth == 0 && i > at && IsStop(tokens, i))
            {
                end = i;
                break;
            }
        }

        return (start, end);
    }

    /// <summary>Table sources at depth 0 of [start, end): after FROM, JOIN, APPLY, UPDATE and commas of a FROM list.</summary>
    private static List<ScopeTable> Sources(List<TSqlParserToken> tokens, int start, int end)
    {
        var result = new List<ScopeTable>();
        var depth = 0;
        var inFrom = false;
        for (var i = start; i < end; i++)
        {
            var t = tokens[i];
            if (t.TokenType == TSqlTokenType.LeftParenthesis)
            {
                depth++;
                continue;
            }

            if (t.TokenType == TSqlTokenType.RightParenthesis)
            {
                depth = Math.Max(0, depth - 1);
                continue;
            }

            if (depth > 0)
            {
                continue;
            }

            if (t.TokenType is TSqlTokenType.Where or TSqlTokenType.Group or TSqlTokenType.Order or TSqlTokenType.Having or TSqlTokenType.Set)
            {
                inFrom = false;
                continue;
            }

            var startsSource = t.TokenType is TSqlTokenType.From or TSqlTokenType.Join
                || Is(t, "apply")
                || (t.TokenType == TSqlTokenType.Update && i + 1 < end && tokens[i + 1].TokenType != TSqlTokenType.LeftParenthesis)
                || (inFrom && t.TokenType == TSqlTokenType.Comma);
            if (!startsSource)
            {
                continue;
            }

            if (t.TokenType is TSqlTokenType.From or TSqlTokenType.Join || Is(t, "apply"))
            {
                inFrom = true;
            }

            var (source, next) = ReadSource(tokens, i + 1, end);
            if (source is not null)
            {
                result.Add(source);
            }

            i = next - 1;
        }

        // UPDATE t ... FROM dbo.T t names the same source twice: keep the one with the full name.
        return [.. result
            .GroupBy(s => (s.Alias ?? s.Name).ToUpperInvariant())
            .Select(g => g.OrderByDescending(s => s.Schema is not null).ThenByDescending(s => s.Alias is not null).First())
            .OrderBy(s => s.Position)];
    }

    /// <summary>One table source at <paramref name="i"/>: a (multi-part) name or variable, TVF arguments, then [AS] alias.</summary>
    private static (ScopeTable? Source, int Next) ReadSource(List<TSqlParserToken> tokens, int i, int end)
    {
        if (i >= end)
        {
            return (null, i);
        }

        var position = i;
        var parts = new List<string>();
        var isVariable = false;
        if (tokens[i].TokenType == TSqlTokenType.Variable)
        {
            parts.Add(tokens[i].Text);
            isVariable = true;
            i++;
        }
        else if (tokens[i].TokenType == TSqlTokenType.LeftParenthesis)
        {
            // A derived table: skip it; only its alias is known.
            var depth = 0;
            for (; i < end; i++)
            {
                if (tokens[i].TokenType == TSqlTokenType.LeftParenthesis)
                {
                    depth++;
                }
                else if (tokens[i].TokenType == TSqlTokenType.RightParenthesis && --depth == 0)
                {
                    i++;
                    break;
                }
            }

            parts.Add(string.Empty);
        }
        else
        {
            while (i < end && IsNamePart(tokens[i]))
            {
                parts.Add(Unquote(tokens[i].Text));
                i++;
                if (i < end && tokens[i].TokenType == TSqlTokenType.Dot)
                {
                    i++;
                    while (i < end && tokens[i].TokenType == TSqlTokenType.Dot)
                    {
                        parts.Add(string.Empty);
                        i++;
                    }

                    continue;
                }

                break;
            }

            if (parts.Count == 0)
            {
                return (null, i);
            }

            if (i < end && tokens[i].TokenType == TSqlTokenType.LeftParenthesis)
            {
                // TVF arguments.
                var depth = 0;
                for (; i < end; i++)
                {
                    if (tokens[i].TokenType == TSqlTokenType.LeftParenthesis)
                    {
                        depth++;
                    }
                    else if (tokens[i].TokenType == TSqlTokenType.RightParenthesis && --depth == 0)
                    {
                        i++;
                        break;
                    }
                }
            }
        }

        string? alias = null;
        if (i < end && tokens[i].TokenType == TSqlTokenType.As)
        {
            i++;
        }

        if (i < end && IsNamePart(tokens[i]) && !NotAliases.Contains(tokens[i].Text))
        {
            alias = Unquote(tokens[i].Text);
            i++;
        }

        var name = parts[^1];
        if (name.Length == 0 && alias is null)
        {
            return (null, i);
        }

        var schema = parts.Count >= 2 && parts[^2].Length > 0 ? parts[^2] : null;
        return (new ScopeTable(schema, name.Length == 0 ? alias! : name, alias, isVariable || name.Length == 0, position), i);
    }

    private static string Unquote(string text) =>
        text.Length >= 2 && text[0] == '[' && text[^1] == ']' ? text[1..^1].Replace("]]", "]", StringComparison.Ordinal)
        : text.Length >= 2 && text[0] == '"' && text[^1] == '"' ? text[1..^1].Replace("\"\"", "\"", StringComparison.Ordinal)
        : text;

    /// <summary>True when the ON at <paramref name="on"/> follows a JOIN of the same FROM clause.</summary>
    private static bool JoinBefore(List<TSqlParserToken> tokens, int start, int on)
    {
        var depth = 0;
        for (var i = on - 1; i >= start; i--)
        {
            var t = tokens[i];
            if (t.TokenType == TSqlTokenType.RightParenthesis)
            {
                depth++;
            }
            else if (t.TokenType == TSqlTokenType.LeftParenthesis)
            {
                depth--;
            }
            else if (depth == 0 && t.TokenType == TSqlTokenType.Join)
            {
                return true;
            }
            else if (depth == 0 && t.TokenType is TSqlTokenType.From or TSqlTokenType.On)
            {
                return false;
            }
        }

        return false;
    }

    /// <summary>True when the caret follows SELECT (and maybe items) with no FROM / INTO yet at depth 0.</summary>
    private static bool InSelectList(List<TSqlParserToken> tokens, int start, int at)
    {
        var depth = 0;
        for (var i = at - 1; i >= start; i--)
        {
            var t = tokens[i];
            if (t.TokenType == TSqlTokenType.RightParenthesis)
            {
                depth++;
                continue;
            }

            if (t.TokenType == TSqlTokenType.LeftParenthesis)
            {
                depth--;
                continue;
            }

            if (depth != 0)
            {
                continue;
            }

            if (t.TokenType == TSqlTokenType.Select)
            {
                // Right after SELECT, or after a comma ending an item.
                var last = tokens[at - 1];
                return last.TokenType is TSqlTokenType.Select or TSqlTokenType.Comma or TSqlTokenType.Distinct
                    || (last.TokenType == TSqlTokenType.RightParenthesis && i + 1 < at && tokens[i + 1].TokenType == TSqlTokenType.Top);
            }

            if (t.TokenType is TSqlTokenType.From or TSqlTokenType.Into or TSqlTokenType.Where or TSqlTokenType.Set)
            {
                return false;
            }
        }

        return false;
    }
}
