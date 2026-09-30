// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.

using Microsoft.Extensions.Logging;
using Moq;
using Mssql.McpServer;
using Mssql.McpServer.InsightsLayer;

namespace MssqlMcp.Tests
{
    public sealed class GetServerInfoTests
    {
        private readonly Tools _tools;

        public GetServerInfoTests()
        {
            TestConnectionString.EnsureInitialized();
            var connectionFactory = TestConnectionString.CreateFactory();
            var loggerMock = new Mock<ILogger<Tools>>();
            _tools = new Tools(
                connectionFactory,
                NoOpInsightsLayerService.Instance,
                NoOpInsightDdlProcessingQueue.Instance,
                loggerMock.Object);
        }

        [Fact]
        public async Task GetServerInfo_ReturnsSuccess_WhenConnectionIsValid()
        {
            var result = await _tools.GetServerInfo() as DbOperationResult;
            
            Assert.NotNull(result);
            Assert.True(result.Success);
            Assert.NotNull(result.Data);
            
            var data = result.Data as System.Collections.IDictionary;
            Assert.NotNull(data);
            
            // Verify all expected sections are present
            Assert.True(data.Contains("server"));
            Assert.True(data.Contains("hardware"));
            Assert.True(data.Contains("databases"));
            
            // Verify server section has expected properties
            var server = data["server"];
            Assert.NotNull(server);
            
            var serverType = server.GetType();
            Assert.NotNull(serverType.GetProperty("productVersion"));
            Assert.NotNull(serverType.GetProperty("edition"));
            Assert.NotNull(serverType.GetProperty("serverName"));
            
            // Verify hardware section has expected properties
            var hardware = data["hardware"];
            Assert.NotNull(hardware);
            
            var hardwareType = hardware.GetType();
            Assert.NotNull(hardwareType.GetProperty("cpuCount"));
            Assert.NotNull(hardwareType.GetProperty("physicalMemoryMB"));
            
            // Verify databases section has expected properties
            var databases = data["databases"];
            Assert.NotNull(databases);
            
            var databasesType = databases.GetType();
            Assert.NotNull(databasesType.GetProperty("totalDatabases"));
            Assert.NotNull(databasesType.GetProperty("onlineDatabases"));
        }
    }
}