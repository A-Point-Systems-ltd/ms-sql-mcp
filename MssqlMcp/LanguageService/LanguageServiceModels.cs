namespace Mssql.McpServer.LanguageService;

/// <summary>Completion kinds on the wire.</summary>
public static class CompletionKinds
{
    public const string Table = "table";
    public const string View = "view";
    public const string Column = "column";
    public const string Procedure = "procedure";
    public const string Function = "function";
    public const string Keyword = "keyword";
    public const string Schema = "schema";
    public const string Parameter = "parameter";
    public const string Variable = "variable";
    public const string Database = "database";
    public const string Type = "type";
    public const string Snippet = "snippet";
    public const string Join = "join";

    /// <summary>The column-picker entry: the client opens its picker instead of inserting text.</summary>
    public const string Picker = "picker";
    public const string Other = "other";
}

/// <summary>Cache states on the wire: <c>warm</c> once metadata is bound, <c>loading</c> while it is still being built.</summary>
public static class CacheStates
{
    public const string Warm = "warm";
    public const string Loading = "loading";
}

public sealed record CompletionItemInfo(string Label, string Kind, string? Detail, string InsertText, string SortText);

/// <summary>
/// <paramref name="IsIncomplete"/> is true when binding timed out and only keywords were returned; the client should ask again.
/// </summary>
public sealed record CompletionList(IReadOnlyList<CompletionItemInfo> Items, bool IsIncomplete, string CacheState);

/// <summary>A 1-based, end-exclusive text range.</summary>
public sealed record TextRange(int StartLine, int StartColumn, int EndLine, int EndColumn);

/// <summary><paramref name="Contents"/> is plain text; the client renders it as plain text or a code block.</summary>
public sealed record HoverInfo(string Contents, TextRange? Range);

public sealed record ParameterInfo(string Label, string? Documentation);

public sealed record SignatureInfo(string Label, string? Documentation, IReadOnlyList<ParameterInfo> Parameters);

/// <summary><paramref name="ActiveParameter"/> is 0-based, or -1 when the caret is on no parameter.</summary>
public sealed record SignatureHelpInfo(IReadOnlyList<SignatureInfo> Signatures, int ActiveSignature, int ActiveParameter);

/// <summary>Result of <c>warm</c> and <c>refresh</c>.</summary>
public sealed record CacheStatus(string CacheState);

/// <summary>A table source of the statement at the caret, with its columns when the catalog knows it (<c>scope</c>).</summary>
public sealed record ScopeTableInfo(string? Alias, string? Schema, string Name, string? Kind, IReadOnlyList<CatalogColumn> Columns);

/// <summary>Result of <c>scope</c>: the statement's table sources in order; <paramref name="CacheState"/> as for completion.</summary>
public sealed record ScopeResult(IReadOnlyList<ScopeTableInfo> Tables, string CacheState);

/// <summary>A module parameter; <paramref name="Default"/> is its default value as written, or null.</summary>
public sealed record ObjectParameterInfo(string Name, string Type, string? Default, bool IsOutput);

/// <summary>
/// Result of <c>objectInfo</c>: the object a name resolves to (OBJECT_ID rules: default schema, brackets, synonyms
/// followed), with the object explorer's script type and, for modules, the parameters. <paramref name="Found"/> is false
/// when nothing matches.
/// </summary>
public sealed record ObjectInfo(bool Found, string? Schema, string? Name, string? Type, string? ScriptType, IReadOnlyList<ObjectParameterInfo> Parameters);
