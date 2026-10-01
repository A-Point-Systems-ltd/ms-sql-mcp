// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.

using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace Mssql.McpServer;

/// <summary>
/// The statement shape a write tool accepts. <see cref="Any"/> is <c>execute_sql</c>'s DDL/DML allowlist.
/// </summary>
public enum SqlStatementKind
{
    Any,
    Insert,
    Update,
    CreateTable,
    DropTable,
}

/// <summary>
/// Classifies T-SQL for routing between <c>read_data</c> (read-only queries) and the write tools.
/// Uses the ScriptDom parser rather than regexes: T-SQL needs no ';' between statements and comment
/// markers may appear inside string literals, so only a real parse can prove "exactly one statement".
/// </summary>
internal static class SqlStatementClassifier
{
    public const string ReadDataRejectedMessage =
        $"{ToolNames.ReadData} accepts only a single read-only SELECT query (including WITH ... SELECT). Use {ToolNames.ExecuteSql} for DDL/DML and SELECT ... INTO.";

    public const string ExecuteSqlSelectRejectedMessage =
        $"{ToolNames.ExecuteSql} does not allow SELECT or other read-only queries. Use {ToolNames.ReadData} for all SELECT statements, including sys.*, INFORMATION_SCHEMA, and DMVs.";

    public const string MultipleStatementsMessage =
        "Only a single T-SQL statement is allowed (no batch separators or multiple statements).";

    public static bool IsReadOnlyQuery(string sql) =>
        TryValidateReadOnly(sql, out _);

    public static bool TryValidateReadOnly(string sql, out string? error) =>
        TryValidateReadOnly(sql, editorWording: false, out error);

    /// <summary>
    /// The same gate as <see cref="TryValidateReadOnly(string, out string?)"/>. With <paramref name="editorWording"/>,
    /// <paramref name="error"/> is a short reason for a person in a query window and names no agent tool.
    /// </summary>
    public static bool TryValidateReadOnly(string sql, bool editorWording, out string? error)
    {
        if (!TryParseSingleStatement(sql, out var statement, out error))
        {
            return false;
        }

        if (statement is not SelectStatement select)
        {
            error = editorWording ? "The batch is not a SELECT query." : ReadDataRejectedMessage;
            return false;
        }

        if (select.Into is not null)
        {
            error = editorWording
                ? "SELECT ... INTO creates a table."
                : $"SELECT ... INTO is not allowed in {ToolNames.ReadData}. Use {ToolNames.ExecuteSql}.";
            return false;
        }

        var visitor = new ExternalDataAccessVisitor();
        select.Accept(visitor);
        if (visitor.Offender is not null)
        {
            error = editorWording
                ? $"{visitor.Offender} can reach outside this database."
                : $"{visitor.Offender} is not allowed in {ToolNames.ReadData} (it can reach outside this database).";
            return false;
        }

        return true;
    }

    public static bool TryValidateExecutable(string sql, out string? error) =>
        TryValidateWrite(sql, SqlStatementKind.Any, out error);

    /// <summary>
    /// Validates that <paramref name="sql"/> is exactly one statement of the shape the calling tool promises,
    /// so e.g. <c>insert_data("DROP TABLE x")</c> is rejected instead of executed.
    /// </summary>
    public static bool TryValidateWrite(string sql, SqlStatementKind kind, out string? error)
    {
        if (!TryParseSingleStatement(sql, out var statement, out error))
        {
            return false;
        }

        if (statement is SelectStatement { Into: null })
        {
            error = ExecuteSqlSelectRejectedMessage;
            return false;
        }

        var ok = kind switch
        {
            // INSERT ... EXEC runs arbitrary SQL, so it is only allowed through execute_sql.
            SqlStatementKind.Insert => statement is InsertStatement { InsertSpecification.InsertSource: not ExecuteInsertSource },
            SqlStatementKind.Update => statement is UpdateStatement,
            SqlStatementKind.CreateTable => statement is CreateTableStatement,
            SqlStatementKind.DropTable => statement is DropTableStatement,
            _ => IsAllowedExecutable(statement),
        };

        if (ok && kind is SqlStatementKind.Insert or SqlStatementKind.Update)
        {
            var visitor = new ExternalDataAccessVisitor();
            statement.Accept(visitor);
            if (visitor.Offender is not null)
            {
                error = $"{visitor.Offender} is not allowed in typed write tools. Use {ToolNames.ExecuteSql}.";
                return false;
            }
        }

        if (!ok && statement is InsertStatement { InsertSpecification.InsertSource: ExecuteInsertSource })
        {
            error = $"INSERT ... EXEC runs arbitrary SQL and is not allowed in {ToolNames.InsertData}. Use {ToolNames.ExecuteSql}.";
            return false;
        }

        if (!ok)
        {
            error = kind == SqlStatementKind.Any
                ? $"Unsupported statement type '{DescribeStatement(statement)}' for {ToolNames.ExecuteSql}."
                : $"Expected a single {ExpectedKeyword(kind)} statement but got '{DescribeStatement(statement)}'. Use {ToolNames.ExecuteSql} for other statement types.";
        }

        return ok;
    }

    private static bool IsAllowedExecutable(TSqlStatement statement)
    {
        switch (statement)
        {
            case InsertStatement or UpdateStatement or DeleteStatement or MergeStatement
                or TruncateTableStatement or ExecuteStatement or SelectStatement { Into: not null }
                or GrantStatement or RevokeStatement or DenyStatement:
                return true;

            // Guarded DDL such as "IF OBJECT_ID(N'dbo.t') IS NOT NULL DROP TABLE dbo.t": every branch must itself be allowed.
            case IfStatement ifStatement:
                return IsAllowedExecutable(ifStatement.ThenStatement)
                    && (ifStatement.ElseStatement is null || IsAllowedExecutable(ifStatement.ElseStatement));

            case BeginEndBlockStatement block:
                return block.StatementList.Statements.Count > 0
                    && block.StatementList.Statements.All(IsAllowedExecutable);
        }

        var name = statement.GetType().Name;
        return name.StartsWith("Create", StringComparison.Ordinal)
            || name.StartsWith("Alter", StringComparison.Ordinal)
            || name.StartsWith("Drop", StringComparison.Ordinal)
            || name.StartsWith("Backup", StringComparison.Ordinal)
            || name.StartsWith("Restore", StringComparison.Ordinal);
    }

    private static bool TryParseSingleStatement(string sql, out TSqlStatement statement, out string? error)
    {
        statement = null!;
        error = null;
        if (string.IsNullOrWhiteSpace(sql))
        {
            error = "SQL is required.";
            return false;
        }

        var parser = new TSql170Parser(initialQuotedIdentifiers: true);
        using var reader = new StringReader(sql);
        var fragment = parser.Parse(reader, out var errors);
        if (errors.Count > 0)
        {
            var first = errors[0];
            error = $"T-SQL syntax error at line {first.Line}, column {first.Column}: {first.Message}";
            return false;
        }

        if (fragment is not TSqlScript script)
        {
            error = "Could not determine the SQL statement type.";
            return false;
        }

        var statements = script.Batches.SelectMany(static b => b.Statements).ToList();
        if (statements.Count == 0)
        {
            error = "SQL is required.";
            return false;
        }

        if (script.Batches.Count > 1 || statements.Count > 1)
        {
            error = MultipleStatementsMessage;
            return false;
        }

        statement = statements[0];
        return true;
    }

    private static string DescribeStatement(TSqlStatement statement)
    {
        var name = statement.GetType().Name;
        return name.EndsWith("Statement", StringComparison.Ordinal) ? name[..^"Statement".Length] : name;
    }

    private static string ExpectedKeyword(SqlStatementKind kind) => kind switch
    {
        SqlStatementKind.Insert => "INSERT",
        SqlStatementKind.Update => "UPDATE",
        SqlStatementKind.CreateTable => "CREATE TABLE",
        SqlStatementKind.DropTable => "DROP TABLE",
        _ => "DDL/DML",
    };

    /// <summary>Flags rowset functions that read from linked servers, files or remote sources.</summary>
    private sealed class ExternalDataAccessVisitor : TSqlFragmentVisitor
    {
        public string? Offender { get; private set; }

        public override void Visit(OpenQueryTableReference node) => Offender ??= "OPENQUERY";

        public override void Visit(OpenRowsetTableReference node) => Offender ??= "OPENROWSET";

        public override void Visit(InternalOpenRowset node) => Offender ??= "OPENROWSET";

        public override void Visit(BulkOpenRowset node) => Offender ??= "OPENROWSET(BULK ...)";

        public override void Visit(AdHocTableReference node) => Offender ??= "OPENDATASOURCE";

        public override void Visit(SchemaObjectName node)
        {
            if (node.ServerIdentifier is not null)
            {
                Offender ??= $"Linked-server reference '{node.ServerIdentifier.Value}'";
            }
        }
    }
}
