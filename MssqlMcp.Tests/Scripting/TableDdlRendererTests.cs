// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.

using Mssql.McpServer.Scripting;

namespace MssqlMcp.Tests.Scripting;

public sealed class TableDdlRendererTests
{
    private static ColumnMeta Col(string name, string type, short len = 0, byte p = 0, byte s = 0, bool nullable = true) =>
        new(name, type, null, len, p, s, nullable, null, false, null, null, false, null, false, null, null, false, false);

    [Theory]
    [InlineData("varchar", 50, 0, 0, "[varchar](50)")]
    [InlineData("varchar", -1, 0, 0, "[varchar](max)")]
    [InlineData("nvarchar", 100, 0, 0, "[nvarchar](50)")]
    [InlineData("nvarchar", -1, 0, 0, "[nvarchar](max)")]
    [InlineData("nchar", 20, 0, 0, "[nchar](10)")]
    [InlineData("varbinary", -1, 0, 0, "[varbinary](max)")]
    [InlineData("decimal", 9, 18, 4, "[decimal](18, 4)")]
    [InlineData("numeric", 5, 9, 0, "[numeric](9, 0)")]
    [InlineData("datetime2", 8, 27, 7, "[datetime2](7)")]
    [InlineData("time", 5, 16, 3, "[time](3)")]
    [InlineData("datetimeoffset", 10, 34, 7, "[datetimeoffset](7)")]
    [InlineData("float", 8, 53, 0, "[float]")]
    [InlineData("float", 4, 24, 0, "[float](24)")]
    [InlineData("int", 4, 10, 0, "[int]")]
    [InlineData("uniqueidentifier", 16, 0, 0, "[uniqueidentifier]")]
    public void Formats_types_like_ssms(string type, short len, byte p, byte s, string expected)
    {
        Assert.Equal(expected, TableDdlRenderer.FormatType(Col("c", type, len, p, s)));
    }

    [Fact]
    public void User_alias_type_is_schema_qualified_without_length()
    {
        var c = Col("c", "Phone", 40) with { UserTypeSchema = "dbo" };
        Assert.Equal("[dbo].[Phone]", TableDdlRenderer.FormatType(c));
    }

    private static TableMeta Orders() => new(
        "sales", "Order Lines", "Hebrew_CI_AI", "Order lines",
        [
            Col("Id", "int", 4, 10, 0, nullable: false) with { IsIdentity = true, IdentitySeed = "1", IdentityIncrement = "1" },
            Col("OrderId", "int", 4, 10, 0, nullable: false),
            Col("Sku", "nvarchar", 80, nullable: false) with { Collation = "Latin1_General_CI_AS" },
            Col("Qty", "decimal", 9, 18, 3, nullable: false) with { DefaultName = "DF_OL_Qty", DefaultDefinition = "((1))" },
            Col("Total", "decimal", 17, 38, 6) with { IsComputed = true, ComputedDefinition = "([Qty]*(2))", IsPersisted = true },
        ],
        [
            new("PK_OL", 1, true, true, false, null, false, [new("Id", false, false)]),
            new("UQ_OL_Sku", 2, true, false, true, null, false, [new("OrderId", false, false), new("Sku", true, false)]),
            new("IX_OL_Order", 2, false, false, false, "([Qty]>(0))", false, [new("OrderId", false, false), new("Qty", false, true)]),
        ],
        [new("CK_OL_Qty", "([Qty]>=(0))", false, true)],
        [new("FK_OL_Orders", "sales", "Order Lines", ["OrderId"], "sales", "Orders", ["Id"], "CASCADE", "NO_ACTION", false, false, false)],
        []);

    [Fact]
    public void Renders_create_table_with_inline_constraints()
    {
        var ddl = TableDdlRenderer.RenderTable(Orders(), includeDependents: false);

        Assert.Contains("CREATE TABLE [sales].[Order Lines](", ddl);
        Assert.Contains("[Id] [int] IDENTITY(1,1) NOT NULL,", ddl);
        Assert.Contains("[Sku] [nvarchar](40) COLLATE Latin1_General_CI_AS NOT NULL,", ddl);
        Assert.Contains("[Qty] [decimal](18, 3) NOT NULL CONSTRAINT [DF_OL_Qty] DEFAULT ((1)),", ddl);
        Assert.Contains("[Total] AS ([Qty]*(2)) PERSISTED,", ddl);
        Assert.Contains("CONSTRAINT [PK_OL] PRIMARY KEY CLUSTERED ([Id] ASC)", ddl);
        Assert.Contains("CONSTRAINT [UQ_OL_Sku] UNIQUE NONCLUSTERED ([OrderId] ASC, [Sku] DESC)", ddl);
        Assert.Contains("CONSTRAINT [CK_OL_Qty] CHECK ([Qty]>=(0))", ddl);
        Assert.DoesNotContain("FOREIGN KEY", ddl);
    }

    [Fact]
    public void Dependents_add_fk_index_nocheck_and_description_in_order()
    {
        var ddl = TableDdlRenderer.RenderTable(Orders(), includeDependents: true);

        var ix = ddl.IndexOf("CREATE NONCLUSTERED INDEX [IX_OL_Order] ON [sales].[Order Lines] ([OrderId] ASC) INCLUDE ([Qty]) WHERE ([Qty]>(0))", StringComparison.Ordinal);
        var fk = ddl.IndexOf("ALTER TABLE [sales].[Order Lines] WITH CHECK ADD CONSTRAINT [FK_OL_Orders] FOREIGN KEY ([OrderId]) REFERENCES [sales].[Orders] ([Id]) ON DELETE CASCADE", StringComparison.Ordinal);
        var ck = ddl.IndexOf("ALTER TABLE [sales].[Order Lines] WITH NOCHECK ADD CONSTRAINT [CK_OL_Qty] CHECK ([Qty]>=(0));", StringComparison.Ordinal);
        Assert.True(ix > 0 && fk > ix && ck > fk, ddl);
        Assert.DoesNotContain("\tCONSTRAINT [CK_OL_Qty]", ddl);
        Assert.DoesNotContain("NOCHECK CONSTRAINT", ddl);
        Assert.Contains("sys.sp_addextendedproperty @name = N'MS_Description', @value = N'Order lines'", ddl);
        Assert.DoesNotContain("ON UPDATE NO ACTION", ddl);
        Assert.StartsWith("SET ANSI_NULLS ON\r\nGO\r\nSET QUOTED_IDENTIFIER ON\r\nGO\r\nCREATE TABLE", ddl);
    }

    [Fact]
    public void Disabled_check_is_inline_and_nochecked_untrusted_one_is_not_inlined()
    {
        var t = Orders() with { Checks = [new("CK_D", "([Qty]>(1))", true, true), new("CK_U", "([Qty]>(2))", false, true)] };
        var ddl = TableDdlRenderer.RenderTable(t, includeDependents: true);
        Assert.Contains("CONSTRAINT [CK_D] CHECK ([Qty]>(1))", ddl);
        Assert.Contains("ALTER TABLE [sales].[Order Lines] NOCHECK CONSTRAINT [CK_D];", ddl);
        Assert.Contains("WITH NOCHECK ADD CONSTRAINT [CK_U] CHECK ([Qty]>(2));", ddl);
        Assert.DoesNotContain("NOCHECK CONSTRAINT [CK_U]", ddl);
        Assert.DoesNotContain("\tCONSTRAINT [CK_U]", ddl);
    }

    [Fact]
    public void Sparse_follows_collate()
    {
        var c = Col("c", "varchar", 10) with { Collation = "X_CI", IsSparse = true };
        var ddl = TableDdlRenderer.RenderTable(Orders() with { Columns = [c], Indexes = [], Checks = [], ForeignKeys = [] }, false);
        Assert.Contains("[c] [varchar](10) COLLATE X_CI SPARSE NULL", ddl);
    }

    [Fact]
    public void Collation_equal_to_database_is_suppressed_and_alias_types_warn_instead_of_collate()
    {
        var same = Col("a", "varchar", 10) with { Collation = "hebrew_ci_ai" };
        var alias = Col("b", "Phone", 10) with { UserTypeSchema = "dbo", Collation = "Latin1_General_CI_AS" };
        var ddl = TableDdlRenderer.RenderTable(Orders() with { Columns = [same, alias], Indexes = [], Checks = [], ForeignKeys = [] }, false);
        Assert.Contains("[a] [varchar](10) NULL", ddl);
        Assert.Contains("[b] [dbo].[Phone] NULL", ddl);
        Assert.DoesNotContain("COLLATE", ddl.Replace("collation", ""));
        Assert.Contains("-- WARNING: column [b] uses alias type [dbo].[Phone] with collation Latin1_General_CI_AS; alias-typed columns take the database collation and this cannot be reproduced.", ddl);
    }

    [Fact]
    public void Rowguidcol_is_rendered_before_nullability()
    {
        var c = Col("g", "uniqueidentifier", 16, nullable: false) with { IsRowGuidCol = true };
        var ddl = TableDdlRenderer.RenderTable(Orders() with { Columns = [c], Indexes = [], Checks = [], ForeignKeys = [] }, false);
        Assert.Contains("[g] [uniqueidentifier] ROWGUIDCOL NOT NULL", ddl);
    }

    [Fact]
    public void Foreign_key_actions_not_for_replication_and_disabled()
    {
        var fk = new ForeignKeyMeta("FK_X", "dbo", "c", ["a", "b"], "dbo", "p", ["x", "y"], "SET_NULL", "CASCADE", true, true, true);
        Assert.Equal(
            "ALTER TABLE [dbo].[c] WITH NOCHECK ADD CONSTRAINT [FK_X] FOREIGN KEY ([a], [b]) REFERENCES [dbo].[p] ([x], [y]) ON DELETE SET NULL ON UPDATE CASCADE NOT FOR REPLICATION;\r\nGO\r\nALTER TABLE [dbo].[c] NOCHECK CONSTRAINT [FK_X];",
            TableDdlRenderer.RenderForeignKey(fk));
    }

    [Fact]
    public void Disabled_indexes_are_emitted_last()
    {
        var disabled = new IndexMeta("IX_D", 2, false, false, false, null, true, [new("OrderId", false, false)]);
        var t = Orders() with { Indexes = [Orders().Indexes[0], disabled] };
        var ddl = TableDdlRenderer.RenderTable(t, includeDependents: true);
        var dis = ddl.IndexOf("ALTER INDEX [IX_D] ON [sales].[Order Lines] DISABLE;", StringComparison.Ordinal);
        Assert.True(dis > ddl.IndexOf("sp_addextendedproperty", StringComparison.Ordinal), ddl);
        Assert.True(dis > ddl.IndexOf("FOREIGN KEY", StringComparison.Ordinal), ddl);
        Assert.Equal(dis, ddl.LastIndexOf("ALTER INDEX", StringComparison.Ordinal));
    }

    [Fact]
    public void Disabled_constraint_backed_index_adds_warning()
    {
        var pk = new IndexMeta("PK_D", 1, true, true, false, null, true, [new("Id", false, false)]);
        var ddl = TableDdlRenderer.RenderTable(Orders() with { Indexes = [pk] }, false);
        Assert.Contains("-- WARNING: constraint [PK_D] is disabled on the source", ddl);
    }

    [Fact]
    public void Render_table_returns_every_warning_it_comments()
    {
        var pk = new IndexMeta("PK_D", 1, true, true, false, null, true, [new("Id", false, false)]);
        var xml = new IndexMeta("IX_Xml", 3, false, false, false, null, false, [new("Sku", false, false)]);
        var alias = Col("Phone", "Phone", 20) with { UserTypeSchema = "dbo", Collation = "Latin1_General_BIN" };
        var t = Orders() with { Columns = [.. Orders().Columns, alias], Indexes = [pk, xml], Warnings = ["table is partitioned"] };

        var ddl = TableDdlRenderer.RenderTable(t, includeDependents: true, out var warnings);

        Assert.Equal(4, warnings.Count);
        Assert.Equal("table is partitioned", warnings[0]);
        Assert.Contains(warnings, w => w.Contains("alias type [dbo].[Phone]", StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.Contains("constraint [PK_D] is disabled", StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.Contains("IX_Xml is a XML index", StringComparison.Ordinal));
        Assert.All(warnings, w => Assert.Contains("-- WARNING: " + w, ddl));
    }

    [Fact]
    public void Table_type_reports_alias_collation_and_constraint_warnings()
    {
        var alias = Col("Phone", "Phone", 20) with { UserTypeSchema = "dbo", Collation = "Latin1_General_BIN" };
        var pk = new IndexMeta("PK_TT", 7, true, true, false, null, false, [new("Id", false, false)]);
        var shape = Orders() with { Columns = [Col("Id", "int", 4, 10, 0, nullable: false), alias], Indexes = [pk], Checks = [], Warnings = ["from reader"] };

        var ddl = TableDdlRenderer.RenderTableType("dbo", "TT", shape, out var warnings);

        Assert.Equal(3, warnings.Count);
        Assert.Equal("from reader", warnings[0]);
        Assert.Contains(warnings, w => w.Contains("alias type [dbo].[Phone]", StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.Contains("constraint [PK_TT] is backed by index type 7", StringComparison.Ordinal));
        Assert.All(warnings, w => Assert.Contains("-- WARNING: " + w, ddl));
        Assert.DoesNotContain("COLLATE", ddl);
    }

    [Fact]
    public void Constraint_with_unexpected_index_type_is_warned_not_mislabelled()
    {
        var pk = new IndexMeta("PK_H", 7, true, true, false, null, false, [new("Id", false, false)]);
        var ddl = TableDdlRenderer.RenderIndex("dbo", "t", pk, out var warning);
        Assert.NotNull(warning);
        Assert.Contains("NONCLUSTERED", ddl);
        Assert.DoesNotContain(" CLUSTERED", ddl.Replace("NONCLUSTERED", ""));
    }

    [Fact]
    public void Constraint_backed_index_renders_as_add_constraint()
    {
        var ddl = TableDdlRenderer.RenderIndex("sales", "Order Lines", Orders().Indexes[0], out var warning);
        Assert.Null(warning);
        Assert.Equal("ALTER TABLE [sales].[Order Lines] ADD CONSTRAINT [PK_OL] PRIMARY KEY CLUSTERED ([Id] ASC);", ddl);
    }

    [Fact]
    public void Unsupported_index_type_is_a_warning_not_ddl()
    {
        var xml = new IndexMeta("XIX", 3, false, false, false, null, false, [new("Doc", false, false)]);
        var ddl = TableDdlRenderer.RenderIndex("dbo", "t", xml, out var warning);
        Assert.StartsWith("-- WARNING", ddl);
        Assert.NotNull(warning);
    }

    [Fact]
    public void Ignore_dup_key_is_emitted_on_constraints_and_indexes_only_when_set()
    {
        var pk = Orders().Indexes[0] with { IgnoreDupKey = true };
        var uq = Orders().Indexes[1];
        var ix = new IndexMeta("UX_Sku", 2, true, false, false, null, false, [new("Sku", false, false)], IgnoreDupKey: true);
        var ddl = TableDdlRenderer.RenderTable(Orders() with { Indexes = [pk, uq, ix] }, includeDependents: true);

        Assert.Contains("CONSTRAINT [PK_OL] PRIMARY KEY CLUSTERED ([Id] ASC) WITH (IGNORE_DUP_KEY = ON)", ddl);
        Assert.Contains("CONSTRAINT [UQ_OL_Sku] UNIQUE NONCLUSTERED ([OrderId] ASC, [Sku] DESC)", ddl);
        Assert.DoesNotContain("[Sku] DESC) WITH", ddl);
        Assert.Contains("CREATE UNIQUE NONCLUSTERED INDEX [UX_Sku] ON [sales].[Order Lines] ([Sku] ASC) WITH (IGNORE_DUP_KEY = ON);", ddl);
        Assert.Equal(
            "ALTER TABLE [sales].[Order Lines] ADD CONSTRAINT [PK_OL] PRIMARY KEY CLUSTERED ([Id] ASC) WITH (IGNORE_DUP_KEY = ON);",
            TableDdlRenderer.RenderIndex("sales", "Order Lines", pk, out _));
        Assert.Contains("PRIMARY KEY CLUSTERED ([Id] ASC) WITH (IGNORE_DUP_KEY = ON)",
            TableDdlRenderer.RenderTableType("dbo", "TT", Orders() with { Indexes = [pk], Checks = [], ForeignKeys = [] }));
    }

    [Fact]
    public void Not_for_replication_on_identity_and_check()
    {
        var id = Col("Id", "int", 4, 10, 0, nullable: false) with { IsIdentity = true, IdentitySeed = "5", IdentityIncrement = "2", IdentityNotForReplication = true };
        var t = Orders() with
        {
            Columns = [id],
            Indexes = [],
            ForeignKeys = [],
            Checks = [new("CK_T", "([Id]>(0))", false, false, NotForReplication: true), new("CK_U", "([Id]>(1))", false, true, NotForReplication: true)],
        };
        var ddl = TableDdlRenderer.RenderTable(t, includeDependents: true);

        Assert.Contains("[Id] [int] IDENTITY(5,2) NOT FOR REPLICATION NOT NULL", ddl);
        Assert.Contains("CONSTRAINT [CK_T] CHECK NOT FOR REPLICATION ([Id]>(0))", ddl);
        Assert.Contains("WITH NOCHECK ADD CONSTRAINT [CK_U] CHECK NOT FOR REPLICATION ([Id]>(1));", ddl);
    }

    [Fact]
    public void Non_default_filegroup_is_emitted_on_table_constraints_and_indexes()
    {
        var pk = Orders().Indexes[0] with { FileGroup = "DATA" };
        var ix = Orders().Indexes[2] with { FileGroup = "IDX]1" };
        var ddl = TableDdlRenderer.RenderTable(Orders() with { Indexes = [pk, ix], FileGroup = "DATA" }, includeDependents: true);

        Assert.Contains("CONSTRAINT [PK_OL] PRIMARY KEY CLUSTERED ([Id] ASC) ON [DATA]", ddl);
        Assert.Contains("\r\n) ON [DATA];", ddl);
        Assert.Contains("CREATE NONCLUSTERED INDEX [IX_OL_Order] ON [sales].[Order Lines] ([OrderId] ASC) INCLUDE ([Qty]) WHERE ([Qty]>(0)) ON [IDX]]1];", ddl);
        Assert.DoesNotContain(" ON [DATA]", TableDdlRenderer.RenderTableType("dbo", "TT", Orders() with { Indexes = [pk], Checks = [], ForeignKeys = [] }));
    }

    [Fact]
    public void Lob_filegroup_is_emitted_as_textimage_on_after_the_table_filegroup()
    {
        var ddl = TableDdlRenderer.RenderTable(Orders() with { FileGroup = "DATA", LobFileGroup = "LOB]1" }, includeDependents: false);
        Assert.Contains("\r\n) ON [DATA] TEXTIMAGE_ON [LOB]]1];", ddl);

        var lobOnly = TableDdlRenderer.RenderTable(Orders() with { LobFileGroup = "LOB" }, includeDependents: false);
        Assert.Contains("\r\n) TEXTIMAGE_ON [LOB];", lobOnly);
        Assert.DoesNotContain("TEXTIMAGE_ON", TableDdlRenderer.RenderTable(Orders(), includeDependents: true));
    }

    [Fact]
    public void Default_options_add_nothing()
    {
        var ddl = TableDdlRenderer.RenderTable(Orders(), includeDependents: true);
        Assert.DoesNotContain("IGNORE_DUP_KEY", ddl);
        Assert.DoesNotContain("NOT FOR REPLICATION", ddl);
        Assert.Contains("\r\n);", ddl);
    }

    [Fact]
    public void Alias_and_table_types()
    {
        Assert.Equal("CREATE TYPE [dbo].[Phone] FROM [varchar](20) NOT NULL;",
            TableDdlRenderer.RenderAliasType("dbo", "Phone", Col("x", "varchar", 20, nullable: false)));

        var shape = Orders() with { ForeignKeys = [], Checks = [], Indexes = [Orders().Indexes[0]] };
        var ddl = TableDdlRenderer.RenderTableType("dbo", "OrderLineList", shape);
        Assert.StartsWith("CREATE TYPE [dbo].[OrderLineList] AS TABLE(", ddl);
        Assert.Contains("PRIMARY KEY CLUSTERED ([Id] ASC)", ddl);
        Assert.DoesNotContain("CONSTRAINT [PK_OL]", ddl);
    }
}
