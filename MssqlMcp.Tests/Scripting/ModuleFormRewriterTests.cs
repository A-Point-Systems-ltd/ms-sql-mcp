// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.

using Mssql.McpServer.Scripting;

namespace MssqlMcp.Tests.Scripting;

public sealed class ModuleFormRewriterTests
{
    [Theory]
    [InlineData("CREATE PROCEDURE dbo.p AS SELECT 1", DdlForm.CreateOrAlter, "CREATE OR ALTER PROCEDURE dbo.p AS SELECT 1")]
    [InlineData("CREATE PROCEDURE dbo.p AS SELECT 1", DdlForm.Alter, "ALTER PROCEDURE dbo.p AS SELECT 1")]
    [InlineData("create   proc dbo.p as select 1", DdlForm.Alter, "ALTER   proc dbo.p as select 1")]
    [InlineData("-- header comment\r\n/* block */\r\nCREATE FUNCTION dbo.f() RETURNS int AS BEGIN RETURN 1 END", DdlForm.CreateOrAlter,
                "-- header comment\r\n/* block */\r\nCREATE OR ALTER FUNCTION dbo.f() RETURNS int AS BEGIN RETURN 1 END")]
    [InlineData("CREATE OR ALTER PROCEDURE dbo.p AS SELECT 1", DdlForm.Alter, "ALTER PROCEDURE dbo.p AS SELECT 1")]
    [InlineData("ALTER TRIGGER dbo.t ON dbo.x AFTER INSERT AS SELECT 1", DdlForm.CreateOrAlter, "CREATE OR ALTER TRIGGER dbo.t ON dbo.x AFTER INSERT AS SELECT 1")]
    [InlineData("CREATE PROCEDURE dbo.p AS SELECT 'CREATE'", DdlForm.Create, "CREATE PROCEDURE dbo.p AS SELECT 'CREATE'")]
    [InlineData("ALTER PROCEDURE dbo.p AS SELECT 1", DdlForm.Create, "CREATE PROCEDURE dbo.p AS SELECT 1")]
    [InlineData("CREATE OR ALTER PROCEDURE dbo.p AS SELECT 1", DdlForm.Create, "CREATE PROCEDURE dbo.p AS SELECT 1")]
    [InlineData("CREATE PROCEDURE dbo.p AS SELECT 'CREATE'", DdlForm.Alter, "ALTER PROCEDURE dbo.p AS SELECT 'CREATE'")]
    public void Rewrites_only_the_leading_keyword(string definition, DdlForm form, string expected)
    {
        Assert.Equal(expected, ModuleFormRewriter.Rewrite(definition, form, quotedIdentifier: true, out var warning));
        Assert.Null(warning);
    }

    [Theory]
    [InlineData("CREATE VIEW dbo.v AS SELECT 1 AS a", DdlForm.CreateOrAlter, "CREATE OR ALTER VIEW dbo.v AS SELECT 1 AS a")]
    [InlineData("CREATE VIEW dbo.v AS SELECT 1 AS a", DdlForm.Alter, "ALTER VIEW dbo.v AS SELECT 1 AS a")]
    [InlineData("/* v1 */\r\n-- note\r\n  create\r\n\tview [dbo].[v] with schemabinding as select 1 as a", DdlForm.Alter, "/* v1 */\r\n-- note\r\n  ALTER\r\n\tview [dbo].[v] with schemabinding as select 1 as a")]
    [InlineData("CREATE /* x */ OR /* y */ ALTER VIEW dbo.v AS SELECT 'CREATE VIEW'", DdlForm.Alter, "ALTER VIEW dbo.v AS SELECT 'CREATE VIEW'")]
    [InlineData("ALTER VIEW dbo.v AS SELECT 1 AS a", DdlForm.CreateOrAlter, "CREATE OR ALTER VIEW dbo.v AS SELECT 1 AS a")]
    public void Rewrites_the_leading_keyword_of_a_view(string definition, DdlForm form, string expected)
    {
        Assert.Equal(expected, ModuleFormRewriter.Rewrite(definition, form, quotedIdentifier: true, out var warning));
        Assert.Null(warning);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Quoted_identifier_setting_does_not_change_leading_keyword_rewrite(bool quotedIdentifier)
    {
        const string def = "CREATE PROCEDURE dbo.p AS SELECT \"literal\", 'CREATE'";
        Assert.Equal("ALTER PROCEDURE dbo.p AS SELECT \"literal\", 'CREATE'",
            ModuleFormRewriter.Rewrite(def, DdlForm.Alter, quotedIdentifier, out var warning));
        Assert.Null(warning);
    }

    [Fact]
    public void Unrecognized_start_is_returned_unchanged_with_warning()
    {
        Assert.Equal("EXEC x", ModuleFormRewriter.Rewrite("EXEC x", DdlForm.Alter, true, out var warning));
        Assert.NotNull(warning);
    }

    [Theory]
    [InlineData("10.50.6560.0", DdlForm.Alter)]
    [InlineData("13.0.5026.0", DdlForm.CreateOrAlter)]
    public void Programmable_form_is_chosen_by_version(string version, DdlForm expected)
    {
        Assert.Equal(expected, ModuleFormRewriter.ProgrammableFormFor(SqlServerVersion.Parse(version)));
    }
}
