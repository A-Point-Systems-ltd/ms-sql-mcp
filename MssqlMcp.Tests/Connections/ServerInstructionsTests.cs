using Mssql.McpServer.Connections;

namespace MssqlMcp.Tests.Connections;

public sealed class ServerInstructionsTests
{
    [Fact]
    public void Instructions_state_the_mandatory_rule_and_count()
    {
        var reg = new ConnectionRegistry(
        [
            new("a", "Server=a", false, true, ConnectionSource.Configured),
            new("b", "Server=b", false, true, ConnectionSource.Configured),
        ]);

        var text = ServerInstructions.Build(reg);

        Assert.Contains("MUST pass the 'connection' argument", text);
        Assert.Contains("there is no default connection", text);
        Assert.Contains("Current connections at startup: 2.", text);
        Assert.DoesNotContain("may be omitted until", text);
        Assert.DoesNotContain(Mssql.McpServer.ToolNames.RunScript, text, StringComparison.Ordinal);
    }

    [Fact]
    public void Single_connection_instructions_say_it_may_be_omitted()
    {
        var reg = new ConnectionRegistry([new("default", "Server=a", false, true, ConnectionSource.Legacy)]);
        Assert.Contains("may be omitted until more are opened", ServerInstructions.Build(reg));
    }
}

[Collection(EnvVarLock.Name)]
public sealed class ServerInstructionsInsightRuleTests
{
    [Theory]
    [InlineData(null, true)]
    [InlineData("false", false)]
    public void Insight_rule_follows_auto_population_switch(string? autoPopulate, bool expected)
    {
        var previousLayer = Environment.GetEnvironmentVariable("USE_INSIGHTS_LAYER");
        var previousAuto = Environment.GetEnvironmentVariable("INSIGHTS_AUTOPOPULATE");
        try
        {
            Environment.SetEnvironmentVariable("USE_INSIGHTS_LAYER", null);
            Environment.SetEnvironmentVariable("INSIGHTS_AUTOPOPULATE", autoPopulate);
            var text = ServerInstructions.Build(new ConnectionRegistry([new("default", "Server=a", false, true, ConnectionSource.Legacy)]));
            Assert.Equal(expected, text.Contains(ServerInstructions.InsightEnrichmentRule, StringComparison.Ordinal));
        }
        finally
        {
            Environment.SetEnvironmentVariable("USE_INSIGHTS_LAYER", previousLayer);
            Environment.SetEnvironmentVariable("INSIGHTS_AUTOPOPULATE", previousAuto);
        }
    }
}
