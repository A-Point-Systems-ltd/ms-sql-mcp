// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.

using Microsoft.Extensions.Logging.Abstractions;
using Mssql.McpServer;
using Mssql.McpServer.Connections;
using Mssql.McpServer.Scripting;

namespace MssqlMcp.Tests.Scripting;

/// <summary>The run_script tool end to end through the real connection factory, on a throwaway LocalDB database.</summary>
public sealed class RunScriptToolTests
{
    [SkippableFact]
    public async Task Each_run_gets_a_new_session_so_session_settings_do_not_leak()
    {
        await using var scratch = await ScratchDatabases.CreateAsync(1);
        var profile = new ConnectionProfile("main", scratch.ConnectionStrings[0], ReadOnly: false, InsightsEnabled: false, ConnectionSource.Configured);
        var tools = new ScriptRunnerTools(new SqlConnectionFactory(new ConnectionRegistry([profile])), NullLogger<ScriptRunnerTools>.Instance);
        using var _ = CurrentConnection.Use(profile);

        var first = await tools.RunScript("SET TRANSACTION ISOLATION LEVEL SERIALIZABLE;");
        Assert.True(first.Success, first.Error);

        var second = await tools.RunScript("SELECT transaction_isolation_level FROM sys.dm_exec_sessions WHERE session_id = @@SPID");

        Assert.True(second.Success, second.Error);
        var run = Assert.IsType<ScriptRunResult>(second.Data);
        Assert.Equal((short)2, Assert.Single(run.ResultSets).Rows[0][0]); // 2 = READ COMMITTED
    }
}
