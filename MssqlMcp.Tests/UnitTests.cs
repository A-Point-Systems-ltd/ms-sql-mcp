// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.

using Microsoft.Extensions.Logging;
using Moq;
using Mssql.McpServer;
using Mssql.McpServer.InsightsLayer;

namespace MssqlMcp.Tests
{
    public sealed class MssqlMcpTests : IDisposable
    {
        private readonly string _tableName;
        private readonly Tools _tools;
        public MssqlMcpTests()
        {
            TestConnectionString.EnsureInitialized();
            _tableName = $"TestTable_{Guid.NewGuid():N}";
            var registry = TestConnectionString.CreateRegistry();
            var connectionFactory = TestConnectionString.CreateFactory(registry);
            var loggerMock = new Mock<ILogger<Tools>>();
            _tools = new Tools(
                connectionFactory,
                NoOpInsightsLayerService.Instance,
                NoOpInsightDdlProcessingQueue.Instance,
                loggerMock.Object,
                registry);
        }

        public void Dispose()
        {
            // Cleanup: Drop the table after each test
            var _ = _tools.DropTable($"DROP TABLE IF EXISTS {_tableName}").GetAwaiter().GetResult();
        }

        [Fact]
        public async Task ScriptObject_table_returns_create_ddl()
        {
            Assert.True((await _tools.CreateTable($"CREATE TABLE {_tableName} (Id INT PRIMARY KEY, Name NVARCHAR(50) NOT NULL DEFAULT (N'x'))")).Success);
            var result = await _tools.ScriptObject("Table", _tableName);
            Assert.True(result.Success, result.Error);
            Assert.Contains($"CREATE TABLE [dbo].[{_tableName}]", System.Text.Json.JsonSerializer.Serialize(result.Data));
        }

        [Theory]
        [InlineData("DatabaseTrigger")]
        [InlineData("Type")]
        [InlineData("Login")]
        [InlineData("ServerRole")]
        [InlineData("DatabaseUser")]
        [InlineData("DatabaseRole")]
        public async Task ListObjects_supports_new_types(string objectType)
        {
            var result = await _tools.ListObjects(objectType);
            Assert.True(result.Success, result.Error);
        }

        [Fact]
        public async Task ScriptObject_unknown_type_lists_supported_types()
        {
            var result = await _tools.ScriptObject("Widget", "x");
            Assert.False(result.Success);
            Assert.Contains("DatabaseRole", result.Error);
        }

        [Theory]
        [InlineData("guest")]
        [InlineData("sys")]
        [InlineData("INFORMATION_SCHEMA")]
        [InlineData("dbo")]
        public async Task ScriptObject_builtin_database_user_is_not_created(string user)
        {
            var result = await _tools.ScriptObject("DatabaseUser", user);
            Assert.True(result.Success, result.Error);
            var json = System.Text.Json.JsonSerializer.Serialize(result.Data);
            Assert.Contains("is a built-in principal and is not scripted.", json);
            Assert.DoesNotContain("CREATE USER", json);
        }

        [Fact]
        public async Task DescribeView_lists_view_indexes()
        {
            var viewName = $"V_{Guid.NewGuid():N}";
            Assert.True((await _tools.CreateTable($"CREATE TABLE {_tableName} (Id INT NOT NULL PRIMARY KEY)")).Success);
            try
            {
                Assert.True((await _tools.ExecuteSQL($"CREATE VIEW dbo.{viewName} WITH SCHEMABINDING AS SELECT Id FROM dbo.{_tableName}")).Success);
                Assert.True((await _tools.ExecuteSQL($"CREATE UNIQUE CLUSTERED INDEX IX_{viewName} ON dbo.{viewName} (Id)")).Success);

                var result = await _tools.DescribeView(viewName);
                Assert.True(result.Success, result.Error);
                var json = System.Text.Json.JsonSerializer.Serialize(result.Data);
                Assert.Contains($"\"name\":\"IX_{viewName}\"", json);
                Assert.Contains("\"isUnique\":true", json);
                Assert.Contains("\"keys\":\"Id\"", json);
            }
            finally
            {
                await _tools.ExecuteSQL($"DROP VIEW IF EXISTS dbo.{viewName}");
            }
        }

        [Fact]
        public async Task DescribeView_and_DescribeTable_return_index_keys_with_raw_characters()
        {
            var viewName = $"V_{Guid.NewGuid():N}";
            Assert.True((await _tools.CreateTable($"CREATE TABLE {_tableName} ([a&b] INT NOT NULL, [c<d] INT NOT NULL, CONSTRAINT PK_{_tableName} PRIMARY KEY ([a&b], [c<d]))")).Success);
            try
            {
                Assert.True((await _tools.ExecuteSQL($"CREATE INDEX IX_T_{_tableName} ON dbo.{_tableName} ([c<d], [a&b])")).Success);
                Assert.True((await _tools.ExecuteSQL($"CREATE VIEW dbo.{viewName} WITH SCHEMABINDING AS SELECT [a&b], [c<d] FROM dbo.{_tableName}")).Success);
                Assert.True((await _tools.ExecuteSQL($"CREATE UNIQUE CLUSTERED INDEX IX_{viewName} ON dbo.{viewName} ([a&b], [c<d])")).Success);
                var relaxed = new System.Text.Json.JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

                var view = await _tools.DescribeView(viewName);
                Assert.True(view.Success, view.Error);
                Assert.Contains("\"keys\":\"a&b,c<d\"", System.Text.Json.JsonSerializer.Serialize(view.Data, relaxed));

                var table = await _tools.DescribeTable(_tableName);
                Assert.True(table.Success, table.Error);
                var tableJson = System.Text.Json.JsonSerializer.Serialize(table.Data, relaxed);
                Assert.Contains("\"keys\":\"a&b,c<d\"", tableJson); // primary key constraint
                Assert.Contains("\"keys\":\"c<d,a&b\"", tableJson); // nonclustered index
                Assert.DoesNotContain("&amp;", tableJson);
            }
            finally
            {
                await _tools.ExecuteSQL($"DROP VIEW IF EXISTS dbo.{viewName}");
            }
        }

        [Fact]
        public async Task CreateTable_ReturnsSuccess_WhenSqlIsValid()
        {
            var sql = $"CREATE TABLE {_tableName} (Id INT PRIMARY KEY)";
            var result = await _tools.CreateTable(sql) as DbOperationResult;
            Assert.NotNull(result);
            Assert.True(result.Success);
        }

        [Fact]
        public async Task DescribeTable_ReturnsSchema_WhenTableExists()
        {
            // Ensure table exists
            var createResult = await _tools.CreateTable($"CREATE TABLE {_tableName} (Id INT PRIMARY KEY)") as DbOperationResult;
            Assert.NotNull(createResult);
            Assert.True(createResult.Success);

            var result = await _tools.DescribeTable(_tableName) as DbOperationResult;
            Assert.NotNull(result);
            Assert.True(result.Success);
            var dict = result.Data as System.Collections.IDictionary;
            Assert.NotNull(dict);
            Assert.True(dict.Contains("table"));
            Assert.True(dict.Contains("columns"));
            Assert.True(dict.Contains("indexes"));
            Assert.True(dict.Contains("constraints"));
            var table = dict["table"];
            Assert.NotNull(table);
            var tableType = table.GetType();
            Assert.NotNull(tableType.GetProperty("name"));
            Assert.NotNull(tableType.GetProperty("schema"));
            var columns = dict["columns"] as System.Collections.IEnumerable;
            Assert.NotNull(columns);
        }

        [Fact]
        public async Task DropTable_ReturnsSuccess_WhenSqlIsValid()
        {
            var sql = $"DROP TABLE IF EXISTS {_tableName}";
            var result = await _tools.DropTable(sql) as DbOperationResult;
            Assert.NotNull(result);
            Assert.True(result.Success);
        }

        [Fact]
        public async Task InsertData_ReturnsSuccess_WhenSqlIsValid()
        {
            // Ensure table exists
            var createResult = await _tools.CreateTable($"CREATE TABLE {_tableName} (Id INT PRIMARY KEY)") as DbOperationResult;
            Assert.NotNull(createResult);
            Assert.True(createResult.Success);

            var sql = $"INSERT INTO {_tableName} (Id) VALUES (1)";
            var result = await _tools.InsertData(sql) as DbOperationResult;
            Assert.NotNull(result);
            Assert.True(result.Success);
            Assert.True(result.RowsAffected.HasValue && result.RowsAffected.Value > 0);
        }

        [Fact]
        public async Task ListObjects_ReturnsTables_WhenObjectTypeIsTable()
        {
            var result = await _tools.ListObjects("Table") as DbOperationResult;
            Assert.NotNull(result);
            Assert.True(result.Success);
            Assert.NotNull(result.Data);
        }

        [Fact]
        public async Task ListObjects_FiltersTables_ByPartialName()
        {
            var createResult = await _tools.CreateTable($"CREATE TABLE {_tableName} (Id INT PRIMARY KEY)") as DbOperationResult;
            Assert.NotNull(createResult);
            Assert.True(createResult.Success);

            var suffix = _tableName[^8..];
            var result = await _tools.ListObjects("Table", partialName: suffix) as DbOperationResult;
            Assert.NotNull(result);
            Assert.True(result.Success);

            var tables = Assert.IsAssignableFrom<IEnumerable<string>>(result.Data);
            Assert.Contains(tables, t => t.EndsWith(_tableName, StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public async Task ReadData_ReturnsData_WhenSqlIsValid()
        {
            // Ensure table exists and has data
            var createResult = await _tools.CreateTable($"CREATE TABLE {_tableName} (Id INT PRIMARY KEY)") as DbOperationResult;
            Assert.NotNull(createResult);
            Assert.True(createResult.Success);
            var insertResult = await _tools.InsertData($"INSERT INTO {_tableName} (Id) VALUES (1)") as DbOperationResult;
            Assert.NotNull(insertResult);
            Assert.True(insertResult.Success);

            var sql = $"SELECT * FROM {_tableName}";
            var result = await _tools.ReadData(sql) as DbOperationResult;
            Assert.NotNull(result);
            Assert.True(result.Success);
            Assert.NotNull(result.Data);
        }

        [Fact]
        public async Task UpdateData_ReturnsSuccess_WhenSqlIsValid()
        {
            // Ensure table exists and has data
            var createResult = await _tools.CreateTable($"CREATE TABLE {_tableName} (Id INT PRIMARY KEY)") as DbOperationResult;
            Assert.NotNull(createResult);
            Assert.True(createResult.Success);
            var insertResult = await _tools.InsertData($"INSERT INTO {_tableName} (Id) VALUES (1)") as DbOperationResult;
            Assert.NotNull(insertResult);
            Assert.True(insertResult.Success);

            var sql = $"UPDATE {_tableName} SET Id = 2 WHERE Id = 1";
            var result = await _tools.UpdateData(sql) as DbOperationResult;
            Assert.NotNull(result);
            Assert.True(result.Success);
            Assert.True(result.RowsAffected.HasValue);
        }

        [Fact]
        public async Task CreateTable_ReturnsError_WhenSqlIsInvalid()
        {
            var sql = "CREATE TABLE";
            var result = await _tools.CreateTable(sql) as DbOperationResult;
            Assert.NotNull(result);
            Assert.False(result.Success);
            Assert.Contains("syntax", result.Error ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task DescribeTable_ReturnsError_WhenTableDoesNotExist()
        {
            var result = await _tools.DescribeTable("NonExistentTable") as DbOperationResult;
            Assert.NotNull(result);
            Assert.False(result.Success);
            Assert.Contains("Table 'NonExistentTable' not found.", result.Error ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task DropTable_ReturnsError_WhenSqlIsInvalid()
        {
            var sql = "DROP";
            var result = await _tools.DropTable(sql) as DbOperationResult;
            Assert.NotNull(result);
            Assert.False(result.Success);
            Assert.Contains("syntax", result.Error ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task InsertData_ReturnsError_WhenSqlIsInvalid()
        {
            var sql = "INSERT INTO TestTable";
            var result = await _tools.InsertData(sql) as DbOperationResult;
            Assert.NotNull(result);
            Assert.False(result.Success);
            Assert.Contains("syntax", result.Error ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task ReadData_ReturnsError_WhenSqlIsInvalid()
        {
            var sql = "SELECT FROM";
            var result = await _tools.ReadData(sql) as DbOperationResult;
            Assert.NotNull(result);
            Assert.False(result.Success);
            Assert.Contains("syntax", result.Error ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task ReadData_ReturnsError_WhenSqlIsNotSelect()
        {
            var result = await _tools.ReadData("UPDATE dbo.NonExistent SET x = 1") as DbOperationResult;
            Assert.NotNull(result);
            Assert.False(result.Success);
            Assert.Contains(ToolNames.ReadData, result.Error ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task ExecuteSQL_ReturnsError_WhenSqlIsSelect()
        {
            var result = await _tools.ExecuteSQL("SELECT 1") as DbOperationResult;
            Assert.NotNull(result);
            Assert.False(result.Success);
            Assert.Contains(ToolNames.ReadData, result.Error ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task ReadData_ReturnsData_WhenQueryUsesSysTables()
        {
            var result = await _tools.ReadData("SELECT TOP 1 name FROM sys.tables") as DbOperationResult;
            Assert.NotNull(result);
            Assert.True(result.Success);
            Assert.NotNull(result.Data);
        }

        [Fact]
        public async Task UpdateData_ReturnsError_WhenSqlIsInvalid()
        {
            var sql = "UPDATE TestTable";
            var result = await _tools.UpdateData(sql) as DbOperationResult;
            Assert.NotNull(result);
            Assert.False(result.Success);
            Assert.Contains("syntax", result.Error ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task SqlInjection_NotExecuted_When_QueryFails()
        {
            // Ensure table exists
            var createResult = await _tools.CreateTable($"CREATE TABLE {_tableName} (Id INT PRIMARY KEY, Name NVARCHAR(100))") as DbOperationResult;
            Assert.NotNull(createResult);
            Assert.True(createResult.Success);

            // Attempt SQL Injection
            var maliciousInput = "1; DROP TABLE " + _tableName + "; --";
            var sql = $"INSERT INTO {_tableName} (Id, Name) VALUES ({maliciousInput}, 'Malicious')";
            var result = await _tools.InsertData(sql) as DbOperationResult;

            Assert.NotNull(result);
            Assert.False(result.Success);
            Assert.Contains("syntax", result.Error ?? string.Empty, StringComparison.OrdinalIgnoreCase);

            // Verify table still exists
            var describeResult = await _tools.DescribeTable(_tableName) as DbOperationResult;
            Assert.NotNull(describeResult);
            Assert.True(describeResult.Success);
        }
    }
}