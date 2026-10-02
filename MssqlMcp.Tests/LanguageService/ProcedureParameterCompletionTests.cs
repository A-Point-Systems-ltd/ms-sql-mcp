using Mssql.McpServer.LanguageService;

namespace MssqlMcp.Tests.LanguageService;

/// <summary>The EXEC argument scan behind procedure parameter completion; no database needed.</summary>
public sealed class ProcedureParameterCompletionTests
{
    private static (List<string> Named, int Positional)? Scan(string textWithCaret)
    {
        var caret = textWithCaret.IndexOf('|', StringComparison.Ordinal);
        return ProcedureParameterCompletion.ScanExecArguments(textWithCaret.Remove(caret, 1), caret);
    }

    [Theory]
    [InlineData("EXEC dbo.p |", 0)]
    [InlineData("EXEC dbo.p2 1, |", 1)]
    [InlineData("EXEC dbo.p2 1, 'a,b', |", 2)]
    [InlineData("EXEC @rc = dbo.p |", 0)]
    [InlineData("EXECUTE [dbo].[p] |", 0)]
    [InlineData("exec dbo.p\n  |", 0)]
    public void Argument_positions_are_found(string text, int positional)
    {
        var scan = Scan(text);
        Assert.NotNull(scan);
        Assert.Equal(positional, scan.Value.Positional);
    }

    [Fact]
    public void Named_arguments_before_and_after_the_caret_are_collected()
    {
        var scan = Scan("EXEC dbo.p3 @a = 1, | , @c = N'x;y' /* @d = 1 */");
        Assert.NotNull(scan);
        Assert.Equal(["@a", "@c"], scan.Value.Named);
        Assert.Equal(0, scan.Value.Positional);
    }

    [Theory]
    [InlineData("SELECT 1|")]
    [InlineData("EXEC dbo.p|")]
    [InlineData("EXEC dbo.p @x = |")]
    [InlineData("EXEC dbo.p 1; SELECT |")]
    [InlineData("EXEC dbo.p 1\nSELECT * FROM dbo.fn(|")]
    [InlineData("EXEC dbo.p 1\nGO\nSELECT * FROM dbo.fn(|")]
    [InlineData("EXEC dbo.p 1\nGO\n|")]
    [InlineData("EXEC dbo.p 1\nSELECT |")]
    [InlineData("EXEC dbo.p 1\nSELECT dbo.f(|")]
    [InlineData("-- EXEC dbo.p\nSELECT |")]
    public void No_parameter_list_outside_an_argument_position(string text) => Assert.Null(Scan(text));

    [Fact]
    public void An_exec_spanning_lines_with_the_caret_inside_qualifies()
    {
        var scan = Scan("EXEC dbo.p3\n    @a = 1,\n    |,\n    @c = 2\nSELECT 1");
        Assert.NotNull(scan);
        Assert.Equal(["@a", "@c"], scan.Value.Named);
    }

    [Fact]
    public void The_scan_stops_at_the_next_statement()
    {
        var scan = Scan("EXEC dbo.p |\nSELECT @x = 1");
        Assert.NotNull(scan);
        Assert.Empty(scan.Value.Named);
    }

    [Theory]
    [InlineData("@x int", "@x", "int")]
    [InlineData("@y nvarchar(10) OUTPUT", "@y", "nvarchar(10) OUTPUT")]
    [InlineData("@z", "@z", null)]
    [InlineData("interval", null, null)]
    public void Parameter_display_is_split_into_name_and_detail(string display, string? name, string? detail) =>
        Assert.Equal((name, detail), ProcedureParameterCompletion.Split(display));
}
