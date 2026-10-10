using System.ComponentModel;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using Mssql.McpServer.Connections;
using Mssql.McpServer.LanguageService;

namespace Mssql.McpServer;

public sealed partial class ScriptRunnerTools
{
    /// <summary>Largest document the language service parses; larger ones get an error so a paste cannot stall the server.</summary>
    internal const int MaxLanguageServiceTextLength = 2_000_000;

    /// <summary>Longest objectInfo name: a four-part name of bracketed sysnames fits well within it.</summary>
    internal const int MaxObjectNameLength = 600;

    [McpServerTool(
        Name = ToolNames.LanguageService,
        Title = "Language Service",
        ReadOnly = true,
        Idempotent = true,
        Destructive = false),
        Description("Internal tool of the MSSQL-MCP editor extension: T-SQL IntelliSense (Microsoft SqlParser) against the connection's database. action: completion | hover | signatureHelp | scope (need text, line, column; 1-based) | objectInfo (needs name) | warm | refresh. Reads metadata only.")]
    public async Task<DbOperationResult> LanguageService(
        [Description("completion, hover, signatureHelp, scope, objectInfo, warm or refresh.")] string action,
        [Description("The document text (completion, hover, signatureHelp).")] string? text = null,
        [Description("1-based caret line.")] int line = 1,
        [Description("1-based caret column.")] int column = 1,
        [Description("completion: add JOIN / ON suggestions, table aliases and the column-picker entry.")] bool enhanced = false,
        [Description("objectInfo: the object name (may be schema-qualified or bracketed).")] string? name = null,
        [Description(Tools.ConnectionParamDescription)] string? connection = null,
        CancellationToken cancellationToken = default)
    {
        var verb = action?.Trim();
        var known = new[] { "completion", "hover", "signatureHelp", "scope", "objectInfo", "warm", "refresh" }
            .FirstOrDefault(a => string.Equals(a, verb, StringComparison.OrdinalIgnoreCase));
        if (known is null)
        {
            return new DbOperationResult(success: false, error: "action must be completion, hover, signatureHelp, scope, objectInfo, warm or refresh.");
        }

        if (languageService is null)
        {
            return new DbOperationResult(success: false, error: "The language service is not available in this server process.");
        }

        var profile = CurrentConnection.Value;
        if (profile is null)
        {
            return new DbOperationResult(success: false, error: "No connection is bound to this call.");
        }

        if (known == "warm")
        {
            return new DbOperationResult(success: true, data: new CacheStatus(languageService.Warm(profile)));
        }

        if (known == "refresh")
        {
            return new DbOperationResult(success: true, data: new CacheStatus(languageService.Refresh(profile)));
        }

        if (known == "objectInfo")
        {
            if (string.IsNullOrWhiteSpace(name) || name.Length > MaxObjectNameLength)
            {
                return new DbOperationResult(success: false, error: $"name is required (at most {MaxObjectNameLength} characters).");
            }

            try
            {
                return new DbOperationResult(success: true, data: await languageService.ObjectInfoAsync(profile, name.Trim(), cancellationToken).ConfigureAwait(false));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError("{Tool} objectInfo failed: {Error}", ToolNames.LanguageService, ex.GetType().Name + ": " + ex.Message);
                return new DbOperationResult(success: false, error: ex.Message);
            }
        }

        text ??= string.Empty;
        if (text.Length > MaxLanguageServiceTextLength)
        {
            return new DbOperationResult(success: false, error: $"text is longer than {MaxLanguageServiceTextLength} characters.");
        }

        if (line < 1 || column < 1)
        {
            return new DbOperationResult(success: false, error: "line and column are 1-based and must be at least 1.");
        }

        try
        {
            object? data = known switch
            {
                "completion" => await languageService.CompleteAsync(profile, text, line, column, enhanced, cancellationToken).ConfigureAwait(false),
                "scope" => await languageService.ScopeAsync(profile, text, line, column, cancellationToken).ConfigureAwait(false),
                "hover" => await languageService.HoverAsync(profile, text, line, column, cancellationToken).ConfigureAwait(false),
                _ => await languageService.SignatureHelpAsync(profile, text, line, column, cancellationToken).ConfigureAwait(false),
            };
            return new DbOperationResult(success: true, data: data);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Never log the document text: it can hold client data.
            logger.LogError("{Tool} {Action} failed: {Error}", ToolNames.LanguageService, known, ex.GetType().Name + ": " + ex.Message);
            return new DbOperationResult(success: false, error: ex.Message);
        }
    }
}
