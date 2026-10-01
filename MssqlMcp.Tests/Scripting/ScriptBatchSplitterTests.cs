// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.

using Mssql.McpServer.Scripting;

namespace MssqlMcp.Tests.Scripting;

public sealed class ScriptBatchSplitterTests
{
    [Fact]
    public void Go_on_its_own_line_splits_into_two_batches()
    {
        var batches = ScriptBatchSplitter.Split("SELECT 1\r\nGO\r\nSELECT 2");

        Assert.Equal(2, batches.Count);
        Assert.Equal("SELECT 1\r\n", batches[0].Text);
        Assert.Equal("SELECT 2", batches[1].Text);
        Assert.All(batches, b => Assert.Equal(1, b.RepeatCount));
    }

    [Fact]
    public void Lowercase_go_splits()
    {
        var batches = ScriptBatchSplitter.Split("select 1\ngo\nselect 2");

        Assert.Equal(["select 1\n", "select 2"], batches.Select(b => b.Text));
    }

    [Fact]
    public void Go_with_count_sets_repeat_count()
    {
        var batches = ScriptBatchSplitter.Split("INSERT dbo.t DEFAULT VALUES\nGO 3\nSELECT 2");

        Assert.Equal(2, batches.Count);
        Assert.Equal(3, batches[0].RepeatCount);
        Assert.Equal(1, batches[1].RepeatCount);
    }

    [Fact]
    public void Go_with_a_count_beyond_int_still_splits_with_the_largest_count()
    {
        var batches = ScriptBatchSplitter.Split("INSERT dbo.t DEFAULT VALUES\nGO 99999999999\nSELECT 2");

        Assert.Equal(2, batches.Count);
        Assert.Equal(int.MaxValue, batches[0].RepeatCount);
        Assert.Equal("SELECT 2", batches[1].Text);
    }

    [Fact]
    public void Go_followed_by_line_comment_splits()
    {
        var batches = ScriptBatchSplitter.Split("SELECT 1\nGO -- c\nSELECT 2");

        Assert.Equal(["SELECT 1\n", "SELECT 2"], batches.Select(b => b.Text));
    }

    [Fact]
    public void Go_after_a_statement_on_the_same_line_does_not_split()
    {
        var batches = ScriptBatchSplitter.Split("SELECT 1 GO");

        Assert.Equal("SELECT 1 GO", Assert.Single(batches).Text);
    }

    [Fact]
    public void Go_inside_a_string_does_not_split()
    {
        const string script = "SELECT 'a\r\nGO\r\nb'";

        Assert.Equal(script, Assert.Single(ScriptBatchSplitter.Split(script)).Text);
    }

    [Fact]
    public void Go_inside_a_block_comment_on_its_own_line_does_not_split()
    {
        const string script = "SELECT 1\n/* GO */\nSELECT 2";

        Assert.Equal(script, Assert.Single(ScriptBatchSplitter.Split(script)).Text);
    }

    [Fact]
    public void Start_line_is_the_script_line_of_the_batch_start()
    {
        var batches = ScriptBatchSplitter.Split("SELECT 1\r\nGO\r\nSELECT 2\r\nGO\r\n\r\nSELECT 3");

        Assert.Equal([1, 3, 5], batches.Select(b => b.StartLine));
    }

    [Fact]
    public void Whitespace_only_batch_is_dropped()
    {
        var batches = ScriptBatchSplitter.Split("GO\r\nSELECT 1\r\nGO\r\n   \r\nGO\r\n");

        Assert.Equal("SELECT 1\r\n", Assert.Single(batches).Text);
        Assert.Equal(2, batches[0].StartLine);
    }

    [Fact]
    public void Unterminated_string_becomes_one_batch_without_throwing()
    {
        const string script = "SELECT 'abc\nGO\nSELECT 2";

        var batch = Assert.Single(ScriptBatchSplitter.Split(script));

        Assert.Equal(script, batch.Text);
        Assert.Equal(1, batch.StartLine);
    }

    [Fact]
    public void Text_after_a_tokenizer_error_stays_in_the_batch_it_starts_in()
    {
        var batches = ScriptBatchSplitter.Split("SELECT 1\nGO\nSELECT 2 /* open\nGO\nSELECT 3");

        Assert.Equal(["SELECT 1\n", "SELECT 2 /* open\nGO\nSELECT 3"], batches.Select(b => b.Text));
        Assert.Equal(3, batches[1].StartLine);
    }

    [Theory]
    [InlineData("SELECT 1\nGO x\nSELECT 2")]
    [InlineData("SELECT 1\nGO 0\nSELECT 2")]
    [InlineData("SELECT 1\n/* c */ GO\nSELECT 2")]
    [InlineData("SELECT 1\nGO 2 3\nSELECT 2")]
    public void Go_with_anything_else_on_its_line_is_not_a_separator(string script) =>
        Assert.Equal(script, Assert.Single(ScriptBatchSplitter.Split(script)).Text);

    [Theory]
    [InlineData("-- note\n", true)]
    [InlineData("  /* a */\r\n-- b", true)]
    [InlineData("/* GO */ SELECT 1", false)]
    [InlineData("/* unterminated", false)]
    [InlineData("'-- not a comment'", false)]
    public void Is_comment_only_is_true_only_for_whitespace_and_comments(string text, bool expected) =>
        Assert.Equal(expected, ScriptBatchSplitter.IsCommentOnly(text));

    [Theory]
    [InlineData("CREATE PROCEDURE dbo.p AS SELECT 1", "p")]
    [InlineData("-- c\n/* d */ CREATE OR ALTER PROC [dbo].[My Proc] AS SELECT 1", "My Proc")]
    [InlineData("ALTER FUNCTION \"s\".\"f\"() RETURNS int AS BEGIN RETURN 1 END", "f")]
    [InlineData("create or alter view v AS SELECT 1 AS a", "v")]
    [InlineData("CREATE TRIGGER dbo.tr ON dbo.t AFTER INSERT AS SET NOCOUNT ON", "tr")]
    [InlineData("CREATE TABLE dbo.t (i int)", null)]
    [InlineData("SELECT 1; CREATE PROCEDURE dbo.p AS SELECT 1", null)]
    [InlineData("EXEC dbo.p", null)]
    public void Defined_module_name_is_the_last_name_part_of_a_leading_module_ddl(string text, string? expected) =>
        Assert.Equal(expected, ScriptBatchSplitter.DefinedModuleName(text));

    [Fact]
    public void Comment_only_batch_is_kept_and_indented_go_splits()
    {
        var batches = ScriptBatchSplitter.Split("SELECT 1\n  GO  \n-- note\nGO");

        Assert.Equal(["SELECT 1\n", "-- note\n"], batches.Select(b => b.Text));
    }
}
