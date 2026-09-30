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

        var fk = ddl.IndexOf("ALTER TABLE [sales].[Order Lines] WITH CHECK ADD CONSTRAINT [FK_OL_Orders] FOREIGN KEY ([OrderId]) REFERENCES [sales].[Orders] ([Id]) ON DELETE CASCADE", StringComparison.Ordinal);
        var ix = ddl.IndexOf("CREATE NONCLUSTERED INDEX [IX_OL_Order] ON [sales].[Order Lines] ([OrderId] ASC) INCLUDE ([Qty]) WHERE ([Qty]>(0))", StringComparison.Ordinal);
        var nocheck = ddl.IndexOf("ALTER TABLE [sales].[Order Lines] NOCHECK CONSTRAINT [CK_OL_Qty]", StringComparison.Ordinal);
        Assert.True(fk > 0 && ix > fk && nocheck > ix, ddl);
        Assert.Contains("sys.sp_addextendedproperty @name = N'MS_Description', @value = N'Order lines'", ddl);
        Assert.DoesNotContain("ON UPDATE NO ACTION", ddl);
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
