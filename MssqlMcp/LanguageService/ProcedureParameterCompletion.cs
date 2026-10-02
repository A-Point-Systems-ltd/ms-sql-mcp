using System.Globalization;
using Babel;

namespace Mssql.McpServer.LanguageService;

/// <summary>
/// Procedure parameter completion after <c>EXEC proc</c>. SqlParser's FindCompletions never offers a procedure's
/// parameters there (sqltoolsservice shows them only as signature help), so this adds them from <c>Resolver.FindMethods</c>
/// at the caret. A small token scan of the current EXEC statement finds the arguments already given: named ones
/// (<c>@x = ...</c>) and leading positional ones are not offered again. This is our code, not ported.
/// </summary>
internal static class ProcedureParameterCompletion
{
    // Statement starters that end an EXEC argument list when scanning forward past the caret.
    private static readonly HashSet<string> StatementStarters = new(StringComparer.OrdinalIgnoreCase)
    {
        "SELECT", "INSERT", "UPDATE", "DELETE", "MERGE", "EXEC", "EXECUTE", "DECLARE", "SET", "IF", "ELSE", "WHILE",
        "BEGIN", "END", "RETURN", "CREATE", "ALTER", "DROP", "TRUNCATE", "WITH", "PRINT", "RAISERROR", "THROW", "USE", "GO",
    };

    /// <summary>
    /// The parameter items for the caret, or none when the caret is not in an EXEC argument list (or is in a value).
    /// <paramref name="line"/> and <paramref name="column"/> are 1-based.
    /// </summary>
    public static List<CompletionItemInfo> Create(string text, int line, int column, IReadOnlyList<MethodHelpText>? methods)
    {
        var items = new List<CompletionItemInfo>();
        if (methods is not { Count: > 0 } || !TryGetOffset(text, line, column, out var caret))
        {
            return items;
        }

        var context = ScanExecArguments(text, caret);
        if (context is null)
        {
            return items;
        }

        var (named, positional) = context.Value;
        var parameters = methods[0].Parameters;
        var seen = new HashSet<string>(named, StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < parameters.Count; i++)
        {
            var (name, detail) = Split(parameters[i].Display);
            if (name is null || i < positional || !seen.Add(name))
            {
                continue;
            }

            items.Add(new CompletionItemInfo(
                name, CompletionKinds.Parameter, detail, name + " = ",
                CompletionConverter.SortText(0, i.ToString("D3", CultureInfo.InvariantCulture))));
        }

        return items;
    }

    /// <summary>"@x int OUTPUT" → ("@x", "int OUTPUT"); a display that does not start with '@' gives no name.</summary>
    internal static (string? Name, string? Detail) Split(string? display)
    {
        var trimmed = display?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed[0] != '@')
        {
            return (null, null);
        }

        var space = trimmed.IndexOfAny([' ', '\t']);
        return space < 0 ? (trimmed, null) : (trimmed[..space], trimmed[(space + 1)..].Trim());
    }

    /// <summary>
    /// When the caret is in the argument list of the EXEC statement around it, the named arguments already given and
    /// the number of leading positional ones; null when it is not, or when the caret is in an argument's value.
    /// </summary>
    internal static (List<string> Named, int Positional)? ScanExecArguments(string text, int caret)
    {
        var tokens = Tokenize(text);

        // The last EXEC/EXECUTE before the caret, with no ';' between them.
        var exec = -1;
        for (var i = 0; i < tokens.Count && tokens[i].Start < caret; i++)
        {
            if (tokens[i].Text == ";")
            {
                exec = -1;
            }
            else if (tokens[i].IsWord
                     && (tokens[i].Text.Equals("EXEC", StringComparison.OrdinalIgnoreCase)
                         || tokens[i].Text.Equals("EXECUTE", StringComparison.OrdinalIgnoreCase)))
            {
                exec = i;
            }
        }

        if (exec < 0)
        {
            return null;
        }

        // Optional "@ret =", then the (dotted) procedure name.
        var p = exec + 1;
        if (p + 1 < tokens.Count && tokens[p].Text.StartsWith('@') && tokens[p + 1].Text == "=")
        {
            p += 2;
        }

        if (p >= tokens.Count || !tokens[p].IsWord || tokens[p].Text.StartsWith('@'))
        {
            return null;
        }

        while (p + 2 < tokens.Count && tokens[p + 1].Text == "." && tokens[p + 2].IsWord)
        {
            p += 2;
        }

        // The caret must be past the name, after some whitespace (not still typing the name).
        if (caret <= tokens[p].End)
        {
            return null;
        }

        // Split the rest of the statement into arguments at top-level commas.
        var arguments = new List<List<Token>> { new() };
        var currentArgument = -1;
        var depth = 0;
        for (var i = p + 1; i < tokens.Count; i++)
        {
            var t = tokens[i];
            if (t.Text == ";" || (depth == 0 && t.IsWord && StatementStarters.Contains(t.Text)))
            {
                break;
            }

            if (currentArgument < 0 && t.Start >= caret)
            {
                currentArgument = arguments.Count - 1;
            }

            switch (t.Text)
            {
                case "(":
                    depth++;
                    break;
                case ")":
                    depth--;
                    break;
                case "," when depth == 0:
                    arguments.Add([]);
                    continue;
            }

            arguments[^1].Add(t);
        }

        // A caret after the last token is in the last argument.
        if (currentArgument < 0)
        {
            currentArgument = arguments.Count - 1;
        }

        var named = new List<string>();
        var positional = 0;
        var positionalRun = true;
        for (var a = 0; a < arguments.Count; a++)
        {
            var arg = arguments[a];
            var isNamed = arg.Count >= 2 && arg[0].Text.StartsWith('@') && arg[1].Text == "=";
            if (a == currentArgument)
            {
                // "@x = |" is a value position: no parameter list there.
                if (isNamed && arg[1].Start < caret)
                {
                    return null;
                }

                positionalRun = false;
                continue;
            }

            if (isNamed)
            {
                named.Add(arg[0].Text);
                positionalRun = false;
            }
            else if (arg.Count > 0 && positionalRun)
            {
                positional++;
            }
        }

        return (named, positional);
    }

    private static bool TryGetOffset(string text, int line, int column, out int offset)
    {
        offset = 0;
        for (var l = 1; l < line; l++)
        {
            var next = text.IndexOf('\n', offset);
            if (next < 0)
            {
                return false;
            }

            offset = next + 1;
        }

        offset += column - 1;
        return offset >= 0 && offset <= text.Length;
    }

    private readonly record struct Token(string Text, int Start, int End, bool IsWord);

    /// <summary>Words (identifiers, @variables, numbers), bracketed or quoted names, and single punctuation; skips strings and comments.</summary>
    private static List<Token> Tokenize(string text)
    {
        var tokens = new List<Token>();
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
            }
            else if (c == '-' && i + 1 < text.Length && text[i + 1] == '-')
            {
                var eol = text.IndexOf('\n', i);
                i = eol < 0 ? text.Length : eol + 1;
            }
            else if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                var close = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = close < 0 ? text.Length : close + 2;
            }
            else if (c == '\'' || ((c is 'N' or 'n') && i + 1 < text.Length && text[i + 1] == '\''))
            {
                var start = i;
                i = SkipQuoted(text, c == '\'' ? i : i + 1, '\'');
                tokens.Add(new Token("'", start, i, IsWord: false));
            }
            else if (c is '[' or '"')
            {
                var start = i;
                i = SkipQuoted(text, i, c == '[' ? ']' : '"');
                tokens.Add(new Token(text[start..i], start, i, IsWord: true));
            }
            else if (char.IsLetterOrDigit(c) || c is '@' or '#' or '_' or '$')
            {
                var start = i;
                while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] is '@' or '#' or '_' or '$'))
                {
                    i++;
                }

                tokens.Add(new Token(text[start..i], start, i, IsWord: true));
            }
            else
            {
                tokens.Add(new Token(c.ToString(), i, i + 1, IsWord: false));
                i++;
            }
        }

        return tokens;
    }

    /// <summary>Skips a delimited run starting at <paramref name="open"/>; a doubled closing character is an escape.</summary>
    private static int SkipQuoted(string text, int open, char close)
    {
        var i = open + 1;
        while (i < text.Length)
        {
            if (text[i] == close)
            {
                if (i + 1 < text.Length && text[i + 1] == close)
                {
                    i += 2;
                    continue;
                }

                return i + 1;
            }

            i++;
        }

        return text.Length;
    }
}
