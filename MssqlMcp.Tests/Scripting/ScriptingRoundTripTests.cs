// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.

using Microsoft.Data.SqlClient;
using Mssql.McpServer.Scripting;

namespace MssqlMcp.Tests.Scripting;

/// <summary>Creates rich objects in a throwaway LocalDB database, scripts, drops, re-runs the script, re-scripts: identical DDL = complete + executable.</summary>
public sealed class ScriptingRoundTripTests
{
    private const string Server = "Server=(localdb)\\MSSQLLocalDB;Integrated Security=true;TrustServerCertificate=True";

    private static async Task ExecBatchesAsync(string cs, string script)
    {
        await using var conn = new SqlConnection(cs);
        await conn.OpenAsync();
        foreach (var batch in Mssql.McpServer.InsightsLayer.SqlBatchSplitter.SplitBatches(script))
        {
            await using var cmd = new SqlCommand(batch, conn);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    private static async Task<ScriptResult> ScriptAsync(string cs, string type, string name, string? parent = null)
    {
        await using var conn = new SqlConnection(cs);
        await conn.OpenAsync();
        var (result, error) = await ObjectScripter.ScriptAsync(conn, type, name, parent, CancellationToken.None);
        Assert.True(result is not null, error);
        return result!;
    }

    [SkippableFact]
    public async Task Table_type_view_and_module_scripts_round_trip()
    {
        var db = $"McpScript_{Guid.NewGuid():N}"[..24];
        try
        {
            await using (var master = new SqlConnection($"{Server};Initial Catalog=master"))
            {
                try { await master.OpenAsync(); } catch (SqlException ex) { throw new SkipException($"LocalDB unavailable: {ex.Message}"); }
                await using var create = new SqlCommand($"CREATE DATABASE [{db}]", master);
                await create.ExecuteNonQueryAsync();
            }

            var cs = $"{Server};Initial Catalog={db}";
            await ExecBatchesAsync(cs, """
                CREATE SCHEMA sales
                GO
                CREATE TYPE dbo.Phone FROM varchar(20) NOT NULL
                GO
                CREATE TABLE sales.Orders (Id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_Orders PRIMARY KEY CLUSTERED)
                GO
                CREATE TABLE sales.[Order Lines] (
                    Id int IDENTITY(10,5) NOT NULL CONSTRAINT PK_OL PRIMARY KEY CLUSTERED,
                    OrderId int NOT NULL,
                    Sku nvarchar(40) COLLATE Latin1_General_CI_AS NOT NULL,
                    Qty decimal(18,3) NOT NULL CONSTRAINT DF_OL_Qty DEFAULT (1),
                    Total AS (Qty * 2) PERSISTED,
                    Phone dbo.Phone NULL,
                    CONSTRAINT UQ_OL UNIQUE NONCLUSTERED (OrderId ASC, Sku DESC),
                    CONSTRAINT CK_OL_Qty CHECK (Qty >= 0))
                GO
                ALTER TABLE sales.[Order Lines] ADD CONSTRAINT FK_OL_Orders FOREIGN KEY (OrderId) REFERENCES sales.Orders (Id) ON DELETE CASCADE
                GO
                CREATE NONCLUSTERED INDEX IX_OL_Order ON sales.[Order Lines] (OrderId) INCLUDE (Qty) WHERE Qty > 0
                GO
                CREATE TYPE dbo.OrderLineList AS TABLE (Id int NOT NULL PRIMARY KEY, Sku nvarchar(40) NULL)
                GO
                CREATE VIEW sales.vOrders AS SELECT o.Id FROM sales.Orders o
                GO
                CREATE PROCEDURE sales.pGet @Id int AS SELECT Id FROM sales.Orders WHERE Id = @Id
                GO
                CREATE TRIGGER sales.trOL ON sales.[Order Lines] AFTER INSERT AS SET NOCOUNT ON
                """);

            var table = await ScriptAsync(cs, "Table", "sales.Order Lines");
            var ix = await ScriptAsync(cs, "Index", "IX_OL_Order", parent: "sales.Order Lines");
            var fk = await ScriptAsync(cs, "ForeignKey", "sales.FK_OL_Orders");
            var alias = await ScriptAsync(cs, "Type", "dbo.Phone");
            var tvp = await ScriptAsync(cs, "Type", "dbo.OrderLineList");
            var proc = await ScriptAsync(cs, "StoredProcedure", "sales.pGet");
            var trig = await ScriptAsync(cs, "TableTrigger", "sales.trOL");
            Assert.Contains("IX_OL_Order", table.Ddl);
            Assert.StartsWith("CREATE NONCLUSTERED INDEX [IX_OL_Order]", ix.Ddl);
            Assert.Contains("ON DELETE CASCADE", fk.Ddl);
            Assert.Equal(DdlForm.CreateOrAlter, proc.Form);  // LocalDB is 2016 SP1+
            Assert.Contains("CREATE OR ALTER PROCEDURE", proc.Ddl);

            // Drop everything the table script recreates, re-run it, and re-script: must be identical.
            await ExecBatchesAsync(cs, "DROP TABLE sales.[Order Lines]\nGO\nDROP TYPE dbo.OrderLineList\nGO\nDROP TYPE dbo.Phone");
            await ExecBatchesAsync(cs, alias.Ddl);
            await ExecBatchesAsync(cs, tvp.Ddl);
            await ExecBatchesAsync(cs, table.Ddl);
            await ExecBatchesAsync(cs, trig.Ddl);   // CREATE OR ALTER form recreates it
            Assert.Equal(table.Ddl, (await ScriptAsync(cs, "Table", "sales.Order Lines")).Ddl);
            Assert.Equal(tvp.Ddl, (await ScriptAsync(cs, "Type", "dbo.OrderLineList")).Ddl);
            await ExecBatchesAsync(cs, proc.Ddl);   // idempotent re-run
        }
        finally
        {
            await using var master = new SqlConnection($"{Server};Initial Catalog=master");
            try
            {
                await master.OpenAsync();
                await using var drop = new SqlCommand($"IF DB_ID(N'{db}') IS NOT NULL BEGIN ALTER DATABASE [{db}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{db}]; END", master);
                await drop.ExecuteNonQueryAsync();
            }
            catch (SqlException)
            {
            }
        }
    }
}
