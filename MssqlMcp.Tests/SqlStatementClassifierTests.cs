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
    [InlineData("SELECT 1;")]
    [InlineData("SELECT '--not a comment; DROP TABLE t' AS x")]
    [InlineData("SELECT 'it''s /* fine */' AS x")]
    [InlineData("SELECT [delete], [update] FROM dbo.[Insert Log]")]
    [InlineData("SELECT TOP 5 * FROM dbo.Orders ORDER BY Id DESC")]
    [InlineData("SELECT a FROM t UNION ALL SELECT b FROM u")]
    [InlineData("SELECT * FROM otherdb.dbo.t")]
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
    [InlineData("EXEC sp_who")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("-- only a comment")]
    public void TryValidateReadOnly_rejects_non_read_queries(string sql)
    {
        Assert.False(SqlStatementClassifier.TryValidateReadOnly(sql, out _));
    }

    /// <summary>Statement-smuggling cases that bypassed the previous regex classifier.</summary>
    [Theory]
    [InlineData("SELECT 1 AS a WAITFOR DELAY '00:00:02'")]
    [InlineData("SELECT '--' AS x; WAITFOR DELAY '00:00:02'")]
    [InlineData("SELECT 1 DROP TABLE t")]
    [InlineData("SELECT 1 /* ; */ DELETE FROM t")]
    [InlineData("SELECT '/*' AS a; DELETE FROM t; SELECT '*/' AS b")]
    [InlineData("SELECT 1\nGO\nDROP TABLE t")]
    [InlineData("SELECT 1 EXEC('DROP TABLE t')")]
    [InlineData("SELECT * FROM OPENQUERY(lnk, 'DELETE FROM t')")]
    [InlineData("SELECT * FROM OPENROWSET('SQLNCLI', 'Server=x;Trusted_Connection=yes', 'SELECT 1')")]
    [InlineData("SELECT * FROM OPENROWSET(BULK 'C:\\secrets.txt', SINGLE_CLOB) AS x")]
    [InlineData("SELECT * FROM OPENDATASOURCE('SQLNCLI', 'Data Source=x').db.dbo.t")]
    [InlineData("SELECT * FROM linkedsrv.db.dbo.t")]
    [InlineData("SELECT 1 FROM (SELECT 1 AS a) x WHERE 1 = 1 SHUTDOWN")]
    public void TryValidateReadOnly_rejects_smuggled_statements(string sql)
    {
        Assert.False(SqlStatementClassifier.TryValidateReadOnly(sql, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void TryValidateReadOnly_reports_syntax_errors_with_position()
    {
        Assert.False(SqlStatementClassifier.TryValidateReadOnly("SELECT FROM WHERE", out var error));
        Assert.Contains("syntax error", error, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("SELECT 1")]
    [InlineData("WITH x AS (SELECT 1) SELECT * FROM x")]
    [InlineData("  -- leading comment\nSELECT * FROM INFORMATION_SCHEMA.TABLES")]
    public void TryValidateExecutable_rejects_select_queries(string sql)
    {
        Assert.False(SqlStatementClassifier.TryValidateExecutable(sql, out var error));
        Assert.Contains(ToolNames.ReadData, error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("INSERT INTO t VALUES (1)")]
    [InlineData("UPDATE t SET x = 1")]
    [InlineData("ALTER TABLE t ADD y int")]
    [InlineData("DROP TABLE t")]
    [InlineData("DROP TABLE IF EXISTS dbo.t")]
    [InlineData("DELETE FROM t WHERE id = 1")]
    [InlineData("TRUNCATE TABLE t")]
    [InlineData("MERGE t AS d USING s ON d.id = s.id WHEN MATCHED THEN UPDATE SET d.x = s.x;")]
    [InlineData("EXEC dbo.DoWork @id = 1")]
    [InlineData("SELECT id INTO dbo.t2 FROM t")]
    [InlineData("CREATE PROCEDURE dbo.p AS BEGIN SET NOCOUNT ON; SELECT 1; UPDATE t SET x = 1; END")]
    [InlineData("CREATE VIEW dbo.v AS SELECT 1 AS a")]
    [InlineData("GRANT SELECT ON dbo.t TO someone")]
    [InlineData("INSERT INTO t EXEC dbo.SomeProc")]
    [InlineData("IF OBJECT_ID(N'dbo.t', N'U') IS NOT NULL DROP TABLE dbo.t")]
    [InlineData("IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'ix') CREATE INDEX ix ON dbo.t (a)")]
    [InlineData("IF 1 = 1 BEGIN DELETE FROM t WHERE id = 1; UPDATE u SET x = 2; END ELSE DROP TABLE v")]
    public void TryValidateExecutable_accepts_ddl_dml(string sql)
    {
        Assert.True(SqlStatementClassifier.TryValidateExecutable(sql, out var error), error);
    }

    [Theory]
    [InlineData("UPDATE t SET x = 1; UPDATE t SET y = 2")]
    [InlineData("UPDATE t SET x = 1 UPDATE t SET y = 2")]
    [InlineData("UPDATE t SET x = 1\nGO\nUPDATE t SET y = 2")]
    public void TryValidateExecutable_rejects_multiple_statements(string sql)
    {
        Assert.False(SqlStatementClassifier.TryValidateExecutable(sql, out var error));
        Assert.Contains("single", error, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("SET NOCOUNT ON")]
    [InlineData("DECLARE @x int")]
    [InlineData("WAITFOR DELAY '00:00:01'")]
    [InlineData("SHUTDOWN")]
    [InlineData("USE master")]
    [InlineData("IF 1 = 1 SELECT * FROM t")]
    [InlineData("IF 1 = 1 BEGIN SELECT 1; END")]
    [InlineData("IF 1 = 1 WAITFOR DELAY '00:00:05'")]
    [InlineData("IF 1 = 1 DROP TABLE t ELSE SHUTDOWN")]
    public void TryValidateExecutable_rejects_unlisted_statement_types(string sql)
    {
        Assert.False(SqlStatementClassifier.TryValidateExecutable(sql, out _));
    }

    [Theory]
    [InlineData(SqlStatementKind.Insert, "INSERT INTO t VALUES (1)", true)]
    [InlineData(SqlStatementKind.Insert, "INSERT INTO t SELECT a FROM u", true)]
    [InlineData(SqlStatementKind.Insert, "DROP TABLE t", false)]
    [InlineData(SqlStatementKind.Insert, "DELETE FROM t", false)]
    [InlineData(SqlStatementKind.Insert, "INSERT INTO t VALUES (1) DROP TABLE t", false)]
    [InlineData(SqlStatementKind.Insert, "INSERT INTO t EXEC('CREATE TABLE side_effect (x int)')", false)]
    [InlineData(SqlStatementKind.Insert, "INSERT INTO t EXEC dbo.SomeProc", false)]
    [InlineData(SqlStatementKind.Insert, "INSERT INTO t EXEC sp_executesql N'DROP TABLE u'", false)]
    [InlineData(SqlStatementKind.Insert, "INSERT INTO t SELECT * FROM OPENROWSET('SQLNCLI', 'Server=x;Trusted_Connection=yes', 'SELECT 1')", false)]
    [InlineData(SqlStatementKind.Insert, "INSERT INTO t SELECT a FROM lnk.db.dbo.u", false)]
    [InlineData(SqlStatementKind.Update, "UPDATE t SET x = q.a FROM t JOIN OPENQUERY(lnk, 'SELECT 1 AS a') q ON 1 = 1", false)]
    [InlineData(SqlStatementKind.Update, "UPDATE t SET x = 1 WHERE id = 2", true)]
    [InlineData(SqlStatementKind.Update, "DELETE FROM t", false)]
    [InlineData(SqlStatementKind.CreateTable, "CREATE TABLE dbo.t (id int PRIMARY KEY)", true)]
    [InlineData(SqlStatementKind.CreateTable, "CREATE PROCEDURE p AS DROP TABLE t", false)]
    [InlineData(SqlStatementKind.CreateTable, "DROP DATABASE prod", false)]
    [InlineData(SqlStatementKind.DropTable, "DROP TABLE IF EXISTS dbo.t", true)]
    [InlineData(SqlStatementKind.DropTable, "DROP DATABASE prod", false)]
    [InlineData(SqlStatementKind.DropTable, "TRUNCATE TABLE t", false)]
    public void TryValidateWrite_enforces_tool_statement_kind(SqlStatementKind kind, string sql, bool expected)
    {
        var ok = SqlStatementClassifier.TryValidateWrite(sql, kind, out var error);
        Assert.True(expected == ok, error);
        if (!expected)
        {
            Assert.False(string.IsNullOrWhiteSpace(error));
        }
    }
}
