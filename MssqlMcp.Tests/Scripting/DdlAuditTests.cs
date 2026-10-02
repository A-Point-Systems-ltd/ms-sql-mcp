// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.

using Microsoft.Extensions.Logging.Abstractions;
using Mssql.McpServer;
using Mssql.McpServer.Connections;
using Mssql.McpServer.Scripting;

namespace MssqlMcp.Tests.Scripting;

/// <summary>The extension-only ddl_history tool end to end on throwaway LocalDB databases.</summary>
public sealed class DdlAuditTests
{
    private const string TableExistsSql = "SELECT CASE WHEN OBJECT_ID(N'dbo.DDL_AuditLog', N'U') IS NULL THEN 0 ELSE 1 END";
    private const string TriggerCountSql = "SELECT COUNT(*) FROM sys.triggers WHERE parent_class = 0 AND name = N'DDL_Audit'";
    private const string TriggerDefinitionSql = "SELECT OBJECT_DEFINITION(object_id) FROM sys.triggers WHERE parent_class = 0 AND name = N'DDL_Audit'";

    private static async Task<DbOperationResult> CallAsync(
        string cs, bool readOnly, string action, string? schema = null, string? name = null, long? id = null, int top = 100)
    {
        var profile = new ConnectionProfile(readOnly ? "ro" : "main", cs, ReadOnly: readOnly, InsightsEnabled: false, ConnectionSource.Configured);
        var tools = new ScriptRunnerTools(new SqlConnectionFactory(new ConnectionRegistry([profile])), NullLogger<ScriptRunnerTools>.Instance);
        using var _ = CurrentConnection.Use(profile);
        return await tools.DdlHistory(action, schema, name, id, top);
    }

    /// <summary>The status without the server/database names, which differ per scratch database.</summary>
    private static DdlAuditStatus Core(object? data) =>
        Assert.IsType<DdlAuditStatus>(data) with { ServerName = null, DatabaseName = null };

    private static async Task<DdlAuditInstallResult> InstallAsync(string cs)
    {
        var result = await CallAsync(cs, readOnly: false, "install");
        Assert.True(result.Success, result.Error);
        return Assert.IsType<DdlAuditInstallResult>(result.Data);
    }

    [SkippableFact]
    public async Task Status_on_an_empty_database_is_all_false_and_can_install_only_on_read_write()
    {
        await using var scratch = await ScratchDatabases.CreateAsync(1);
        var cs = scratch.ConnectionStrings[0];

        var rw = await CallAsync(cs, readOnly: false, "status");
        var ro = await CallAsync(cs, readOnly: true, "STATUS");

        Assert.True(rw.Success, rw.Error);
        Assert.Equal(new DdlAuditStatus(false, TableCompatible: false, false, false, CanInstall: true), Core(rw.Data));
        Assert.True(ro.Success, ro.Error);
        Assert.Equal(new DdlAuditStatus(false, TableCompatible: false, false, false, CanInstall: false), Core(ro.Data));
    }

    [SkippableFact]
    public async Task Install_creates_table_and_enabled_trigger_and_a_second_install_is_a_no_op()
    {
        await using var scratch = await ScratchDatabases.CreateAsync(1);
        var cs = scratch.ConnectionStrings[0];

        var first = await InstallAsync(cs);
        var definition = await ScratchDatabases.ScalarAsync<string>(cs, TriggerDefinitionSql);
        var second = await InstallAsync(cs);

        Assert.Equal(new DdlAuditInstallResult(CreatedTable: true, CreatedTrigger: true, TriggerEnabled: true), first);
        Assert.Equal(new DdlAuditInstallResult(CreatedTable: false, CreatedTrigger: false, TriggerEnabled: true), second);
        Assert.Equal(definition, await ScratchDatabases.ScalarAsync<string>(cs, TriggerDefinitionSql));
        Assert.Equal(1, await ScratchDatabases.ScalarAsync<int>(cs, TriggerCountSql));
        var status = await CallAsync(cs, readOnly: false, "status");
        Assert.Equal(new DdlAuditStatus(true, TableCompatible: true, true, true, CanInstall: false), Core(status.Data));

        // The exact table definition from the brief: named default and named primary key.
        Assert.Equal(1, await ScratchDatabases.ScalarAsync<int>(cs, "SELECT COUNT(*) FROM sys.default_constraints WHERE name = N'DF_DDL_Audit_PostTime' AND parent_object_id = OBJECT_ID(N'dbo.DDL_AuditLog')"));
        Assert.Equal(1, await ScratchDatabases.ScalarAsync<int>(cs, "SELECT COUNT(*) FROM sys.key_constraints WHERE name = N'PK_DDL_AuditLog' AND parent_object_id = OBJECT_ID(N'dbo.DDL_AuditLog')"));
    }

    [SkippableFact]
    public async Task An_existing_custom_trigger_is_left_byte_identical_and_only_the_table_is_created()
    {
        await using var scratch = await ScratchDatabases.CreateAsync(1);
        var cs = scratch.ConnectionStrings[0];
        await ScratchDatabases.ExecAsync(cs, "CREATE TRIGGER [DDL_Audit] ON DATABASE FOR CREATE_PROCEDURE AS SET NOCOUNT ON; -- custom body");
        var before = await ScratchDatabases.ScalarAsync<string>(cs, TriggerDefinitionSql);

        var result = await InstallAsync(cs);

        Assert.Equal(new DdlAuditInstallResult(CreatedTable: true, CreatedTrigger: false, TriggerEnabled: true), result);
        Assert.Equal(before, await ScratchDatabases.ScalarAsync<string>(cs, TriggerDefinitionSql));
        Assert.Equal(1, await ScratchDatabases.ScalarAsync<int>(cs, TableExistsSql));
    }

    [SkippableFact]
    public async Task A_disabled_existing_trigger_stays_disabled_with_a_warning()
    {
        await using var scratch = await ScratchDatabases.CreateAsync(1);
        var cs = scratch.ConnectionStrings[0];
        await InstallAsync(cs);
        await ScratchDatabases.ExecAsync(cs, "DISABLE TRIGGER [DDL_Audit] ON DATABASE");

        var result = await InstallAsync(cs);

        Assert.False(result.CreatedTable);
        Assert.False(result.CreatedTrigger);
        Assert.False(result.TriggerEnabled);
        Assert.Equal("DDL_Audit exists but is disabled; it was left unchanged.", result.Warning);
        Assert.True(await ScratchDatabases.ScalarAsync<bool>(cs, "SELECT is_disabled FROM sys.triggers WHERE parent_class = 0 AND name = N'DDL_Audit'"));
    }

    [SkippableFact]
    public async Task List_returns_create_and_alter_newest_first_and_get_returns_the_alter_text()
    {
        await using var scratch = await ScratchDatabases.CreateAsync(1);
        var cs = scratch.ConnectionStrings[0];
        await InstallAsync(cs);
        await ScratchDatabases.ExecAsync(cs, "CREATE PROCEDURE dbo.p AS SELECT 1");
        const string alter = "ALTER PROCEDURE dbo.p AS SELECT 2";
        await ScratchDatabases.ExecAsync(cs, alter);
        await ScratchDatabases.ExecAsync(cs, "CREATE PROCEDURE dbo.other AS SELECT 3");

        var list = await CallAsync(cs, readOnly: true, "list", schema: "dbo", name: "p");

        Assert.True(list.Success, list.Error);
        var entries = Assert.IsAssignableFrom<IReadOnlyList<DdlAuditEntry>>(list.Data);
        Assert.Equal(2, entries.Count);
        Assert.Equal(["ALTER_PROCEDURE", "CREATE_PROCEDURE"], entries.Select(e => e.EventType));
        Assert.True(entries[0].Id > entries[1].Id);
        Assert.Equal("dbo", entries[0].SchemaName);
        Assert.Equal("PROCEDURE", entries[0].ObjectType);
        Assert.Equal(alter.Length, entries[0].Length);
        Assert.True(DateTime.TryParseExact(entries[0].PostTime, "yyyy-MM-ddTHH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out _), entries[0].PostTime);

        var padded = await CallAsync(cs, readOnly: true, "list", schema: " dbo ", name: " p ");
        Assert.Equal(entries.Select(e => e.Id), Assert.IsAssignableFrom<IReadOnlyList<DdlAuditEntry>>(padded.Data).Select(e => e.Id));

        var noSchema = await CallAsync(cs, readOnly: true, "list", name: "p", top: 1);
        Assert.Equal(entries[0].Id, Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<DdlAuditEntry>>(noSchema.Data)).Id);
        var otherSchema = await CallAsync(cs, readOnly: true, "list", schema: "sales", name: "p");
        Assert.Empty(Assert.IsAssignableFrom<IReadOnlyList<DdlAuditEntry>>(otherSchema.Data));

        var get = await CallAsync(cs, readOnly: true, "get", id: entries[0].Id);
        Assert.True(get.Success, get.Error);
        var command = Assert.IsType<DdlAuditCommand>(get.Data);
        Assert.Equal(alter, command.CommandText);
        Assert.Equal("p", command.ObjectName);
        Assert.Equal("ALTER_PROCEDURE", command.EventType);

        var unknown = await CallAsync(cs, readOnly: true, "get", id: int.MaxValue);
        Assert.False(unknown.Success);
        Assert.NotNull(unknown.Error);
    }

    [SkippableFact]
    public async Task Read_only_install_is_refused_and_creates_nothing()
    {
        await using var scratch = await ScratchDatabases.CreateAsync(1);
        var cs = scratch.ConnectionStrings[0];

        var result = await CallAsync(cs, readOnly: true, "install");

        Assert.False(result.Success);
        Assert.Equal("Connection 'ro' is read-only; the DDL history table and trigger can only be created on a read/write connection.", result.Error);
        Assert.Equal(0, await ScratchDatabases.ScalarAsync<int>(cs, TableExistsSql));
        Assert.Equal(0, await ScratchDatabases.ScalarAsync<int>(cs, TriggerCountSql));
    }

    [SkippableFact]
    public async Task List_without_the_table_and_bad_arguments_give_errors()
    {
        await using var scratch = await ScratchDatabases.CreateAsync(1);
        var cs = scratch.ConnectionStrings[0];

        var list = await CallAsync(cs, readOnly: false, "list", name: "p");
        Assert.False(list.Success);
        Assert.Equal("DDL history is not installed on this database (dbo.DDL_AuditLog is missing).", list.Error);

        var get = await CallAsync(cs, readOnly: false, "get", id: 1);
        Assert.False(get.Success);
        Assert.Equal("DDL history is not installed on this database (dbo.DDL_AuditLog is missing).", get.Error);

        Assert.False((await CallAsync(cs, readOnly: false, "list")).Success);
        Assert.False((await CallAsync(cs, readOnly: false, "get")).Success);
        var bad = await CallAsync(cs, readOnly: false, "drop");
        Assert.False(bad.Success);
        Assert.Equal("action must be status, install, list or get.", bad.Error);
    }

    [SkippableFact]
    public async Task An_incompatible_existing_table_blocks_the_trigger_and_names_what_is_missing()
    {
        await using var scratch = await ScratchDatabases.CreateAsync(1);
        var cs = scratch.ConnectionStrings[0];
        await ScratchDatabases.ExecAsync(
            cs,
            "CREATE TABLE dbo.DDL_AuditLog (ID int NOT NULL PRIMARY KEY, PostTime datetime NOT NULL, ObjectName varchar(100) NULL, CommandText nvarchar(max) NULL)");

        var status = await CallAsync(cs, readOnly: false, "status");
        Assert.Equal(new DdlAuditStatus(true, TableCompatible: false, false, false, CanInstall: false), Core(status.Data));

        var install = await CallAsync(cs, readOnly: false, "install");

        Assert.False(install.Success);
        Assert.Equal(
            "dbo.DDL_AuditLog exists but the DDL_Audit trigger cannot write to it: HostName missing; LoginName missing; SchemaName missing; "
            + "ObjectType missing; EventType missing; CommandXML missing; ProgramName missing; ID NOT NULL without a default or identity; "
            + "PostTime NOT NULL without a default or identity. Nothing was created.",
            install.Error);
        Assert.Equal(0, await ScratchDatabases.ScalarAsync<int>(cs, TriggerCountSql));
    }

    [SkippableFact]
    public async Task An_existing_table_with_narrow_or_wrong_typed_columns_blocks_the_trigger_and_names_each_problem()
    {
        await using var scratch = await ScratchDatabases.CreateAsync(1);
        var cs = scratch.ConnectionStrings[0];
        await ScratchDatabases.ExecAsync(cs, """
            CREATE TABLE dbo.DDL_AuditLog (
                ID int IDENTITY(1,1) NOT NULL PRIMARY KEY, PostTime datetime NOT NULL DEFAULT (getdate()),
                HostName varchar(100) NULL, LoginName nvarchar(100) NULL, SchemaName varchar(100) NULL, ObjectName varchar(50) NULL,
                ObjectType varchar(100) NULL, EventType varchar(20) NULL, CommandText nvarchar(4000) NULL, CommandXML xml NULL,
                ProgramName AS CAST('x' AS varchar(100)))
            """);

        var status = await CallAsync(cs, readOnly: false, "status");
        var install = await CallAsync(cs, readOnly: false, "install");

        Assert.Equal(new DdlAuditStatus(true, TableCompatible: false, false, false, CanInstall: false), Core(status.Data));
        Assert.False(install.Success);
        Assert.Equal(
            "dbo.DDL_AuditLog exists but the DDL_Audit trigger cannot write to it: ObjectName varchar(50), needs at least 100 characters; "
            + "EventType varchar(20), needs at least 64 characters; CommandText nvarchar(4000), needs nvarchar(max) or varchar(max); "
            + "ProgramName is a computed column. Nothing was created.",
            install.Error);
        Assert.Equal(0, await ScratchDatabases.ScalarAsync<int>(cs, TriggerCountSql));
    }

    [SkippableFact]
    public async Task A_varchar_max_command_text_is_compatible_and_status_warns_that_it_is_lossy()
    {
        await using var scratch = await ScratchDatabases.CreateAsync(1);
        var cs = scratch.ConnectionStrings[0];
        await ScratchDatabases.ExecAsync(cs, """
            CREATE TABLE dbo.DDL_AuditLog (
                ID bigint IDENTITY(1,1) NOT NULL PRIMARY KEY, PostTime datetime2 NOT NULL DEFAULT (sysdatetime()),
                HostName nvarchar(128) NULL, LoginName nvarchar(128) NULL, SchemaName nvarchar(128) NULL, ObjectName nvarchar(128) NULL,
                ObjectType nvarchar(128) NULL, EventType nvarchar(64) NULL, CommandText varchar(max) NULL, CommandXML xml NULL,
                ProgramName nvarchar(128) NULL)
            """);

        var status = Assert.IsType<DdlAuditStatus>((await CallAsync(cs, readOnly: false, "status")).Data);

        Assert.True(status.TableCompatible);
        Assert.True(status.CanInstall);
        Assert.Equal(["CommandText is varchar(max); non-Latin text in DDL will be stored lossy."], status.Warnings);
        Assert.Equal(new DdlAuditInstallResult(CreatedTable: false, CreatedTrigger: true, TriggerEnabled: true), await InstallAsync(cs));
    }

    /// <summary>Why CommandXML must be xml: the trigger cannot even be created against a character CommandXML (257).</summary>
    [SkippableFact]
    public async Task A_character_command_xml_column_is_incompatible_because_sql_server_refuses_the_trigger_on_it()
    {
        await using var scratch = await ScratchDatabases.CreateAsync(1);
        var cs = scratch.ConnectionStrings[0];
        await ScratchDatabases.ExecAsync(cs, """
            CREATE TABLE dbo.DDL_AuditLog (
                ID int IDENTITY(1,1) NOT NULL PRIMARY KEY, PostTime datetime NOT NULL DEFAULT (getdate()),
                HostName varchar(100) NULL, LoginName varchar(100) NULL, SchemaName varchar(100) NULL, ObjectName varchar(100) NULL,
                ObjectType varchar(100) NULL, EventType varchar(64) NULL, CommandText nvarchar(max) NULL, CommandXML nvarchar(max) NULL,
                ProgramName varchar(100) NULL)
            """);

        var install = await CallAsync(cs, readOnly: false, "install");
        Assert.Equal(
            "dbo.DDL_AuditLog exists but the DDL_Audit trigger cannot write to it: CommandXML nvarchar(max), needs xml. Nothing was created.",
            install.Error);

        await using var conn = new Microsoft.Data.SqlClient.SqlConnection(cs);
        await conn.OpenAsync();
        var script = await Mssql.McpServer.InsightsLayer.InsightsLayerService.ReadEmbeddedResourceAsync(
            typeof(DdlAudit).Assembly, Mssql.McpServer.InsightsLayer.InsightsLayerService.TriggerScriptResource, CancellationToken.None);
        var ex = await Assert.ThrowsAsync<Microsoft.Data.SqlClient.SqlException>(
            () => Mssql.McpServer.InsightsLayer.InsightsLayerService.ExecuteBatchesAsync(conn, script, CancellationToken.None));
        Assert.Equal(257, ex.Number);
    }

    [SkippableFact]
    public async Task Status_reports_the_server_and_database_names_the_connection_actually_reached()
    {
        await using var scratch = await ScratchDatabases.CreateAsync(1);
        var cs = scratch.ConnectionStrings[0];

        var status = Assert.IsType<DdlAuditStatus>((await CallAsync(cs, readOnly: true, "status")).Data);

        Assert.Equal(scratch.Names[0], status.DatabaseName);
        Assert.Equal(await ScratchDatabases.ScalarAsync<string>(cs, "SELECT CAST(COALESCE(@@SERVERNAME, SERVERPROPERTY('ServerName')) AS nvarchar(256))"), status.ServerName);
        Assert.False(string.IsNullOrWhiteSpace(status.ServerName));
        Assert.Null(status.Warnings);
    }

    [SkippableFact]
    public async Task A_compatible_existing_table_with_extra_nullable_columns_gets_the_trigger()
    {
        await using var scratch = await ScratchDatabases.CreateAsync(1);
        var cs = scratch.ConnectionStrings[0];
        await ScratchDatabases.ExecAsync(cs, """
            CREATE TABLE dbo.DDL_AuditLog (
                ID int IDENTITY(1,1) NOT NULL PRIMARY KEY, PostTime datetime NOT NULL DEFAULT (getdate()),
                HostName varchar(100) NULL, LoginName varchar(100) NULL, SchemaName varchar(100) NULL, ObjectName varchar(100) NULL,
                ObjectType varchar(100) NULL, EventType varchar(64) NULL, CommandText nvarchar(max) NULL, CommandXML xml NULL,
                ProgramName varchar(100) NULL, Note nvarchar(50) NULL, Version rowversion)
            """);

        var result = await InstallAsync(cs);

        Assert.Equal(new DdlAuditInstallResult(CreatedTable: false, CreatedTrigger: true, TriggerEnabled: true), result);

        // The object-name index belongs to the create-only table script: an existing table is never given one.
        Assert.Equal(0, await ScratchDatabases.ScalarAsync<int>(cs, "SELECT COUNT(*) FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.DDL_AuditLog') AND name = N'IX_DDL_AuditLog_ObjectName'"));
    }

    [SkippableFact]
    public async Task A_trigger_failure_after_creating_the_table_rolls_back_the_table_and_the_writer_user()
    {
        await using var scratch = await ScratchDatabases.CreateAsync(1);
        var cs = scratch.ConnectionStrings[0];
        await using var conn = new Microsoft.Data.SqlClient.SqlConnection(cs);
        await conn.OpenAsync();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => DdlAudit.InstallAsync(conn, CancellationToken.None, triggerScript: "CREATE TRIGGER [DDL_Audit] ON DATABASE FOR no_such_event AS SET NOCOUNT ON"));

        Assert.StartsWith("Creating the DDL_Audit trigger failed: ", ex.Message, StringComparison.Ordinal);
        Assert.Contains("The dbo.DDL_AuditLog this call created was removed again; nothing was left behind.", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, await ScratchDatabases.ScalarAsync<int>(cs, TableExistsSql));
        Assert.Equal(0, await ScratchDatabases.ScalarAsync<int>(cs, TriggerCountSql));
        Assert.Equal(0, await ScratchDatabases.ScalarAsync<int>(cs, "SELECT COUNT(*) FROM sys.database_principals WHERE name = N'DDL_Audit_Writer'"));
    }

    /// <summary>NET-020: IDs are bigint end to end (an existing bigint table reseeded above int.MaxValue).</summary>
    [SkippableFact]
    public async Task Ids_above_int_max_value_are_listed_and_fetched()
    {
        await using var scratch = await ScratchDatabases.CreateAsync(1);
        var cs = scratch.ConnectionStrings[0];
        await ScratchDatabases.ExecAsync(cs, """
            CREATE TABLE dbo.DDL_AuditLog (
                ID bigint IDENTITY(1,1) NOT NULL PRIMARY KEY, PostTime datetime NOT NULL DEFAULT (getdate()),
                HostName varchar(100) NULL, LoginName varchar(100) NULL, SchemaName varchar(100) NULL, ObjectName varchar(100) NULL,
                ObjectType varchar(100) NULL, EventType varchar(64) NULL, CommandText nvarchar(max) NULL, CommandXML xml NULL,
                ProgramName varchar(100) NULL)
            """);
        await ScratchDatabases.ExecAsync(cs, "DBCC CHECKIDENT ('dbo.DDL_AuditLog', RESEED, 3000000000) WITH NO_INFOMSGS");
        await InstallAsync(cs);
        await ScratchDatabases.ExecAsync(cs, "CREATE PROCEDURE dbo.big AS SELECT 1");

        var list = await CallAsync(cs, readOnly: true, "list", schema: "dbo", name: "big");
        var entry = Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<DdlAuditEntry>>(list.Data));
        Assert.True(entry.Id > int.MaxValue, entry.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));

        var get = await CallAsync(cs, readOnly: true, "get", id: entry.Id);
        Assert.True(get.Success, get.Error);
        var command = Assert.IsType<DdlAuditCommand>(get.Data);
        Assert.Equal(entry.Id, command.Id);
        Assert.Equal("CREATE PROCEDURE dbo.big AS SELECT 1", command.CommandText);
    }
}
