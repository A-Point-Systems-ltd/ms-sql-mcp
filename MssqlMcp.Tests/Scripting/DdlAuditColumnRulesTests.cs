// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.

using Mssql.McpServer.Scripting;

namespace MssqlMcp.Tests.Scripting;

/// <summary>
/// The existing-table guard of <c>ddl_history install</c> as a pure function: which column types and widths the
/// DDL_Audit trigger can write to. <c>max_length</c> is in bytes (-1 = max), so nvarchar(100) is 200.
/// </summary>
public sealed class DdlAuditColumnRulesTests
{
    private static DdlAudit.AuditColumn Col(
        string name, string type, short maxLength, bool nullable = true, bool identity = false, bool computed = false, bool hasDefault = false) =>
        new(name, type, maxLength, nullable, identity, computed, hasDefault);

    /// <summary>The table the create-only script makes: compatible, no warnings.</summary>
    private static List<DdlAudit.AuditColumn> Standard() =>
    [
        Col("ID", "int", 4, nullable: false, identity: true),
        Col("PostTime", "datetime", 8, nullable: false, hasDefault: true),
        Col("HostName", "varchar", 100),
        Col("LoginName", "varchar", 100),
        Col("SchemaName", "varchar", 100),
        Col("ObjectName", "varchar", 100),
        Col("ObjectType", "varchar", 100),
        Col("EventType", "varchar", 64),
        Col("CommandText", "nvarchar", -1),
        Col("CommandXML", "xml", -1),
        Col("ProgramName", "varchar", 100),
    ];

    private static List<DdlAudit.AuditColumn> Replace(List<DdlAudit.AuditColumn> columns, DdlAudit.AuditColumn column)
    {
        var i = columns.FindIndex(c => string.Equals(c.Name, column.Name, StringComparison.OrdinalIgnoreCase));
        columns[i] = column;
        return columns;
    }

    [Fact]
    public void The_standard_table_is_compatible_without_warnings()
    {
        var problems = DdlAudit.DescribeColumnProblems(Standard(), out var warnings);

        Assert.Empty(problems);
        Assert.Empty(warnings);
    }

    [Fact]
    public void Nvarchar_name_columns_count_characters_not_bytes()
    {
        var columns = Standard();
        Replace(columns, Col("ObjectName", "nvarchar", 200));
        Replace(columns, Col("EventType", "nvarchar", 128));
        Replace(columns, Col("LoginName", "nvarchar", -1));


        Assert.Empty(DdlAudit.DescribeColumnProblems(columns, out var warnings));
        Assert.Empty(warnings);
    }

    [Theory]
    [InlineData("ObjectName", "varchar", 10, "ObjectName varchar(10), needs at least 100 characters")]
    [InlineData("HostName", "nvarchar", 198, "HostName nvarchar(99), needs at least 100 characters")]
    [InlineData("SchemaName", "int", 4, "SchemaName int, needs varchar or nvarchar of at least 100 characters")]
    [InlineData("ProgramName", "char", 100, "ProgramName char(100), needs varchar or nvarchar of at least 100 characters")]
    [InlineData("ObjectType", "varchar", 50, "ObjectType varchar(50), needs at least 100 characters")]
    [InlineData("EventType", "varchar", 20, "EventType varchar(20), needs at least 64 characters")]
    [InlineData("EventType", "nvarchar", 100, "EventType nvarchar(50), needs at least 64 characters")]
    [InlineData("CommandText", "nvarchar", 8000, "CommandText nvarchar(4000), needs nvarchar(max) or varchar(max)")]
    [InlineData("CommandText", "text", 16, "CommandText text, needs nvarchar(max) or varchar(max)")]
    [InlineData("CommandXML", "nvarchar", 200, "CommandXML nvarchar(100), needs xml")]
    [InlineData("CommandXML", "nvarchar", -1, "CommandXML nvarchar(max), needs xml")]
    [InlineData("CommandXML", "varchar", -1, "CommandXML varchar(max), needs xml")]
    [InlineData("CommandXML", "varbinary", -1, "CommandXML varbinary(max), needs xml")]
    public void A_too_narrow_or_wrong_type_column_is_reported(string name, string type, short maxLength, string expected)
    {
        var columns = Replace(Standard(), Col(name, type, maxLength));

        Assert.Equal([expected], DdlAudit.DescribeColumnProblems(columns, out _));
    }

    [Fact]
    public void Varchar_max_command_text_is_compatible_with_a_lossy_warning()
    {
        var columns = Replace(Standard(), Col("CommandText", "varchar", -1));

        Assert.Empty(DdlAudit.DescribeColumnProblems(columns, out var warnings));
        Assert.Equal(["CommandText is varchar(max); non-Latin text in DDL will be stored lossy."], warnings);
    }

    [Fact]
    public void Computed_or_identity_trigger_columns_are_reported()
    {
        var columns = Standard();
        Replace(columns, Col("HostName", "varchar", 100, computed: true));
        Replace(columns, Col("EventType", "int", 4, nullable: false, identity: true));

        Assert.Equal(
            ["HostName is a computed column", "EventType is an identity column"],
            DdlAudit.DescribeColumnProblems(columns, out _));
    }

    [Fact]
    public void Id_and_post_time_must_exist_with_integer_and_date_time_types()
    {
        var missing = Standard().Where(c => c.Name is not ("ID" or "PostTime")).ToList();
        Assert.Equal(["ID missing", "PostTime missing"], DdlAudit.DescribeColumnProblems(missing, out _));

        var wrong = Standard();
        Replace(wrong, Col("ID", "uniqueidentifier", 16, nullable: false, hasDefault: true));
        Replace(wrong, Col("PostTime", "varchar", 30, nullable: false, hasDefault: true));
        Assert.Equal(
            ["ID uniqueidentifier, needs an integer type", "PostTime varchar(30), needs a date/time type"],
            DdlAudit.DescribeColumnProblems(wrong, out _));
    }

    [Theory]
    [InlineData("bigint", 8, "datetime2", 8)]
    [InlineData("smallint", 2, "datetimeoffset", 10)]
    [InlineData("tinyint", 1, "smalldatetime", 4)]
    [InlineData("int", 4, "date", 3)]
    public void Other_integer_and_date_time_types_are_accepted(string idType, short idLength, string timeType, short timeLength)
    {
        var columns = Standard();
        Replace(columns, Col("ID", idType, idLength, nullable: false, identity: true));
        Replace(columns, Col("PostTime", timeType, timeLength, nullable: false, hasDefault: true));

        Assert.Empty(DdlAudit.DescribeColumnProblems(columns, out _));
    }

    [Fact]
    public void Every_problem_is_listed_in_column_order_with_missing_and_not_null_columns()
    {
        var columns = new List<DdlAudit.AuditColumn>
        {
            Col("ID", "int", 4, nullable: false),
            Col("PostTime", "datetime", 8, nullable: false),
            Col("ObjectName", "varchar", 10),
            Col("CommandText", "nvarchar", -1),
            Col("Note", "nvarchar", 100, nullable: false),
        };

        Assert.Equal(
            [
                "HostName missing", "LoginName missing", "SchemaName missing", "ObjectName varchar(10), needs at least 100 characters",
                "ObjectType missing", "EventType missing", "CommandXML missing", "ProgramName missing",
                "ID NOT NULL without a default or identity", "PostTime NOT NULL without a default or identity",
                "Note NOT NULL without a default or identity",
            ],
            DdlAudit.DescribeColumnProblems(columns, out _));
    }

    [Fact]
    public void Column_names_compare_case_insensitively()
    {
        var columns = Standard().Select(c => c with { Name = c.Name.ToUpperInvariant() }).ToList();

        Assert.Empty(DdlAudit.DescribeColumnProblems(columns, out _));
    }

    [Fact]
    public void The_error_lists_each_problem()
    {
        Assert.Equal(
            "dbo.DDL_AuditLog exists but the DDL_Audit trigger cannot write to it: HostName missing; ObjectName varchar(10), needs at least 100 characters. Nothing was created.",
            DdlAudit.IncompatibleTableError(["HostName missing", "ObjectName varchar(10), needs at least 100 characters"]));
    }
}
