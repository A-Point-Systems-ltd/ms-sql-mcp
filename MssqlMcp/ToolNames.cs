// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.

namespace Mssql.McpServer;

/// <summary>
/// Wire names of every MCP tool. Pinned explicitly so an SDK naming-policy change cannot silently
/// rename tools, and so every agent-facing message references the name the client actually sees.
/// </summary>
public static class ToolNames
{
    public const string ListObjects = "list_objects";
    public const string DescribeTable = "describe_table";
    public const string ScriptObject = "script_object";
    public const string DescribeView = "describe_view";
    public const string GetObject = "get_object";
    public const string ReadData = "read_data";
    public const string ExecuteSql = "execute_sql";
    public const string InsertData = "insert_data";
    public const string UpdateData = "update_data";
    public const string CreateTable = "create_table";
    public const string DropTable = "drop_table";
    public const string GetServerInfo = "get_server_info";
    public const string GetInsight = "get_insight";
    public const string UpsertInsight = "upsert_insight";
    public const string ListInsights = "list_insights";
    public const string GetInsightHistory = "get_insight_history";
    public const string RefreshInsights = "refresh_insights";
    public const string InstallInsightsLayer = "install_insights_layer";
    public const string InsightsCheck = "insights_check";
    public const string RebuildBaselineInsights = "rebuild_baseline_insights";
    // Connection-management tools (no 'connection' argument of their own).
    public const string ListConnections = "list_connections";
    public const string OpenConnection = "open_connection";
    public const string CloseConnection = "close_connection";
    // Extension-only: registered only when MSSQL_SCRIPT_RUNNER=true, never part of the agent tool set.
    public const string RunScript = "run_script";
    public const string DdlHistory = "ddl_history";
    public const string LanguageService = "language_service";

    /// <summary>All 23 tool names, in the order documented in README.md.</summary>
    public static readonly IReadOnlyList<string> All =
    [
        ListObjects, DescribeTable, DescribeView, GetObject, ScriptObject, ReadData, ExecuteSql, InsertData, UpdateData,
        CreateTable, DropTable, GetServerInfo, GetInsight, UpsertInsight, ListInsights, GetInsightHistory,
        RefreshInsights, InstallInsightsLayer, InsightsCheck, RebuildBaselineInsights,
        ListConnections, OpenConnection, CloseConnection,
    ];

    /// <summary>
    /// Tools for the VS Code extension only, not in <see cref="All"/>. They are routed like data tools but are listed
    /// only when their opt-in environment variable is set.
    /// </summary>
    public static readonly IReadOnlySet<string> ExtensionOnlyTools = new HashSet<string>(StringComparer.Ordinal)
    {
        RunScript, DdlHistory, LanguageService,
    };

    /// <summary>Tools refused on a read-only connection profile.</summary>
    public static readonly IReadOnlySet<string> WriteTools = new HashSet<string>(StringComparer.Ordinal)
    {
        ExecuteSql, InsertData, UpdateData, CreateTable, DropTable,
        UpsertInsight, InstallInsightsLayer, RefreshInsights, RebuildBaselineInsights,
    };

    /// <summary>Tools that manage connections themselves and therefore take no 'connection' argument.</summary>
    public static readonly IReadOnlySet<string> ConnectionManagementTools = new HashSet<string>(StringComparer.Ordinal)
    {
        ListConnections, OpenConnection, CloseConnection,
    };
}
