// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.

using Mssql.McpServer.Scripting;

namespace MssqlMcp.Tests.Scripting;

public sealed class SqlServerVersionTests
{
    [Theory]
    [InlineData("10.50.6560.0", false, false)]
    [InlineData("11.0.7001.0", false, true)]
    [InlineData("13.0.4000.0", false, true)]
    [InlineData("13.0.4001.0", true, true)]
    [InlineData("16.0.1000.6", true, true)]
    public void Feature_flags_follow_version(string version, bool createOrAlter, bool alterRoleAddMember)
    {
        var v = SqlServerVersion.Parse(version);
        Assert.Equal(createOrAlter, v.SupportsCreateOrAlter);
        Assert.Equal(alterRoleAddMember, v.SupportsAlterRoleAddMember);
    }

    [Theory]
    [InlineData("12.0.2000.8", 5, true)]
    [InlineData("12.0.2000.8", 8, true)]
    [InlineData("12.0.2000.8", 3, false)]
    [InlineData("12.0.2000.8", 0, false)]
    public void Azure_engine_editions_support_the_current_surface_regardless_of_version(string version, int engineEdition, bool azure)
    {
        var v = SqlServerVersion.Parse(version, engineEdition);
        Assert.Equal(azure, v.IsAzure);
        Assert.Equal(azure, v.SupportsCreateOrAlter);
        Assert.Equal(azure, v.SupportsTemporal);
        Assert.True(v.SupportsMemoryOptimized); // 12.0 on-prem already has memory-optimized tables
        Assert.True(v.SupportsAlterRoleAddMember);
    }

    [Fact]
    public void Sql_server_2008_r2_standard_edition_is_unchanged()
    {
        var v = SqlServerVersion.Parse("10.50.6560.0", 3);
        Assert.False(v.IsAzure);
        Assert.False(v.SupportsCreateOrAlter);
        Assert.False(v.SupportsAlterRoleAddMember);
        Assert.False(v.SupportsTemporal);
        Assert.False(v.SupportsMemoryOptimized);
    }

    [Theory]
    [InlineData("a\u2028b", "a b")]
    [InlineData("a\u2029b", "a b")]
    [InlineData("a\r\nb", "a  b")]
    [InlineData("plain", "plain")]
    public void Comment_safe_replaces_line_breaking_characters(string input, string expected) =>
        Assert.Equal(expected, Sql.CommentSafe(input));

    [Fact]
    public void Quoting_doubles_closing_brackets_and_quotes()
    {
        Assert.Equal("[a]]b]", Sql.Q("a]b"));
        Assert.Equal("N'it''s'", Sql.N("it's"));
        Assert.Equal("NULL", Sql.N(null));
        Assert.Equal("[dbo].[T x]", Sql.Qualified("dbo", "T x"));
    }
}
