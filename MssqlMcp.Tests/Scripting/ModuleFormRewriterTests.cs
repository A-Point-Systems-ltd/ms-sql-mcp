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
    public void Rewrites_only_the_leading_keyword(string definition, DdlForm form, string expected)
    {
        Assert.Equal(expected, ModuleFormRewriter.Rewrite(definition, form, quotedIdentifier: true, out var warning));
        Assert.Null(warning);
    }

    [Fact]
    public void Quoted_identifier_off_definition_is_tokenized_with_matching_setting()
    {
        const string def = "CREATE PROCEDURE dbo.p AS SELECT \"literal\"";
        Assert.StartsWith("ALTER PROCEDURE", ModuleFormRewriter.Rewrite(def, DdlForm.Alter, quotedIdentifier: false, out _));
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
