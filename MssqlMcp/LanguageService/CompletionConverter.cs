// Ported from microsoft/sqltoolsservice (MIT), adapted to this server's wire records:
// https://github.com/microsoft/sqltoolsservice/blob/02de444481bccd4492cd4e35e44fb97738f3a770/src/Microsoft.SqlTools.LanguageService/LanguageServices/Completion/SqlCompletionItem.cs
// https://github.com/microsoft/sqltoolsservice/blob/02de444481bccd4492cd4e35e44fb97738f3a770/src/Microsoft.SqlTools.LanguageService/LanguageServices/AutoCompleteHelper.cs
// (SqlCompletionItem label/insert-text quoting; AutoCompleteHelper.GetDefaultCompletionItems, ConvertQuickInfoToHover,
// ConvertMethodHelpTextListToSignatureHelp; CompletionService.ShouldShowCompletionList.) Kinds and sort order are ours.
//
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.
//
// MIT License
//
// Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated
// documentation files (the "Software"), to deal in the Software without restriction, including without limitation
// the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to
// permit persons to whom the Software is furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all copies or substantial portions of
// the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE
// WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR
// COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
// OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

using System.Globalization;
using System.Text.RegularExpressions;
using Babel;
using Microsoft.SqlServer.Management.SqlParser.Intellisense;
using Microsoft.SqlServer.Management.SqlParser.Parser;

namespace Mssql.McpServer.LanguageService;

/// <summary>Converts SqlParser results (declarations, quick info, method help) to the language_service wire records.</summary>
internal static partial class CompletionConverter
{
    private static readonly (string Start, string End)[] Delimiters = [("[", "]"), ("\"", "\"")];

    private static readonly HashSet<string> AnsiScalarFunctions = new(StringComparer.OrdinalIgnoreCase)
    {
        "CURRENT_DATE", "CURRENT_TIME", "CURRENT_TIMESTAMP", "CURRENT_USER", "SESSION_USER", "SYSTEM_USER", "USER",
    };

    [GeneratedRegex(@"^[\p{L}_@#][\p{L}\p{N}@$#_]{0,127}$")]
    private static partial Regex ValidSqlName();

    /// <summary>The token SqlParser sees at the caret, or null.</summary>
    public static Token? TokenAt(ParseResult parseResult, int line, int column)
    {
        var tokens = parseResult.Script?.TokenManager;
        if (tokens is null)
        {
            return null;
        }

        var index = tokens.FindToken(line, column);
        return index >= 0 ? tokens.GetToken(index) : null;
    }

    /// <summary>No completion list inside comments (CompletionService.ShouldShowCompletionList).</summary>
    public static bool IsComment(Token? token) =>
        token?.Type is "LEX_MULTILINE_COMMENT" or "LEX_END_OF_LINE_COMMENT";

    public static CompletionItemInfo FromDeclaration(Declaration declaration, string? tokenText)
    {
        var title = declaration.Title;
        var label = title;
        var insertText = title;
        var delimiter = Delimiters.FirstOrDefault(d => tokenText is not null && tokenText.StartsWith(d.Start, StringComparison.Ordinal));
        var typedDelimiter = delimiter != default;

        if (!typedDelimiter && !string.IsNullOrEmpty(title))
        {
            switch (declaration.Type)
            {
                case DeclarationType.Server:
                case DeclarationType.Database:
                case DeclarationType.Table:
                case DeclarationType.Column:
                case DeclarationType.View:
                case DeclarationType.Schema:
                    // Quote only when needed: an invalid regular identifier or a reserved word.
                    if (!ValidSqlName().IsMatch(title) || SqlKeywords.IsReservedWord(title))
                    {
                        insertText = Delimit(("[", "]"), title);
                    }

                    break;
            }
        }

        // A token that starts with a delimiter always gets the label and insert text quoted the same way.
        if (typedDelimiter)
        {
            label = Delimit(delimiter, label);
            insertText = Delimit(delimiter, insertText);
        }

        var kind = KindOf(declaration.Type);
        var detail = string.IsNullOrEmpty(declaration.DatabaseQualifiedName) ? declaration.Type.ToString() : declaration.DatabaseQualifiedName;
        return new CompletionItemInfo(label, kind, detail, insertText, SortText(SortRank(declaration), label));
    }

    /// <summary>The default keyword list (AutoCompleteHelper.GetDefaultCompletionItems), filtered by the word being typed.</summary>
    public static List<CompletionItemInfo> Keywords(string? prefix)
    {
        var items = new List<CompletionItemInfo>();
        foreach (var text in SqlKeywords.DefaultCompletionText)
        {
            if (string.IsNullOrWhiteSpace(prefix) || text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                var label = text.ToUpperInvariant();
                items.Add(new CompletionItemInfo(label, CompletionKinds.Keyword, label + " keyword", label, SortText(7, label)));
            }
        }

        return items;
    }

    public static HoverInfo? ToHover(CodeObjectQuickInfo? quickInfo)
    {
        if (quickInfo is null || string.IsNullOrWhiteSpace(quickInfo.Text))
        {
            return null;
        }

        TextRange? range = quickInfo.StartLocation is { } s && quickInfo.EndLocation is { } e && s.LineNumber > 0 && e.LineNumber > 0
            ? new TextRange(s.LineNumber, s.ColumnNumber, e.LineNumber, e.ColumnNumber)
            : null;
        return new HoverInfo(quickInfo.Text.Replace("\r\n", "\n", StringComparison.Ordinal).Trim(), range);
    }

    /// <summary>
    /// AutoCompleteHelper.ConvertMethodHelpTextListToSignatureHelp, with one fix: a method without parameters no longer
    /// throws (the original aggregates an empty sequence). <paramref name="line"/> and <paramref name="column"/> are 1-based.
    /// </summary>
    public static SignatureHelpInfo? ToSignatureHelp(List<MethodHelpText>? methods, MethodNameAndParamLocations? locations, int line, int column)
    {
        if (methods is null || locations is null)
        {
            return null;
        }

        var signatures = methods
            .Select(method =>
            {
                // Signature label format: <name> param1,param2,...,paramN <return type>
                var parameters = method.Parameters.Select(p => new ParameterInfo(p.Display, NullIfEmpty(p.Description))).ToList();
                var label = string.Join(" ", new[] { method.Name, string.Join(",", parameters.Select(p => p.Label)), method.Type }
                    .Where(part => !string.IsNullOrEmpty(part)));
                return new SignatureInfo(label, NullIfEmpty(method.Description), parameters);
            })
            .Where(s => locations.Name is null || s.Label.Contains(locations.Name, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (signatures.Count == 0)
        {
            return null;
        }

        // The current parameter at the caret: -1 when the caret is on no parameter.
        var current = -1;
        if (locations.ParamStartLocation is { } start
            && (line > start.LineNumber || (line == start.LineNumber && column >= start.ColumnNumber)))
        {
            current = 0;
        }

        foreach (var separator in locations.ParamSeperatorLocations ?? [])
        {
            if (line > separator.LineNumber || (line == separator.LineNumber && column > separator.ColumnNumber))
            {
                current++;
            }
        }

        if (locations.ParamEndLocation is { } end
            && (line > end.LineNumber || (line == end.LineNumber && column > end.ColumnNumber)))
        {
            current = -1;
        }

        return new SignatureHelpInfo(signatures, 0, current);
    }

    /// <summary>Sort ranks: 0 procedure parameters, 1 variables and columns, 2 objects, 3 schemas, 4 databases, 5 functions, 6 global variables, 7 keywords.</summary>
    internal static string SortText(int rank, string label) => rank.ToString(CultureInfo.InvariantCulture) + "_" + label.ToLowerInvariant();

    private static int SortRank(Declaration d) => d.Type switch
    {
        DeclarationType.ScalarParameter or DeclarationType.TableParameter or DeclarationType.CursorParameter => 0,
        DeclarationType.Column or DeclarationType.ScalarVariable or DeclarationType.TableVariable or DeclarationType.CursorVariable => 1,
        DeclarationType.Table or DeclarationType.View or DeclarationType.VirtualTable or DeclarationType.Synonym
            or DeclarationType.StoredProcedure or DeclarationType.ExtendedStoredProcedure => 2,
        DeclarationType.Schema => 3,
        DeclarationType.Database => 4,
        DeclarationType.BuiltInFunction when d.Title.StartsWith("@@", StringComparison.Ordinal) => 6,
        _ => 5,
    };

    private static string KindOf(DeclarationType type) => type switch
    {
        DeclarationType.Table or DeclarationType.VirtualTable => CompletionKinds.Table,
        DeclarationType.View => CompletionKinds.View,
        DeclarationType.Column => CompletionKinds.Column,
        DeclarationType.StoredProcedure or DeclarationType.ExtendedStoredProcedure => CompletionKinds.Procedure,
        DeclarationType.BuiltInFunction or DeclarationType.ScalarValuedFunction or DeclarationType.TableValuedFunction
            or DeclarationType.UserDefinedAggregate => CompletionKinds.Function,
        DeclarationType.Schema => CompletionKinds.Schema,
        DeclarationType.ScalarParameter or DeclarationType.TableParameter or DeclarationType.CursorParameter => CompletionKinds.Parameter,
        DeclarationType.ScalarVariable or DeclarationType.TableVariable or DeclarationType.CursorVariable => CompletionKinds.Variable,
        DeclarationType.Database => CompletionKinds.Database,
        DeclarationType.SystemDataType or DeclarationType.UserDefinedDataType or DeclarationType.UserDefinedTableType
            or DeclarationType.UserDefinedClrType or DeclarationType.ScalarDataType or DeclarationType.TableDataType => CompletionKinds.Type,
        _ => CompletionKinds.Other,
    };

    private static string Delimit((string Start, string End) delimiter, string text) =>
        text.StartsWith(delimiter.Start, StringComparison.Ordinal) && text.EndsWith(delimiter.End, StringComparison.Ordinal)
            ? text
            : delimiter.Start + text + delimiter.End;

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}
