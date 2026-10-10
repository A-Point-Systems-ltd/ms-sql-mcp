using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace Mssql.McpServer.LanguageService.Formatting;

/// <summary>Where a line starts: the output column of <see cref="Anchor"/> (-1: the base indent) plus units and columns.</summary>
internal readonly record struct IndentSpec(int Anchor, int Units, int Columns = 0);

internal enum LayoutKind
{
    /// <summary>Keeps an original line break (re-indented relative to the previous line); otherwise one space or none.</summary>
    Default,

    /// <summary>Always starts a new line at the indent.</summary>
    Break,

    /// <summary>Stays on the previous token's line (normal spacing).</summary>
    Join,

    /// <summary>Stays on the previous token's line with no space (the variable after a leading comma).</summary>
    JoinNoSpace,
}

/// <summary>The layout decision for one token; <see cref="Soft"/> is the indent if a comment forces a line break before it.</summary>
internal readonly record struct TokenLayout(LayoutKind Kind, IndentSpec Indent, IndentSpec? Soft);

/// <summary>
/// Decides, from the ScriptDom syntax tree, which tokens start lines and at what indent, and which tokens are
/// identifiers (never re-cased) or system type / built-in function names (re-cased like keywords). It never decides
/// anything that changes tokens: the emitter only rewrites whitespace and letter case.
/// </summary>
internal sealed class SqlLayout
{
    private readonly SqlTokenDoc _doc;
    private readonly SqlFormatOptions _options;
    private readonly bool[] _locked;
    private readonly Dictionary<QuerySpecification, IReadOnlyList<int>> _mirrorRows = new(ReferenceEqualityComparer.Instance);

    public SqlLayout(SqlTokenDoc doc, SqlFormatOptions options)
    {
        _doc = doc;
        _options = options;
        Layouts = new TokenLayout[doc.Count];
        _locked = new bool[doc.Count];
        IdentifierToken = new bool[doc.Count];
        ForceRecase = new bool[doc.Count];
    }

    public TokenLayout[] Layouts { get; }

    /// <summary>Tokens inside an Identifier (object, column, alias, variable-like names): their case is never changed.</summary>
    public bool[] IdentifierToken { get; }

    /// <summary>Identifier tokens that are still re-cased: system data type names and built-in function names.</summary>
    public bool[] ForceRecase { get; }

    public void Build(TSqlScript script)
    {
        // Pass 1: statements, blocks and statement-level clauses (and the inline locks of short statements).
        foreach (var batch in script.Batches)
        {
            LayoutStatements(batch.Statements, new IndentSpec(-1, 0));
        }

        for (var i = 0; i < _doc.Count; i++)
        {
            if (_doc.Tokens[i].Type == TSqlTokenType.Go)
            {
                Break(i, new IndentSpec(-1, 0));
            }
        }

        // Pass 2: queries, joins, predicates and lists wherever they appear (subqueries included); names and types.
        script.Accept(new QueryVisitor(this));
    }

    // ----- primitives -----

    private void Set(int i, TokenLayout layout)
    {
        if (i >= 0 && i < _doc.Count && !_locked[i])
        {
            Layouts[i] = layout;
        }
    }

    private void Break(int i, IndentSpec indent) => Set(i, new TokenLayout(LayoutKind.Break, indent, null));

    private void Join(int i, IndentSpec? soft = null) => Set(i, new TokenLayout(LayoutKind.Join, default, soft));

    private void JoinNoSpace(int i) => Set(i, new TokenLayout(LayoutKind.JoinNoSpace, default, null));

    /// <summary>Locks tokens (after the first) of a fragment: later decisions inside it are ignored and it keeps its single line.</summary>
    private void LockInner(TSqlFragment fragment)
    {
        var first = _doc.First(fragment);
        var last = _doc.Last(fragment);
        for (var i = first + 1; i <= last && i < _doc.Count; i++)
        {
            _locked[i] = true;
        }
    }

    private int First(TSqlFragment f) => _doc.First(f);

    private int Last(TSqlFragment f) => _doc.Last(f);

    private int After(TSqlFragment f) => _doc.NextCode(Last(f));

    private int Before(TSqlFragment f) => _doc.PrevCode(First(f));

    private bool SingleLine(TSqlFragment f) => !_doc.SpansLines(First(f), Last(f));

    /// <summary>The first code token of type <paramref name="type"/> in [from, to], or -1.</summary>
    private int Find(int from, int to, TSqlTokenType type)
    {
        for (var i = Math.Max(0, from); i <= to && i < _doc.Count; i++)
        {
            if (_doc.Tokens[i].Type == type)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>The first token in [from, to] whose text is <paramref name="word"/> (case-insensitive), or -1.</summary>
    private int FindWord(int from, int to, string word)
    {
        for (var i = Math.Max(0, from); i <= to && i < _doc.Count; i++)
        {
            if (_doc.Tokens[i].Is(word))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>The last code token of type <paramref name="type"/> in [from, to], or -1.</summary>
    private int FindLast(int from, int to, TSqlTokenType type)
    {
        for (var i = Math.Min(to, _doc.Count - 1); i >= Math.Max(0, from); i--)
        {
            if (_doc.Tokens[i].Type == type)
            {
                return i;
            }
        }

        return -1;
    }

    private bool IsToken(int i, TSqlTokenType type) => i >= 0 && i < _doc.Count && _doc.Tokens[i].Type == type;

    // ----- pass 1: statements -----

    private void LayoutStatements(IEnumerable<TSqlStatement> statements, IndentSpec indent)
    {
        foreach (var statement in statements)
        {
            Break(First(statement), indent);
            LayoutStatement(statement);
        }
    }

    /// <summary>A module body: a lone BEGIN...END sits at the header's level, other statements one level in.</summary>
    private void LayoutBody(IList<TSqlStatement> statements, int anchor)
    {
        if (statements.Count == 1 && statements[0] is BeginEndBlockStatement)
        {
            LayoutStatements(statements, new IndentSpec(anchor, 0));
        }
        else
        {
            LayoutStatements(statements, new IndentSpec(anchor, 1));
        }
    }

    private void LayoutStatement(TSqlStatement statement)
    {
        switch (statement)
        {
            case BeginEndBlockStatement block:
                LayoutBlock(block);
                break;
            case TryCatchStatement tryCatch:
                LayoutTryCatch(tryCatch);
                break;
            case IfStatement ifStatement:
                LayoutIf(ifStatement, First(ifStatement));
                break;
            case WhileStatement whileStatement:
                LayoutWhile(whileStatement);
                break;
            case ProcedureStatementBody procedure:
                LayoutProcedure(procedure);
                break;
            case FunctionStatementBody function:
                LayoutFunction(function);
                break;
            case ViewStatementBody view:
                LayoutView(view);
                break;
            case TriggerStatementBody trigger:
                LayoutTrigger(trigger);
                break;
            case DeclareVariableStatement declare:
                LayoutDeclare(declare);
                break;
            case DeclareCursorStatement cursor:
                LayoutCursor(cursor);
                break;
            case DeclareTableVariableStatement tableVariable:
                LayoutTableDefinition(tableVariable.Body?.Definition, First(tableVariable));
                break;
            case CreateTableStatement createTable:
                LayoutTableDefinition(createTable.Definition, First(createTable));
                break;
            case SelectStatement select:
                LayoutSelectStatement(select);
                break;
            case InsertStatement insert:
                LayoutInsert(insert);
                break;
            case UpdateStatement update:
                LayoutUpdate(update);
                break;
            case DeleteStatement delete:
                LayoutDelete(delete);
                break;
            case MergeStatement merge:
                LayoutMerge(merge);
                break;
        }
    }

    private void LayoutBlock(BeginEndBlockStatement block)
    {
        var begin = First(block);
        LayoutStatements(block.StatementList?.Statements ?? [], new IndentSpec(begin, 1));
        var end = FindLast(begin + 1, Last(block), TSqlTokenType.End);
        if (end > begin)
        {
            Break(end, new IndentSpec(begin, 0));
        }
    }

    private void LayoutTryCatch(TryCatchStatement statement)
    {
        var begin = First(statement);
        var at = new IndentSpec(begin, 0);
        var tryStatements = statement.TryStatements?.Statements ?? [];
        LayoutStatements(tryStatements, new IndentSpec(begin, 1));
        var searchFrom = tryStatements.Count > 0 ? Last(tryStatements[^1]) + 1 : begin + 1;
        var endTry = Find(searchFrom, Last(statement), TSqlTokenType.End);
        if (endTry < 0)
        {
            return;
        }

        Break(endTry, at);
        var beginCatch = Find(endTry + 1, Last(statement), TSqlTokenType.Begin);
        if (beginCatch > 0)
        {
            Break(beginCatch, at);
        }

        LayoutStatements(statement.CatchStatements?.Statements ?? [], new IndentSpec(begin, 1));
        var endCatch = FindLast(beginCatch + 1, Last(statement), TSqlTokenType.End);
        if (endCatch > beginCatch)
        {
            Break(endCatch, at);
        }
    }

    /// <summary>IF: the predicate stays on the IF line; a BEGIN block at the IF's level, a single statement one level in.</summary>
    private void LayoutIf(IfStatement statement, int anchor)
    {
        Join(First(statement.Predicate));
        LayoutBranch(statement.ThenStatement, anchor);
        if (statement.ElseStatement is null)
        {
            return;
        }

        var elseToken = Before(statement.ElseStatement);
        Break(elseToken, new IndentSpec(anchor, 0));
        if (statement.ElseStatement is IfStatement elseIf)
        {
            // ELSE IF on one line; its branches line up with the first IF.
            Join(First(elseIf));
            LayoutIf(elseIf, anchor);
        }
        else
        {
            LayoutBranch(statement.ElseStatement, anchor);
        }
    }

    private void LayoutWhile(WhileStatement statement)
    {
        Join(First(statement.Predicate));
        LayoutBranch(statement.Statement, First(statement));
    }

    private void LayoutBranch(TSqlStatement? branch, int anchor)
    {
        if (branch is null)
        {
            return;
        }

        Break(First(branch), new IndentSpec(anchor, branch is BeginEndBlockStatement or TryCatchStatement ? 0 : 1));
        LayoutStatement(branch);
    }

    /// <summary>Parameters one per line one level in; a closing parenthesis on its own line; WITH and AS at the header's level.</summary>
    private int LayoutParameters(IList<ProcedureParameter> parameters, int anchor)
    {
        if (parameters.Count == 0)
        {
            return -1;
        }

        var open = Before(parameters[0]);
        if (IsToken(open, TSqlTokenType.LeftParenthesis))
        {
            Join(open);
        }

        foreach (var parameter in parameters)
        {
            Break(First(parameter), new IndentSpec(anchor, 1));
        }

        var close = After(parameters[^1]);
        if (IsToken(close, TSqlTokenType.RightParenthesis) && IsToken(open, TSqlTokenType.LeftParenthesis))
        {
            Break(close, new IndentSpec(anchor, 1));
            return close;
        }

        return Last(parameters[^1]);
    }

    private void LayoutModuleTail(int anchor, int headerEnd, int bodyStart, IList<TSqlStatement>? statements)
    {
        var at = new IndentSpec(anchor, 0);
        var with = Find(headerEnd + 1, bodyStart - 1, TSqlTokenType.With);
        if (with > 0)
        {
            Break(with, at);
        }

        var asToken = FindLast(headerEnd + 1, bodyStart - 1, TSqlTokenType.As);
        if (asToken > 0)
        {
            Break(asToken, at);
        }

        if (statements is not null)
        {
            LayoutBody(statements, anchor);
        }
    }

    private void LayoutProcedure(ProcedureStatementBody procedure)
    {
        var anchor = First(procedure);
        var headerEnd = LayoutParameters(procedure.Parameters, anchor);
        if (headerEnd < 0 && procedure.ProcedureReference is { } reference)
        {
            headerEnd = Last(reference);
        }

        var statements = procedure.StatementList?.Statements;
        var bodyStart = statements is { Count: > 0 } ? First(statements[0]) : Last(procedure) + 1;
        LayoutModuleTail(anchor, headerEnd, bodyStart, statements);
    }

    private void LayoutFunction(FunctionStatementBody function)
    {
        var anchor = First(function);
        var at = new IndentSpec(anchor, 0);
        var headerEnd = LayoutParameters(function.Parameters, anchor);
        if (headerEnd < 0)
        {
            headerEnd = Last(function.Name);
        }

        var returns = FindWord(headerEnd + 1, Last(function), "returns");
        if (returns > 0)
        {
            Break(returns, at);
            headerEnd = returns;
        }

        if (function.ReturnType is TableValuedFunctionReturnType tvf)
        {
            LayoutTableDefinition(tvf.DeclareTableVariableBody?.Definition, anchor);
            headerEnd = Math.Max(headerEnd, Last(tvf));
        }
        else if (function.ReturnType is ScalarFunctionReturnType scalar)
        {
            headerEnd = Math.Max(headerEnd, Last(scalar));
        }

        var statements = function.StatementList?.Statements;
        if (function.ReturnType is SelectFunctionReturnType inline && inline.SelectStatement is { } select)
        {
            var returnToken = FindLast(headerEnd + 1, First(select) - 1, TSqlTokenType.Return);
            if (returnToken > 0)
            {
                LayoutModuleTail(anchor, headerEnd, returnToken, null);
                Break(returnToken, at);
                LayoutInlineBody(select, anchor);
            }

            return;
        }

        var bodyStart = statements is { Count: > 0 } ? First(statements[0]) : Last(function) + 1;
        LayoutModuleTail(anchor, headerEnd, bodyStart, statements);
    }

    /// <summary>RETURN ( query ): a multi-line query one level in, its closing parenthesis back at the header's level.</summary>
    private void LayoutInlineBody(SelectStatement select, int anchor)
    {
        if (!SingleLine(select))
        {
            if (select.QueryExpression is QueryParenthesisExpression { QueryExpression: { } inner } parenthesized)
            {
                // RETURN ( on the RETURN line, the query one level in, ) back at the header's level.
                Join(First(parenthesized));
                Break(First(inner), new IndentSpec(anchor, 1));
                Break(Last(parenthesized), new IndentSpec(anchor, 0));
            }
            else
            {
                Break(First(select), new IndentSpec(anchor, 1));
            }
        }

        LayoutSelectStatement(select);
    }

    private void LayoutView(ViewStatementBody view)
    {
        var anchor = First(view);
        if (view.SelectStatement is not { } select)
        {
            return;
        }

        var headerEnd = view.Columns is { Count: > 0 } ? After(view.Columns[^1]) : Last(view.SchemaObjectName);
        LayoutModuleTail(anchor, headerEnd, First(select), null);
        Break(First(select), new IndentSpec(anchor, 0));
        LayoutSelectStatement(select);
    }

    private void LayoutTrigger(TriggerStatementBody trigger)
    {
        var anchor = First(trigger);
        var at = new IndentSpec(anchor, 0);
        var headerEnd = trigger.TriggerObject is { } target ? Last(target) : anchor;
        if (trigger.TriggerActions is { Count: > 0 } actions)
        {
            // FOR / AFTER / INSTEAD OF on its own line.
            var kind = _doc.PrevCode(First(actions[0]));
            if (_doc.Tokens[kind].Is("of"))
            {
                kind = _doc.PrevCode(kind);
            }

            if (kind > headerEnd)
            {
                Break(kind, at);
            }

            headerEnd = Last(actions[^1]);
        }

        var statements = trigger.StatementList?.Statements;
        var bodyStart = statements is { Count: > 0 } ? First(statements[0]) : Last(trigger) + 1;
        LayoutModuleTail(anchor, headerEnd, bodyStart, statements);
    }

    /// <summary>
    /// DECLARE: variables of one type without a value share a line; a variable with a value gets its own line. Later
    /// lines start with a leading comma under the first variable (DECLARE plus one space).
    /// </summary>
    private void LayoutDeclare(DeclareVariableStatement declare)
    {
        var declarations = declare.Declarations;
        if (declarations.Count < 2)
        {
            return;
        }

        var anchor = First(declare);
        var row = new IndentSpec(anchor, 0, _doc.Tokens[anchor].Text.Length + 1);
        var first = First(declarations[0]);
        if (_doc.NewlinesBefore(first) > 0)
        {
            Break(first, row);
        }
        else
        {
            Join(first);
        }

        for (var j = 1; j < declarations.Count; j++)
        {
            var start = First(declarations[j]);
            var comma = _doc.PrevCode(start);
            var newRow = declarations[j].Value is not null || declarations[j - 1].Value is not null
                || TypeKey(declarations[j]) != TypeKey(declarations[j - 1]);
            if (newRow && IsToken(comma, TSqlTokenType.Comma))
            {
                Break(comma, row);
                JoinNoSpace(start);
            }
            else
            {
                Join(comma);
                Join(start);
            }
        }
    }

    private string TypeKey(DeclareVariableElement element)
    {
        if (element.DataType is null)
        {
            return string.Empty;
        }

        var parts = new List<string>();
        for (var i = First(element.DataType); i <= Last(element.DataType); i++)
        {
            parts.Add(_doc.Tokens[i].Text.ToUpperInvariant());
        }

        return string.Concat(parts);
    }

    private void LayoutCursor(DeclareCursorStatement cursor)
    {
        if (cursor.CursorDefinition?.Select is { } select)
        {
            Break(First(select), new IndentSpec(First(cursor), 1));
            LayoutSelectStatement(select);
        }
    }

    /// <summary>Table definitions (CREATE TABLE, table variables, multi-statement TVFs): one element per line.</summary>
    private void LayoutTableDefinition(TableDefinition? definition, int anchor)
    {
        if (definition is null)
        {
            return;
        }

        var elements = new List<TSqlFragment>();
        elements.AddRange(definition.ColumnDefinitions);
        elements.AddRange(definition.TableConstraints);
        elements.AddRange(definition.Indexes);
        elements.Sort((a, b) => a.FirstTokenIndex.CompareTo(b.FirstTokenIndex));
        if (elements.Count == 0 || (elements.Count == 1 && SingleLine(definition)))
        {
            return;
        }

        foreach (var element in elements)
        {
            Break(First(element), new IndentSpec(anchor, 1));
        }

        var close = After(elements[^1]);
        if (IsToken(close, TSqlTokenType.RightParenthesis))
        {
            Break(close, new IndentSpec(anchor, 0));
        }
    }

    private void LayoutSelectStatement(SelectStatement select)
    {
        var anchor = First(select);
        if (select.WithCtesAndXmlNamespaces is { } with)
        {
            var ctes = with.CommonTableExpressions;
            for (var j = 0; j < ctes.Count; j++)
            {
                var cte = ctes[j];
                if (j > 0)
                {
                    Break(First(cte), new IndentSpec(anchor, 0));
                }

                if (cte.QueryExpression is { } query)
                {
                    var open = _doc.PrevCode(First(query));
                    if (IsToken(open, TSqlTokenType.LeftParenthesis))
                    {
                        Join(open);
                    }

                    Break(First(query), new IndentSpec(anchor, 1));
                    var close = _doc.NextCode(Last(query));
                    if (IsToken(close, TSqlTokenType.RightParenthesis))
                    {
                        Break(close, new IndentSpec(anchor, 0));
                    }
                }
            }

            if (select.QueryExpression is { } main)
            {
                Break(First(main), new IndentSpec(anchor, 0));
            }
        }

        if (select.Into is { } into)
        {
            var intoToken = Before(into);
            var queryAnchor = select.QueryExpression is { } q ? First(q) : anchor;
            Break(intoToken, new IndentSpec(queryAnchor, 0));
        }

        if (select.OptimizerHints is { Count: > 0 } hints)
        {
            var option = FindLast(anchor, First(hints[0]), TSqlTokenType.Option);
            if (option > 0)
            {
                Break(option, new IndentSpec(anchor, 0));
            }
        }
    }

    /// <summary>Short UPDATE / DELETE / INSERT written on one line stay on one line.</summary>
    private bool KeepInline(TSqlStatement statement, bool hasFrom)
    {
        if (hasFrom || !SingleLine(statement))
        {
            return false;
        }

        LockInner(statement);
        return true;
    }

    private void LayoutInsert(InsertStatement insert)
    {
        var spec = insert.InsertSpecification;
        if (spec is null)
        {
            return;
        }

        var anchor = First(insert);
        if (spec.Columns.Count <= _options.MaxItemsPerRow && KeepInline(insert, false))
        {
            return;
        }

        var at = new IndentSpec(anchor, 0);
        if (spec.Target is { } target)
        {
            Join(First(target));
        }

        IReadOnlyList<int>? columnRows = null;
        if (spec.Columns.Count > 0)
        {
            var open = Before(spec.Columns[0]);
            if (IsToken(open, TSqlTokenType.LeftParenthesis))
            {
                Join(open);
            }

            columnRows = LayoutList([.. spec.Columns], new IndentSpec(anchor, 1), null, allowInline: true);
            var close = After(spec.Columns[^1]);
            if (IsToken(close, TSqlTokenType.RightParenthesis))
            {
                Join(close);
            }
        }

        var multiRow = columnRows is { Count: > 1 };
        if (spec.OutputClause is { } output)
        {
            Break(First(output), at);
        }

        if (spec.OutputIntoClause is { } outputInto)
        {
            Break(First(outputInto), at);
        }

        switch (spec.InsertSource)
        {
            case SelectInsertSource selectSource when selectSource.Select is { } query:
                Break(First(query), at);
                if (multiRow && query is QuerySpecification qs && qs.SelectElements.Count == spec.Columns.Count)
                {
                    _mirrorRows[qs] = columnRows!;
                }

                break;
            case ValuesInsertSource values:
                Break(First(values), at);
                if (values.RowValues.Count == 1 && values.RowValues[0] is { } row)
                {
                    if (row.ColumnValues.Count > 0)
                    {
                        Join(First(row));
                        var mirror = multiRow && row.ColumnValues.Count == spec.Columns.Count ? columnRows : null;
                        LayoutList([.. row.ColumnValues], new IndentSpec(anchor, 1), mirror, allowInline: true);
                        var close = After(row.ColumnValues[^1]);
                        if (IsToken(close, TSqlTokenType.RightParenthesis))
                        {
                            Join(close);
                        }
                    }
                }

                break;
            case ExecuteInsertSource exec:
                Break(First(exec), at);
                break;
        }
    }

    private void LayoutUpdate(UpdateStatement update)
    {
        var spec = update.UpdateSpecification;
        if (spec is null || KeepInline(update, spec.FromClause is not null))
        {
            return;
        }

        var anchor = First(update);
        var at = new IndentSpec(anchor, 0);
        if (spec.Target is { } target)
        {
            Join(First(target));
        }

        if (spec.SetClauses.Count > 0)
        {
            Break(Before(spec.SetClauses[0]), at);
            LayoutList([.. spec.SetClauses], new IndentSpec(anchor, 1), null, allowInline: true);
        }

        if (spec.OutputClause is { } output)
        {
            Break(First(output), at);
        }

        if (spec.OutputIntoClause is { } outputInto)
        {
            Break(First(outputInto), at);
        }

        LayoutFromWhere(spec.FromClause, spec.WhereClause, anchor);
    }

    private void LayoutDelete(DeleteStatement delete)
    {
        var spec = delete.DeleteSpecification;
        if (spec is null || KeepInline(delete, spec.FromClause is not null))
        {
            return;
        }

        var anchor = First(delete);
        if (spec.Target is { } target)
        {
            Join(First(target));
        }

        if (spec.OutputClause is { } output)
        {
            Break(First(output), new IndentSpec(anchor, 0));
        }

        if (spec.OutputIntoClause is { } outputInto)
        {
            Break(First(outputInto), new IndentSpec(anchor, 0));
        }

        LayoutFromWhere(spec.FromClause, spec.WhereClause, anchor);
    }

    private void LayoutMerge(MergeStatement merge)
    {
        var spec = merge.MergeSpecification;
        if (spec is null)
        {
            return;
        }

        var anchor = First(merge);
        var at = new IndentSpec(anchor, 0);
        if (spec.TableReference is { } source)
        {
            Break(Before(source), at);
            Join(First(source));
        }

        if (spec.SearchCondition is { } on)
        {
            Join(Before(on));
            Join(First(on));
        }

        foreach (var clause in spec.ActionClauses)
        {
            var when = FindLast(0, First(clause), TSqlTokenType.When);
            Break(when >= 0 ? when : First(clause), at);
            for (var i = when + 1; when >= 0 && i < First(clause); i++)
            {
                Join(i);
            }

            Join(First(clause));
            if (clause.Action is { } action)
            {
                Break(First(action), new IndentSpec(anchor, 1));
            }
        }

        if (spec.OutputClause is { } output)
        {
            Break(First(output), at);
        }

        if (spec.OutputIntoClause is { } outputInto)
        {
            Break(First(outputInto), at);
        }
    }

    // ----- shared clause layouts -----

    /// <summary>FROM (joins one level in, ON on the join line) and WHERE, at the anchor's level.</summary>
    private void LayoutFromWhere(FromClause? from, WhereClause? where, int anchor)
    {
        var at = new IndentSpec(anchor, 0);
        if (from is not null)
        {
            var fromToken = First(from);
            Break(fromToken, at);
            for (var j = 0; j < from.TableReferences.Count; j++)
            {
                if (j == 0)
                {
                    Join(First(from.TableReferences[j]));
                }

                LayoutTableReference(from.TableReferences[j], fromToken);
            }
        }

        if (where?.SearchCondition is { } condition)
        {
            Break(First(where), at);
            LayoutPredicate(condition, anchor);
        }
    }

    private void LayoutTableReference(TableReference reference, int fromToken)
    {
        switch (reference)
        {
            case QualifiedJoin join:
                LayoutTableReference(join.FirstTableReference, fromToken);
                LayoutJoin(join.FirstTableReference, join.SecondTableReference, fromToken);
                var onToken = _doc.NextCode(Last(join.SecondTableReference));
                if (_doc.Tokens.ElementAtOrDefault(onToken)?.Type == TSqlTokenType.On)
                {
                    Join(onToken);
                    Join(First(join.SearchCondition));
                }

                break;
            case UnqualifiedJoin join:
                LayoutTableReference(join.FirstTableReference, fromToken);
                LayoutJoin(join.FirstTableReference, join.SecondTableReference, fromToken);
                break;
        }
    }

    private void LayoutJoin(TableReference left, TableReference right, int fromToken)
    {
        var joinToken = After(left);
        if (joinToken < 0 || joinToken >= First(right))
        {
            return;
        }

        Break(joinToken, new IndentSpec(fromToken, 1));
        for (var i = joinToken + 1; i < First(right); i++)
        {
            Join(i);
        }

        Join(First(right));
    }

    /// <summary>
    /// A search condition: one predicate stays on the keyword's line; two or more go one per line, one level in, each
    /// AND / OR leading its line. A parenthesized group of two or more spread over lines opens on its operator's line,
    /// its predicates one more level in, its closing parenthesis back on its own line.
    /// </summary>
    private void LayoutPredicate(BooleanExpression condition, int anchor)
    {
        var chain = Flatten(condition);
        if (chain.Count == 1)
        {
            Join(First(condition));
            return;
        }

        LayoutChain(chain, anchor);
    }

    private void LayoutChain(List<(int Operator, BooleanExpression Operand)> chain, int anchor)
    {
        var line = new IndentSpec(anchor, 1);
        for (var k = 0; k < chain.Count; k++)
        {
            var (op, operand) = chain[k];
            var start = First(operand);
            if (op < 0)
            {
                Break(start, line);
            }
            else
            {
                Break(op, line);
                Join(start, line);
            }

            if (operand is BooleanParenthesisExpression group && group.Expression is { } inner && !SingleLine(group))
            {
                var innerChain = Flatten(inner);
                if (innerChain.Count < 2)
                {
                    continue;
                }

                var groupAnchor = op < 0 ? start : op;
                LayoutChain(innerChain, groupAnchor);
                var close = Last(group);
                if (IsToken(close, TSqlTokenType.RightParenthesis))
                {
                    Break(close, new IndentSpec(groupAnchor, 0));
                }
            }
        }
    }

    /// <summary>AND / OR operands left to right (not through parentheses), each with the token of the operator before it (-1 for the first).</summary>
    private List<(int Operator, BooleanExpression Operand)> Flatten(BooleanExpression expression)
    {
        var result = new List<(int, BooleanExpression)>();
        Walk(expression, -1);
        return result;

        void Walk(BooleanExpression e, int op)
        {
            if (e is BooleanBinaryExpression binary && binary.FirstExpression is not null && binary.SecondExpression is not null)
            {
                Walk(binary.FirstExpression, op);
                Walk(binary.SecondExpression, After(binary.FirstExpression));
            }
            else
            {
                result.Add((op, e));
            }
        }
    }

    /// <summary>
    /// A list of up to <see cref="SqlFormatOptions.MaxItemsPerRow"/> items written on one line stays on the keyword's
    /// line. Otherwise each row starts on its own line at <paramref name="row"/>: rows the author already broke are
    /// kept when they hold at most the maximum, longer rows are split, and <paramref name="mirror"/> (row sizes of a
    /// matching INSERT column list) wins when given. Returns the row sizes.
    /// </summary>
    private IReadOnlyList<int> LayoutList(IReadOnlyList<TSqlFragment> items, IndentSpec row, IReadOnlyList<int>? mirror, bool allowInline)
    {
        var max = _options.MaxItemsPerRow;
        List<int> rows;
        if (mirror is not null && mirror.Sum() == items.Count)
        {
            rows = [.. mirror];
        }
        else
        {
            rows = [];
            var size = 0;
            for (var j = 0; j < items.Count; j++)
            {
                var newRow = j > 0 && _doc.SpansLines(Last(items[j - 1]), First(items[j]));
                if (j > 0 && (newRow || size == max))
                {
                    rows.Add(size);
                    size = 0;
                }

                size++;
            }

            rows.Add(size);
        }

        if (allowInline && rows.Count == 1 && items.Count <= max)
        {
            for (var j = 0; j < items.Count; j++)
            {
                Join(First(items[j]), row);
                if (j > 0)
                {
                    Join(_doc.PrevCode(First(items[j])));
                }
            }

            return rows;
        }

        var index = 0;
        foreach (var size in rows)
        {
            for (var k = 0; k < size && index < items.Count; k++, index++)
            {
                var start = First(items[index]);
                if (k == 0)
                {
                    Break(start, row);
                }
                else
                {
                    Join(start, row);
                }

                if (index > 0)
                {
                    // The comma ends the previous row (trailing comma).
                    Join(_doc.PrevCode(start));
                }
            }
        }

        return rows;
    }

    // ----- pass 2 -----

    private void LayoutQuerySpecification(QuerySpecification query)
    {
        var anchor = First(query);
        var at = new IndentSpec(anchor, 0);
        if (query.SelectElements.Count > 0)
        {
            _mirrorRows.TryGetValue(query, out var mirror);
            LayoutList([.. query.SelectElements], new IndentSpec(anchor, 1), mirror, allowInline: true);
        }

        LayoutFromWhere(query.FromClause, query.WhereClause, anchor);
        if (query.GroupByClause is { } groupBy)
        {
            Break(First(groupBy), at);
            if (groupBy.GroupingSpecifications.Count > 0)
            {
                LayoutList([.. groupBy.GroupingSpecifications], new IndentSpec(anchor, 1), null, allowInline: true);
            }
        }

        if (query.HavingClause?.SearchCondition is { } having)
        {
            Break(First(query.HavingClause), at);
            LayoutPredicate(having, anchor);
        }

        LayoutQueryTail(query, anchor);
    }

    private void LayoutQueryTail(QueryExpression query, int anchor)
    {
        var at = new IndentSpec(anchor, 0);
        if (query.OrderByClause is { } orderBy)
        {
            Break(First(orderBy), at);
            if (orderBy.OrderByElements.Count > 0)
            {
                LayoutList([.. orderBy.OrderByElements], new IndentSpec(anchor, 1), null, allowInline: true);
            }
        }

        if (query.OffsetClause is { } offset)
        {
            Break(First(offset), at);
        }

        if (query.ForClause is { } forClause)
        {
            Break(Before(forClause), at);
        }
    }

    private void LayoutBinaryQuery(BinaryQueryExpression query)
    {
        if (query.FirstQueryExpression is null || query.SecondQueryExpression is null)
        {
            return;
        }

        var anchor = First(query);
        var at = new IndentSpec(anchor, 0);
        var op = After(query.FirstQueryExpression);
        if (op > 0 && op < First(query.SecondQueryExpression))
        {
            Break(op, at);
            for (var i = op + 1; i < First(query.SecondQueryExpression); i++)
            {
                Join(i);
            }
        }

        Break(First(query.SecondQueryExpression), at);
        LayoutQueryTail(query, anchor);
    }

    /// <summary>A subquery written on one line stays on one line; a multi-line one gets the full layout, aligned on its SELECT.</summary>
    private void LockIfSingleLine(TSqlFragment? subquery)
    {
        if (subquery is not null && SingleLine(subquery))
        {
            LockInner(subquery);
        }
    }

    private void MarkIdentifier(TSqlFragment fragment)
    {
        for (var i = First(fragment); i <= Last(fragment) && i < _doc.Count; i++)
        {
            IdentifierToken[i] = true;
        }
    }

    private void MarkRecase(TSqlFragment fragment)
    {
        for (var i = First(fragment); i <= Last(fragment) && i < _doc.Count; i++)
        {
            ForceRecase[i] = true;
        }
    }

    private sealed class QueryVisitor(SqlLayout layout) : TSqlFragmentVisitor
    {
        public override void Visit(ScalarSubquery node) => layout.LockIfSingleLine(node.QueryExpression);

        public override void Visit(QueryDerivedTable node) => layout.LockIfSingleLine(node.QueryExpression);

        public override void Visit(QueryParenthesisExpression node)
        {
            if (node.QueryExpression is not null && layout.SingleLine(node))
            {
                layout.LockInner(node);
            }
        }

        public override void Visit(QuerySpecification node) => layout.LayoutQuerySpecification(node);

        public override void Visit(BinaryQueryExpression node) => layout.LayoutBinaryQuery(node);

        public override void Visit(Identifier node) => layout.MarkIdentifier(node);

        public override void Visit(SqlDataTypeReference node)
        {
            if (node.Name is { } name)
            {
                layout.MarkRecase(name);
            }
        }

        public override void Visit(FunctionCall node)
        {
            if (node.CallTarget is null && node.FunctionName is { } name && SqlBuiltIns.IsBuiltInFunction(name.Value))
            {
                layout.MarkRecase(name);
            }
        }
    }
}
