// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.

using Microsoft.Data.SqlClient;
using Mssql.McpServer.Scripting;

namespace MssqlMcp.Tests.Scripting;

public sealed class ObjectScripterTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [Theory]
    [InlineData("table", "Table")]
    [InlineData("Stored_Procedure", "StoredProcedure")]
    [InlineData("proc", "StoredProcedure")]
    [InlineData("TVF", "TableFunction")]
    [InlineData("foreign-key", "ForeignKey")]
    [InlineData("indexes", "Index")]
    [InlineData("Database Trigger", "DatabaseTrigger")]
    [InlineData("udt", "Type")]
    [InlineData("user", "DatabaseUser")]
    [InlineData("role", "DatabaseRole")]
    [InlineData("SERVERROLE", "ServerRole")]
    [InlineData("logins", "Login")]
    public void Normalizes_object_type_aliases(string input, string expected)
    {
        Assert.Equal(expected, ObjectScripter.NormalizeObjectType(input));
    }

    [Theory]
    [InlineData("Synonym")]
    [InlineData("")]
    [InlineData(null)]
    public void Unsupported_object_type_is_rejected_before_any_query(string? input)
    {
        Assert.Null(ObjectScripter.NormalizeObjectType(input));
    }

    [Fact]
    public async Task Unsupported_object_type_returns_error_without_touching_the_connection()
    {
        using var unopened = new SqlConnection();
        var (result, error) = await ObjectScripter.ScriptAsync(unopened, "Synonym", "dbo.x", null, CancellationToken.None);
        Assert.Null(result);
        Assert.Contains("Unsupported objectType", error);
    }

    [Theory]
    [InlineData("Login")]
    [InlineData("DatabaseUser")]
    [InlineData("Index")]
    [InlineData("DatabaseTrigger")]
    [InlineData("Table")]
    [InlineData("TableTrigger")]
    public async Task Names_longer_than_sysname_are_rejected_before_any_query(string type)
    {
        using var unopened = new SqlConnection();
        var (result, error) = await ObjectScripter.ScriptAsync(unopened, type, new string('x', 129), "dbo.t", CancellationToken.None);
        Assert.Null(result);
        Assert.Contains("128", error);
    }

    [Fact]
    public async Task Index_parent_longer_than_sysname_is_rejected_before_any_query()
    {
        using var unopened = new SqlConnection();
        var (result, error) = await ObjectScripter.ScriptAsync(unopened, "Index", "IX", "dbo." + new string('t', 129), CancellationToken.None);
        Assert.Null(result);
        Assert.Contains("128", error);
    }

    [Theory]
    [InlineData("View", "bogus")]
    [InlineData("Table", "")]
    [InlineData("StoredProcedure", "create or alter")]
    public async Task Invalid_form_value_is_rejected_before_any_query(string type, string form)
    {
        using var unopened = new SqlConnection();
        var (result, error) = await ObjectScripter.ScriptAsync(unopened, type, "dbo.x", null, CancellationToken.None, form);
        Assert.Null(result);
        Assert.Equal("form must be 'create' or 'alter'.", error);
    }

    [Theory]
    [InlineData("Table")]
    [InlineData("Index")]
    [InlineData("ForeignKey")]
    [InlineData("Type")]
    [InlineData("Login")]
    [InlineData("ServerRole")]
    [InlineData("DatabaseUser")]
    [InlineData("DatabaseRole")]
    public async Task Alter_form_is_rejected_for_types_that_have_no_alter_script(string type)
    {
        using var unopened = new SqlConnection();
        var (result, error) = await ObjectScripter.ScriptAsync(unopened, type, "dbo.x", "dbo.t", CancellationToken.None, "alter");
        Assert.Null(result);
        Assert.Equal("form='alter' is supported only for View, StoredProcedure, TableFunction, ScalarFunction, TableTrigger and DatabaseTrigger.", error);
    }

    [Theory]
    [InlineData("13.0.4001.0", DdlForm.CreateOrAlter, "CREATE OR ALTER VIEW")]
    [InlineData("12.0.2000.8", DdlForm.Alter, "ALTER VIEW")]
    public void View_alter_form_rewrites_the_leading_create_and_keeps_leading_comments(string version, DdlForm form, string keyword)
    {
        var module = ("-- owner: sales\r\nCREATE VIEW [dbo].[v] AS SELECT 1 AS a", true, true);
        var result = ObjectScripter.ComposeView("dbo", "v", module, [], SqlServerVersion.Parse(version), alter: true);

        Assert.Equal(form, result.Form);
        Assert.Empty(result.Warnings);
        Assert.Equal(
            $"SET ANSI_NULLS ON\r\nGO\r\nSET QUOTED_IDENTIFIER ON\r\nGO\r\n-- owner: sales\r\n{keyword} [dbo].[v] AS SELECT 1 AS a\r\nGO",
            result.Ddl);
    }

    [Fact]
    public void View_default_form_keeps_the_stored_create_on_every_version()
    {
        var module = ("CREATE VIEW [dbo].[v] AS SELECT 1 AS a", true, true);
        foreach (var version in new[] { "13.0.4001.0", "12.0.2000.8", "10.50.6560.0" })
        {
            var result = ObjectScripter.ComposeView("dbo", "v", module, [], SqlServerVersion.Parse(version), alter: false);
            Assert.Equal(DdlForm.Create, result.Form);
            Assert.Contains("\r\nCREATE VIEW [dbo].[v] AS", result.Ddl);
        }
    }

    [Fact]
    public void Indexed_view_alter_form_warns_that_indexes_are_dropped_and_recreated()
    {
        var module = ("CREATE VIEW [dbo].[v] WITH SCHEMABINDING AS SELECT 1 AS a", true, true);
        IReadOnlyList<(string, string?)> indexes = [("CREATE UNIQUE CLUSTERED INDEX [IX_v] ON [dbo].[v] ([a]);", null)];
        const string Warning = "Altering an indexed view drops its indexes; the index statements below recreate them.";

        var alter = ObjectScripter.ComposeView("dbo", "v", module, indexes, SqlServerVersion.Parse("13.0.4001.0"), alter: true);
        Assert.Equal(Warning, Assert.Single(alter.Warnings));
        Assert.StartsWith($"-- WARNING: {Warning}\r\nSET ANSI_NULLS ON", alter.Ddl);
        Assert.Contains("\r\nCREATE OR ALTER VIEW [dbo].[v] WITH SCHEMABINDING AS", alter.Ddl);
        Assert.EndsWith("\r\nGO\r\nCREATE UNIQUE CLUSTERED INDEX [IX_v] ON [dbo].[v] ([a]);\r\nGO", alter.Ddl);

        var create = ObjectScripter.ComposeView("dbo", "v", module, indexes, SqlServerVersion.Parse("13.0.4001.0"), alter: false);
        Assert.Empty(create.Warnings);
        Assert.DoesNotContain("-- WARNING", create.Ddl);
    }

    [Fact]
    public void Encrypted_view_alter_form_returns_a_warning_only_result_with_the_alter_form()
    {
        var result = ObjectScripter.ComposeView("dbo", "v", (null, true, true), [], SqlServerVersion.Parse("13.0.4001.0"), alter: true);
        Assert.Equal(DdlForm.CreateOrAlter, result.Form);
        Assert.StartsWith("-- WARNING:", result.Ddl);
        Assert.Single(result.Warnings);
    }

    /// <summary>
    /// Opt-in, read-only live check against a SQL Server 2008 R2 instance (never runs by default):
    /// set RUN_DCDEV_SCRIPTING_CHECK=1 and optionally DCDEV_CONNECTION_STRING (defaults to DC\DEV, Windows auth, master).
    /// Scripts the first visible object of each kind and checks programmable objects use the ALTER form.
    /// </summary>
    [SkippableFact]
    public async Task Live_sql2008r2_scripting_check()
    {
        Skip.IfNot(Environment.GetEnvironmentVariable("RUN_DCDEV_SCRIPTING_CHECK") == "1", "Set RUN_DCDEV_SCRIPTING_CHECK=1 to run the live 2008 R2 check.");
        var cs = Environment.GetEnvironmentVariable("DCDEV_CONNECTION_STRING")
            ?? "Server=DC\\DEV;Integrated Security=true;TrustServerCertificate=True;Initial Catalog=master";

        await using var conn = new SqlConnection(cs);
        await conn.OpenAsync();
        var version = await CatalogReader.GetVersionAsync(conn, CancellationToken.None);

        var candidates = new List<(string Type, string Name, string? Parent)>();
        async Task AddFirstAsync(string type, string sql)
        {
            await using var cmd = new SqlCommand(sql, conn);
            await using var r = await cmd.ExecuteReaderAsync();
            if (await r.ReadAsync())
            {
                candidates.Add((type, r.GetString(0), r.FieldCount > 1 ? r.GetString(1) : null));
            }
        }

        const string Qn = "QUOTENAME(SCHEMA_NAME(o.schema_id)) + '.' + QUOTENAME(o.name)";
        await AddFirstAsync("Table", $"SELECT TOP (1) {Qn} FROM sys.objects o WHERE o.type = 'U' ORDER BY o.is_ms_shipped, o.name");
        await AddFirstAsync("View", $"SELECT TOP (1) {Qn} FROM sys.objects o WHERE o.type = 'V' ORDER BY o.is_ms_shipped, o.name");
        await AddFirstAsync("StoredProcedure", $"SELECT TOP (1) {Qn} FROM sys.objects o WHERE o.type = 'P' AND o.is_ms_shipped = 0 ORDER BY o.name");
        await AddFirstAsync("ScalarFunction", $"SELECT TOP (1) {Qn} FROM sys.objects o WHERE o.type = 'FN' AND o.is_ms_shipped = 0 ORDER BY o.name");
        await AddFirstAsync("TableFunction", $"SELECT TOP (1) {Qn} FROM sys.objects o WHERE o.type IN ('IF','TF') AND o.is_ms_shipped = 0 ORDER BY o.name");
        await AddFirstAsync("TableTrigger", $"SELECT TOP (1) {Qn} FROM sys.objects o WHERE o.type = 'TR' ORDER BY o.name");
        await AddFirstAsync("ForeignKey", $"SELECT TOP (1) {Qn} FROM sys.objects o WHERE o.type = 'F' ORDER BY o.name");
        await AddFirstAsync("Index", """
            SELECT TOP (1) QUOTENAME(i.name), QUOTENAME(SCHEMA_NAME(o.schema_id)) + '.' + QUOTENAME(o.name)
            FROM sys.indexes i JOIN sys.objects o ON o.object_id = i.object_id WHERE o.type = 'U' AND i.type > 0 ORDER BY o.is_ms_shipped, o.name, i.index_id
            """);
        await AddFirstAsync("Type", "SELECT TOP (1) QUOTENAME(SCHEMA_NAME(schema_id)) + '.' + QUOTENAME(name) FROM sys.types WHERE is_user_defined = 1 ORDER BY name");
        await AddFirstAsync("DatabaseTrigger", "SELECT TOP (1) QUOTENAME(name) FROM sys.triggers WHERE parent_class = 0 ORDER BY name");
        await AddFirstAsync("DatabaseUser", "SELECT TOP (1) QUOTENAME(name) FROM sys.database_principals WHERE type IN ('S','U','G') ORDER BY name");
        await AddFirstAsync("DatabaseRole", "SELECT TOP (1) QUOTENAME(name) FROM sys.database_principals WHERE type = 'R' ORDER BY is_fixed_role, name");
        await AddFirstAsync("Login", "SELECT TOP (1) QUOTENAME(name) FROM sys.server_principals WHERE type IN ('S','U','G') ORDER BY name");
        await AddFirstAsync("ServerRole", "SELECT TOP (1) QUOTENAME(name) FROM sys.server_principals WHERE type = 'R' ORDER BY name");

        foreach (var (type, name, parent) in candidates)
        {
            var (result, error) = await ObjectScripter.ScriptAsync(conn, type, name, parent, CancellationToken.None);
            Assert.True(result is not null, $"{type} {name}: {error}");
            output.WriteLine($"{type} {name}: form={result!.Form}, warnings={result.Warnings.Count}, ddl={result.Ddl.Length} chars");
            if ((type is "StoredProcedure" or "ScalarFunction" or "TableFunction" or "TableTrigger") && !version.SupportsCreateOrAlter && result.Warnings.Count == 0)
            {
                Assert.Equal(DdlForm.Alter, result.Form);
                Assert.DoesNotContain("CREATE OR ALTER", result.Ddl, StringComparison.OrdinalIgnoreCase);
                Assert.Matches(@"(?i)\bALTER\s+(PROC|PROCEDURE|FUNCTION|TRIGGER)\b", result.Ddl);
            }
        }

        Assert.NotEmpty(candidates);
    }
}
