using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace Mssql.McpServer.LanguageService.Formatting;

/// <summary>One non-whitespace token (code or comment) of the original text.</summary>
internal sealed record SqlTok(int StreamIndex, TSqlTokenType Type, string Text, int Offset)
{
    public int End => Offset + Text.Length;

    public bool IsComment => Type is TSqlTokenType.SingleLineComment or TSqlTokenType.MultilineComment;

    public bool Is(string text) => string.Equals(Text, text, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// The original text as ScriptDom tokens without whitespace: <see cref="Tokens"/> (code and comments) and the
/// whitespace between them (<see cref="Gap"/>). Maps ScriptDom stream indices (fragment token ranges) to token indices.
/// </summary>
internal sealed class SqlTokenDoc
{
    private readonly int[] _codeIndexAtOrAfter;
    private readonly int[] _lineStarts;

    public SqlTokenDoc(string text, IList<TSqlParserToken> stream)
    {
        Text = text;
        var tokens = new List<SqlTok>();
        _codeIndexAtOrAfter = new int[stream.Count + 1];
        for (var i = 0; i < stream.Count; i++)
        {
            var t = stream[i];
            if (t.TokenType is TSqlTokenType.WhiteSpace or TSqlTokenType.EndOfFile || string.IsNullOrEmpty(t.Text))
            {
                continue;
            }

            tokens.Add(new SqlTok(i, t.TokenType, t.Text, t.Offset));
        }

        Tokens = tokens;
        var next = tokens.Count;
        for (int i = stream.Count, k = tokens.Count - 1; i >= 0; i--)
        {
            while (k >= 0 && tokens[k].StreamIndex >= i)
            {
                next = k;
                k--;
            }

            _codeIndexAtOrAfter[i] = next;
        }

        var starts = new List<int> { 0 };
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                starts.Add(i + 1);
            }
        }

        _lineStarts = [.. starts];
        NewLine = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
    }

    public string Text { get; }

    public IReadOnlyList<SqlTok> Tokens { get; }

    public int Count => Tokens.Count;

    /// <summary>The document's line break: CRLF when it has any, else LF.</summary>
    public string NewLine { get; }

    /// <summary>The whitespace before token <paramref name="i"/> (<paramref name="i"/> == Count: after the last token).</summary>
    public string Gap(int i)
    {
        var start = i == 0 ? 0 : Tokens[i - 1].End;
        var end = i == Count ? Text.Length : Tokens[i].Offset;
        return Text[start..end];
    }

    public int GapStart(int i) => i == 0 ? 0 : Tokens[i - 1].End;

    /// <summary>Line breaks in the gap before token <paramref name="i"/>.</summary>
    public int NewlinesBefore(int i)
    {
        var start = GapStart(i);
        var end = i == Count ? Text.Length : Tokens[i].Offset;
        var n = 0;
        for (var p = start; p < end; p++)
        {
            if (Text[p] == '\n')
            {
                n++;
            }
        }

        return n;
    }

    /// <summary>The token index of a fragment's first token.</summary>
    public int First(TSqlFragment fragment) => Map(fragment.FirstTokenIndex);

    /// <summary>The token index of a fragment's last token.</summary>
    public int Last(TSqlFragment fragment)
    {
        var i = fragment.LastTokenIndex;
        // The last token is never whitespace, but be safe: walk back to the last real token at or before it.
        var k = Map(i);
        if (k < Count && Tokens[k].StreamIndex == i)
        {
            return k;
        }

        return Math.Max(0, k - 1);
    }

    /// <summary>The token index at or after a ScriptDom stream index.</summary>
    public int Map(int streamIndex) => streamIndex < 0 ? 0 : _codeIndexAtOrAfter[Math.Min(streamIndex, _codeIndexAtOrAfter.Length - 1)];

    /// <summary>The next code (non-comment) token after <paramref name="i"/>, or -1.</summary>
    public int NextCode(int i)
    {
        for (var k = i + 1; k < Count; k++)
        {
            if (!Tokens[k].IsComment)
            {
                return k;
            }
        }

        return -1;
    }

    /// <summary>The previous code (non-comment) token before <paramref name="i"/>, or -1.</summary>
    public int PrevCode(int i)
    {
        for (var k = i - 1; k >= 0; k--)
        {
            if (!Tokens[k].IsComment)
            {
                return k;
            }
        }

        return -1;
    }

    /// <summary>True when the original text between tokens <paramref name="from"/> and <paramref name="to"/> (inclusive) spans lines.</summary>
    public bool SpansLines(int from, int to)
    {
        if (from < 0 || to >= Count || from >= to)
        {
            return false;
        }

        return Text.AsSpan(Tokens[from].Offset, Tokens[to].End - Tokens[from].Offset).Contains('\n');
    }

    /// <summary>1-based line and column of a text offset.</summary>
    public (int Line, int Column) Position(int offset)
    {
        var idx = Array.BinarySearch(_lineStarts, offset);
        var line = idx >= 0 ? idx : ~idx - 1;
        return (line + 1, offset - _lineStarts[line] + 1);
    }

    /// <summary>The text offset of a 1-based line and column (clamped to the text).</summary>
    public int Offset(int line, int column)
    {
        if (line < 1)
        {
            return 0;
        }

        if (line > _lineStarts.Length)
        {
            return Text.Length;
        }

        var start = _lineStarts[line - 1];
        var lineEnd = line < _lineStarts.Length ? _lineStarts[line] : Text.Length;
        return Math.Min(start + Math.Max(0, column - 1), lineEnd);
    }

    /// <summary>The visual indent (tabs expanded) of the original line that holds token <paramref name="i"/>.</summary>
    public int LineIndent(int i, int tabSize)
    {
        var (line, _) = Position(Tokens[i].Offset);
        var p = _lineStarts[line - 1];
        var width = 0;
        while (p < Text.Length && Text[p] is ' ' or '\t')
        {
            width = Text[p] == '\t' ? ((width / tabSize) + 1) * tabSize : width + 1;
            p++;
        }

        return width;
    }
}
