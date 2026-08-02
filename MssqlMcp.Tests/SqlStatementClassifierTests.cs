// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.

using Mssql.McpServer;

namespace MssqlMcp.Tests;

public sealed class SqlStatementClassifierTests
{
    [Theory]
    [InlineData("SELECT 1")]
    [InlineData("  select * from dbo.Users")]
    [InlineData("-- comment\nSELECT name FROM sys.tables")]
    [InlineData("WITH x AS (SELECT 1 AS n) SELECT n FROM x")]
    [InlineData("SELECT s.name, t.name FROM sys.tables t JOIN sys.schemas s ON t.schema_id = s.schema_id")]
    public void TryValidateReadOnly_accepts_select_queries(string sql)
    {
        Assert.True(SqlStatementClassifier.TryValidateReadOnly(sql, out var error), error);
        Assert.True(SqlStatementClassifier.IsReadOnlyQuery(sql));
    }

    [Theory]
    [InlineData("INSERT INTO t VALUES (1)")]
    [InlineData("UPDATE t SET x = 1")]
    [InlineData("DELETE FROM t")]
    [InlineData("CREATE TABLE t (id int)")]
    [InlineData("SELECT id INTO #tmp FROM t")]
    [InlineData("WITH x AS (SELECT 1) DELETE FROM t")]
    public void TryValidateReadOnly_rejects_non_read_queries(string sql)
    {
        Assert.False(SqlStatementClassifier.TryValidateReadOnly(sql, out _));
    }

    [Theory]
    [InlineData("SELECT 1")]
    [InlineData("WITH x AS (SELECT 1) SELECT * FROM x")]
    [InlineData("  -- leading comment\nSELECT * FROM INFORMATION_SCHEMA.TABLES")]
    public void TryValidateExecutable_rejects_select_queries(string sql)
    {
        Assert.False(SqlStatementClassifier.TryValidateExecutable(sql, out var error));
        Assert.Contains("ReadData", error, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("INSERT INTO t VALUES (1)")]
    [InlineData("UPDATE t SET x = 1")]
    [InlineData("ALTER TABLE t ADD y int")]
    [InlineData("DROP TABLE t")]
    public void TryValidateExecutable_accepts_ddl_dml(string sql)
    {
        Assert.True(SqlStatementClassifier.TryValidateExecutable(sql, out var error), error);
    }

    [Fact]
    public void TryValidateExecutable_rejects_multiple_statements()
    {
        const string sql = "UPDATE t SET x = 1; UPDATE t SET y = 2";
        Assert.False(SqlStatementClassifier.TryValidateExecutable(sql, out var error));
        Assert.Contains("single", error, StringComparison.OrdinalIgnoreCase);
    }
}
