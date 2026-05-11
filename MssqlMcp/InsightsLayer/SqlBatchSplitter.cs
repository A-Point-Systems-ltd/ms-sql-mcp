using System.Text;

namespace Mssql.McpServer.InsightsLayer;

/// <summary>
/// Splits T-SQL scripts on <c>GO</c> batch terminators (line-based, case-insensitive).
/// </summary>
public static class SqlBatchSplitter
{
    public static IEnumerable<string> SplitBatches(string script)
    {
        if (string.IsNullOrWhiteSpace(script))
        {
            yield break;
        }

        using var reader = new StringReader(script);
        var batch = new StringBuilder();
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (IsGoLine(line))
            {
                if (batch.Length > 0)
                {
                    yield return batch.ToString().Trim();
                    batch.Clear();
                }
            }
            else
            {
                batch.AppendLine(line);
            }
        }

        if (batch.Length > 0)
        {
            yield return batch.ToString().Trim();
        }
    }

    private static bool IsGoLine(string line) =>
        line.Trim().Equals("GO", StringComparison.OrdinalIgnoreCase);
}
