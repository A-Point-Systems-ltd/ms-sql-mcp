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

    [Fact]
    public void Quoting_doubles_closing_brackets_and_quotes()
    {
        Assert.Equal("[a]]b]", Sql.Q("a]b"));
        Assert.Equal("N'it''s'", Sql.N("it's"));
        Assert.Equal("NULL", Sql.N(null));
        Assert.Equal("[dbo].[T x]", Sql.Qualified("dbo", "T x"));
    }
}
