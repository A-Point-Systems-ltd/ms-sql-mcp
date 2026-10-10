using System.Text;
using System.Text.RegularExpressions;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace Mssql.McpServer.LanguageService.Formatting;

/// <summary>
/// The T-SQL formatter behind Format Document / Format Selection / Ctrl+F2. ScriptDom (Microsoft's T-SQL parser)
/// parses the text; <see cref="SqlLayout"/> decides line breaks and indents from its syntax tree; this class rewrites
/// only the whitespace between tokens and the letter case of keywords, system data types and built-in functions.
/// Identifiers, aliases, literals, comments, brackets, semicolons and GO are never changed, added or removed. Before
/// returning, the result is parsed again and compared token by token with the original; any difference other than
/// that letter case fails the request and nothing changes. Text that does not parse is never formatted.
/// </summary>
public static partial class SqlFormatter
{
    private static readonly HashSet<string> SpacedOperators = new(StringComparer.Ordinal)
    {
        "=", "<", ">", "<=", ">=", "<>", "!=", "!<", "!>", "+=", "-=", "*=", "/=", "%=", "&=", "|=", "^=",
    };

    private static readonly HashSet<string> CompoundOperatorStarts = new(StringComparer.Ordinal)
    {
        "<", ">", "!", "+", "-", "*", "/", "%", "&", "|", "^",
    };

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex WordPattern();

    /// <summary>
    /// Formats <paramref name="text"/>. With a <paramref name="range"/> (1-based, end-exclusive) only edits inside it
    /// are returned, widened to whole statements it cuts through; when the whole text does not parse, the selected lines
    /// are formatted on their own if they parse.
    /// </summary>
    public static SqlFormatResult Format(string text, SqlFormatOptions? options = null, TextRange? range = null)
    {
        options = (options ?? new SqlFormatOptions()).Normalized();
        var parsed = Parse(text, out var errors);
        if (errors.Count > 0 || parsed is not TSqlScript script)
        {
            if (range is not null && FormatLinesAlone(text, options, range) is { } alone)
            {
                return alone;
            }

            var e = errors.FirstOrDefault();
            return e is null
                ? SqlFormatResult.Fail("The text could not be parsed as T-SQL; nothing was changed.")
                : SqlFormatResult.Fail($"Not formatted: syntax error at line {e.Line}, column {e.Column}: {e.Message}", e.Line, e.Column);
        }

        var doc = new SqlTokenDoc(text, parsed.ScriptTokenStream);
        if (doc.Count == 0)
        {
            return new SqlFormatResult(true, text, []);
        }

        var layout = new SqlLayout(doc, options);
        layout.Build(script);
        var (gaps, tokens) = Emit(doc, layout, options);
        var output = Join(gaps, tokens);
        if (Verify(doc, tokens, output) is { } problem)
        {
            return SqlFormatResult.Fail($"Not formatted: the result would differ from the original beyond whitespace and letter case ({problem}). Nothing was changed.");
        }

        var (first, last) = range is null ? (0, doc.Count - 1) : SelectedTokens(doc, script, range);
        return new SqlFormatResult(true, output, Edits(doc, gaps, tokens, first, last));
    }

    private static TSqlFragment? Parse(string text, out IList<ParseError> errors)
    {
        var parser = new TSql170Parser(initialQuotedIdentifiers: true);
        using var reader = new StringReader(text);
        return parser.Parse(reader, out errors);
    }

    /// <summary>The selected lines alone (when the whole document does not parse), indented like the first selected line.</summary>
    private static SqlFormatResult? FormatLinesAlone(string text, SqlFormatOptions options, TextRange range)
    {
        var lines = text.Split('\n');
        var startLine = Math.Clamp(range.StartLine, 1, lines.Length);
        var endLine = Math.Clamp(range.EndColumn == 1 && range.EndLine > range.StartLine ? range.EndLine - 1 : range.EndLine, startLine, lines.Length);
        var selected = string.Join('\n', lines[(startLine - 1)..endLine]);
        if (string.IsNullOrWhiteSpace(selected))
        {
            return null;
        }

        _ = Parse(selected, out var errors);
        if (errors.Count > 0)
        {
            return null;
        }

        var firstText = lines[(startLine - 1)..endLine].First(l => !string.IsNullOrWhiteSpace(l));
        var baseIndent = 0;
        foreach (var ch in firstText)
        {
            if (ch == ' ')
            {
                baseIndent++;
            }
            else if (ch == '\t')
            {
                baseIndent = ((baseIndent / options.IndentSize) + 1) * options.IndentSize;
            }
            else
            {
                break;
            }
        }

        var result = Format(selected, options with { BaseIndent = baseIndent });
        if (!result.Success)
        {
            return null;
        }

        var shifted = result.Edits.Select(e => e with { StartLine = e.StartLine + startLine - 1, EndLine = e.EndLine + startLine - 1 }).ToList();
        return result with { Text = null, Edits = shifted };
    }

    private static (string[] Gaps, string[] Tokens) Emit(SqlTokenDoc doc, SqlLayout layout, SqlFormatOptions options)
    {
        var n = doc.Count;
        var gaps = new string[n + 1];
        var tokens = new string[n];
        var outColumn = new int[n];
        var column = 0;
        var lineOut = options.BaseIndent;
        var lineOrig = 0;

        int Resolve(IndentSpec spec) =>
            Math.Max(0, (spec.Anchor >= 0 ? outColumn[spec.Anchor] : options.BaseIndent) + (spec.Units * options.IndentSize) + spec.Columns);

        // The indent token i gets when it starts a line.
        int IndentFor(int i)
        {
            var l = layout.Layouts[i];
            if (l.Kind == LayoutKind.Break)
            {
                return Resolve(l.Indent);
            }

            if (l.Soft is { } soft)
            {
                return Resolve(soft);
            }

            // An original line break kept: the same indent relative to the line above as before.
            return Math.Max(0, lineOut + Math.Max(0, doc.LineIndent(i, options.IndentSize) - lineOrig));
        }

        for (var i = 0; i < n; i++)
        {
            var tok = doc.Tokens[i];
            var l = layout.Layouts[i];
            var newlines = doc.NewlinesBefore(i);
            var forced = i > 0 && doc.Tokens[i - 1].Type == TSqlTokenType.SingleLineComment;
            bool startsLine;
            int indent;
            if (i == 0)
            {
                startsLine = true;
                var next = tok.IsComment ? doc.NextCode(i) : i;
                indent = next >= 0 ? IndentFor(next) : options.BaseIndent;
            }
            else if (tok.IsComment)
            {
                startsLine = newlines > 0 || forced;
                var next = doc.NextCode(i);
                indent = next >= 0 ? IndentFor(next) : lineOut;
            }
            else
            {
                switch (l.Kind)
                {
                    case LayoutKind.Break:
                        startsLine = true;
                        break;
                    case LayoutKind.Join:
                    case LayoutKind.JoinNoSpace:
                        startsLine = forced;
                        break;
                    default:
                        startsLine = newlines > 0 || forced;
                        break;
                }

                indent = startsLine ? IndentFor(i) : 0;
            }

            string gap;
            if (i == 0)
            {
                gap = IndentText(indent, options);
                column = indent;
            }
            else if (startsLine)
            {
                var blank = Math.Min(Math.Max(newlines - 1, 0), options.MaxBlankLines);
                gap = string.Concat(Enumerable.Repeat(doc.NewLine, blank + 1)) + IndentText(indent, options);
                column = indent;
            }
            else
            {
                gap = Spacing(doc.Tokens[i - 1], tok, doc.Gap(i), l.Kind);
                column += gap.Length;
            }

            if (startsLine && !tok.IsComment)
            {
                lineOut = indent;
                lineOrig = doc.LineIndent(i, options.IndentSize);
            }

            gaps[i] = gap;
            tokens[i] = Recase(doc, layout, i, options.KeywordCase);
            outColumn[i] = column;
            var nl = tokens[i].LastIndexOf('\n');
            column = nl >= 0 ? tokens[i].Length - nl - 1 : column + tokens[i].Length;
        }

        gaps[n] = doc.NewlinesBefore(n) > 0 ? doc.NewLine : string.Empty;
        return (gaps, tokens);
    }

    private static string IndentText(int width, SqlFormatOptions options) =>
        options.UseTabs
            ? new string('\t', width / options.IndentSize) + new string(' ', width % options.IndentSize)
            : new string(' ', width);

    /// <summary>The space between two tokens on one line.</summary>
    private static string Spacing(SqlTok prev, SqlTok cur, string originalGap, LayoutKind kind)
    {
        if (kind == LayoutKind.JoinNoSpace)
        {
            return string.Empty;
        }

        if (cur.IsComment || prev.IsComment)
        {
            return " ";
        }

        var p = prev.Text;
        var c = cur.Text;
        if (c is "," or ";" or ")" or "." || p is "." or "(")
        {
            return string.Empty;
        }

        if (p == ",")
        {
            return " ";
        }

        if (SpacedOperators.Contains(c) || SpacedOperators.Contains(p))
        {
            // Never split a compound operator the tokenizer returned as two tokens (< =, ! =).
            return originalGap.Length == 0 && CompoundOperatorStarts.Contains(p) && c is "=" or ">" or "<" ? string.Empty : " ";
        }

        return originalGap.Length > 0 ? " " : string.Empty;
    }

    private static string Recase(SqlTokenDoc doc, SqlLayout layout, int i, KeywordCase keywordCase)
    {
        var tok = doc.Tokens[i];
        if (keywordCase == KeywordCase.Preserve || tok.IsComment
            || tok.Type is TSqlTokenType.QuotedIdentifier or TSqlTokenType.Variable
            || !WordPattern().IsMatch(tok.Text)
            || (layout.IdentifierToken[i] && !layout.ForceRecase[i]))
        {
            return tok.Text;
        }

        return keywordCase == KeywordCase.Upper ? tok.Text.ToUpperInvariant() : tok.Text.ToLowerInvariant();
    }

    private static string Join(string[] gaps, string[] tokens)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < tokens.Length; i++)
        {
            sb.Append(gaps[i]).Append(tokens[i]);
        }

        return sb.Append(gaps[tokens.Length]).ToString();
    }

    /// <summary>Null when <paramref name="output"/> parses to the same tokens as the original (letter case aside); else what differs.</summary>
    private static string? Verify(SqlTokenDoc original, string[] tokens, string output)
    {
        var reparsed = Parse(output, out var errors);
        if (errors.Count > 0 || reparsed is null)
        {
            return "it no longer parses";
        }

        var check = new SqlTokenDoc(output, reparsed.ScriptTokenStream);
        if (check.Count != original.Count)
        {
            return $"{original.Count} tokens became {check.Count}";
        }

        for (var i = 0; i < original.Count; i++)
        {
            var before = original.Tokens[i];
            var after = check.Tokens[i];
            if (before.Type != after.Type
                || !string.Equals(after.Text, tokens[i], StringComparison.Ordinal)
                || !string.Equals(before.Text, after.Text, StringComparison.OrdinalIgnoreCase))
            {
                var (line, column) = original.Position(before.Offset);
                return $"token at line {line}, column {column}";
            }
        }

        return null;
    }

    /// <summary>The token range to edit for a selection: the tokens it touches, widened over statements it cuts through.</summary>
    private static (int First, int Last) SelectedTokens(SqlTokenDoc doc, TSqlScript script, TextRange range)
    {
        var start = doc.Offset(range.StartLine, range.StartColumn);
        var end = doc.Offset(range.EndLine, range.EndColumn);
        var first = -1;
        var last = -1;
        for (var i = 0; i < doc.Count; i++)
        {
            var tok = doc.Tokens[i];
            if (tok.End > start && tok.Offset < Math.Max(end, start + 1))
            {
                if (first < 0)
                {
                    first = i;
                }

                last = i;
            }
        }

        if (first < 0)
        {
            return (0, -1);
        }

        var statements = new List<(int First, int Last)>();
        script.Accept(new StatementCollector(doc, statements));
        // Every statement the selection touches is widened to in full, unless a statement inside it is touched too
        // (selecting lines in a procedure body formats those statements, not the whole procedure).
        bool changed;
        do
        {
            changed = false;
            foreach (var (f, l) in statements)
            {
                if (f > last || l < first || (f >= first && l <= last))
                {
                    continue;
                }

                var (lo, hi) = (first, last);
                var touchesInner = statements.Any(t => t != (f, l) && t.First >= f && t.Last <= l && t.First <= hi && t.Last >= lo);
                if (!touchesInner)
                {
                    first = Math.Min(first, f);
                    last = Math.Max(last, l);
                    changed = true;
                }
            }
        }
        while (changed);

        return (first, last);
    }

    private static List<SqlTextEdit> Edits(SqlTokenDoc doc, string[] gaps, string[] tokens, int first, int last)
    {
        var edits = new List<SqlTextEdit>();
        if (first > last)
        {
            return edits;
        }

        void Add(int startOffset, int endOffset, string newText)
        {
            var (sl, sc) = doc.Position(startOffset);
            var (el, ec) = doc.Position(endOffset);
            edits.Add(new SqlTextEdit(sl, sc, el, ec, newText));
        }

        for (var i = first; i <= last + 1 && i <= doc.Count; i++)
        {
            var isWhole = first == 0 && last == doc.Count - 1;
            if (i == last + 1 && !isWhole)
            {
                break;
            }

            var gapEnd = i == doc.Count ? doc.Text.Length : doc.Tokens[i].Offset;
            if (!string.Equals(gaps[i], doc.Gap(i), StringComparison.Ordinal))
            {
                Add(doc.GapStart(i), gapEnd, gaps[i]);
            }

            if (i < doc.Count && i <= last && !string.Equals(tokens[i], doc.Tokens[i].Text, StringComparison.Ordinal))
            {
                Add(doc.Tokens[i].Offset, doc.Tokens[i].End, tokens[i]);
            }
        }

        return edits;
    }

    private sealed class StatementCollector(SqlTokenDoc doc, List<(int, int)> statements) : TSqlFragmentVisitor
    {
        public override void Visit(TSqlStatement node) => statements.Add((doc.First(node), doc.Last(node)));
    }
}
