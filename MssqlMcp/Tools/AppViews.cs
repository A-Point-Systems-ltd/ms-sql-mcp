using System.Reflection;

namespace Mssql.McpServer;

/// <summary>MCP Apps views: single-file HTML built by apps/connections-ui and embedded from MssqlMcp/Apps.</summary>
internal static class AppViews
{
    public const string MimeType = "text/html;profile=mcp-app";

    public static string Read(string fileName)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var name = assembly.GetManifestResourceNames().SingleOrDefault(n => n.EndsWith(".Apps." + fileName, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"View '{fileName}' is not embedded; run 'npm run build' in apps/connections-ui and rebuild.");
        using var stream = assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
