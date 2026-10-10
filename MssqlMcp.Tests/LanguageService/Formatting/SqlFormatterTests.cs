using Mssql.McpServer.LanguageService;
using Mssql.McpServer.LanguageService.Formatting;

namespace MssqlMcp.Tests.LanguageService.Formatting;

/// <summary>The T-SQL formatter: the team's style samples as golden files, safety rules, options and range formatting.</summary>
public sealed class SqlFormatterTests
{
    private static string Format(string text, SqlFormatOptions? options = null)
    {
        var result = SqlFormatter.Format(text, options);
        Assert.True(result.Success, result.Error);
        return result.Text!;
    }

    /// <summary>Applies edits (1-based, end-exclusive, non-overlapping) to text with LF line breaks.</summary>
    private static string Apply(string text, IReadOnlyList<SqlTextEdit> edits)
    {
        var lineStarts = new List<int> { 0 };
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                lineStarts.Add(i + 1);
            }
        }

        int Offset(int line, int column) => lineStarts[line - 1] + column - 1;
        foreach (var e in edits.OrderByDescending(e => Offset(e.StartLine, e.StartColumn)))
        {
            var start = Offset(e.StartLine, e.StartColumn);
            text = text[..start] + e.NewText + text[Offset(e.EndLine, e.EndColumn)..];
        }

        return text;
    }

    private const string DeclareAndProcSample = """
        --declare with defaults
        DECLARE @companyPhone VARCHAR(20) = CAST(dbo.GetGlobal('CompanyPhone') AS VARCHAR(20))
                ,@companyName VARCHAR(200) = CAST(dbo.GetGlobal('CompanyName') AS VARCHAR(200))
                ,@ClosingDate SMALLDATETIME

        declare
                @sendSmsToVaadOnClose BIT, @sendOpenCallSmsToCustomer BIT
                ,@sendOpenCallSmsToServiceManager VARCHAR(100)
                ,@smsSentToSupplier int, @chkSendRuleMailToCompany INT

        DECLARE csr_problems CURSOR FAST_FORWARD FOR
            SELECT PID ,ClosingDate, sendSmsToVaadOnClose, sendOpenCallSmsToCustomer, sendOpenCallSmsToServiceManager, smsSentToSupplier, chkSendRuleMailToCompany
            FROM INSERTED
            WHERE (sendOpenCallSmsToCustomer = 1)

        SELECT LS.*
        FROM dbo.TableProblemsLastSub LS
            INNER JOIN @Affected A ON A.ProblemNum = LS.PID
            LEFT JOIN @Latest1 L ON L.ProblemNum = LS.PID
        WHERE L.ProblemNum IS NULL;
        go

        CREATE OR ALTER proc [dbo].[apiFileUploadDone](
            @fileID int,
            @fileAction tinyint = 12
            )
        --with encryption
        as
        begin
        	if @fileAction = 1
        		--upload done successfully
        		update FileAtts set LastUpdate = getdate() where id = @fileID

        	if @fileAction = 2
        		--upload failed - delete
        		delete from FileAtts where id = @fileID

        	select 'Done' as Status
        end
        GO

        """;

    private const string DeclareAndProcExpected = """
        --declare with defaults
        declare @companyPhone varchar(20) = cast(dbo.GetGlobal('CompanyPhone') as varchar(20))
                ,@companyName varchar(200) = cast(dbo.GetGlobal('CompanyName') as varchar(200))
                ,@ClosingDate smalldatetime

        declare
                @sendSmsToVaadOnClose bit, @sendOpenCallSmsToCustomer bit
                ,@sendOpenCallSmsToServiceManager varchar(100)
                ,@smsSentToSupplier int, @chkSendRuleMailToCompany int

        declare csr_problems cursor fast_forward for
            select
                PID, ClosingDate, sendSmsToVaadOnClose, sendOpenCallSmsToCustomer,
                sendOpenCallSmsToServiceManager, smsSentToSupplier, chkSendRuleMailToCompany
            from INSERTED
            where (sendOpenCallSmsToCustomer = 1)

        select LS.*
        from dbo.TableProblemsLastSub LS
            inner join @Affected A on A.ProblemNum = LS.PID
            left join @Latest1 L on L.ProblemNum = LS.PID
        where L.ProblemNum is null;
        go

        create or alter proc [dbo].[apiFileUploadDone](
            @fileID int,
            @fileAction tinyint = 12
            )
        --with encryption
        as
        begin
            if @fileAction = 1
                --upload done successfully
                update FileAtts set LastUpdate = getdate() where id = @fileID

            if @fileAction = 2
                --upload failed - delete
                delete from FileAtts where id = @fileID

            select 'Done' as Status
        end
        go

        """;

    private const string InsertSample = """
        insert into ccHok (
            ownerID, createDate, createdBy,
            dayOfCharge, tokenID, validfrom, validTo,
            amount, dealerNumber, comissionCostOn,
            ccTerminalID,
            curID, period, updateBy, description)
        select
            CCU.ContactID, GETDATE(), @UserID,
            DAY(CCU.BillingDate), tokenID, CCU.BillingDate, CCU.validTo,
            0, @dealerNumber, B.comissionCostOn,
            dbo.getTerminalForDealerNumber(@dealerNumber),
            1, 1, @UserID, CCU.Recurcing
        from @OutputTbl ccTokens
            inner join CC_Meshulam_Units_Contacts_Tokens CCU ON ccTokens.ContactID = CCU.ContactID
            inner join CC_Meshulam_Buildings CMB on CCU.Address = CMB.Address
            inner join Buildings B on CMB.BID = B.BID
        where
            CCU.isImported = 0
            and B.BID is NOT NULL
            and CCU.BillingDate > GETDATE()
            and (
                --specific address
                B.fullAddress like '%lapid%'
                OR
                --person's name
                CCU.FirstName like '%abc%'
                OR
                --or the building is active
                CMB.Active=1
            )

        """;

    private const string InsertExpected = """
        insert into ccHok (
            ownerID, createDate, createdBy,
            dayOfCharge, tokenID, validfrom, validTo,
            amount, dealerNumber, comissionCostOn,
            ccTerminalID,
            curID, period, updateBy, description)
        select
            CCU.ContactID, getdate(), @UserID,
            day(CCU.BillingDate), tokenID, CCU.BillingDate, CCU.validTo,
            0, @dealerNumber, B.comissionCostOn,
            dbo.getTerminalForDealerNumber(@dealerNumber),
            1, 1, @UserID, CCU.Recurcing
        from @OutputTbl ccTokens
            inner join CC_Meshulam_Units_Contacts_Tokens CCU on ccTokens.ContactID = CCU.ContactID
            inner join CC_Meshulam_Buildings CMB on CCU.Address = CMB.Address
            inner join Buildings B on CMB.BID = B.BID
        where
            CCU.isImported = 0
            and B.BID is not null
            and CCU.BillingDate > getdate()
            and (
                --specific address
                B.fullAddress like '%lapid%'
                or
                --person's name
                CCU.FirstName like '%abc%'
                or
                --or the building is active
                CMB.Active = 1
            )

        """;

    [Fact]
    public void Declare_cursor_select_and_procedure_sample_matches_the_team_style() =>
        Assert.Equal(DeclareAndProcExpected, Format(DeclareAndProcSample));

    [Fact]
    public void Insert_select_and_where_sample_matches_the_team_style() =>
        Assert.Equal(InsertExpected, Format(InsertSample));

    [Theory]
    [InlineData(DeclareAndProcSample)]
    [InlineData(InsertSample)]
    [InlineData(EdgeCases)]
    public void Formatting_twice_changes_nothing(string text)
    {
        var once = Format(text);
        Assert.Equal(once, Format(once));
    }

    private const string EdgeCases = """
        WITH cte AS (SELECT a, b FROM dbo.T WHERE x>=1), cte2 AS (
        SELECT a FROM cte
        )
        SELECT c.a, (SELECT COUNT(*) FROM dbo.U u WHERE u.a = c.a) AS cnt
        FROM cte2 c
        WHERE c.a IN (SELECT a FROM dbo.V) AND EXISTS (SELECT 1
        FROM dbo.W w WHERE w.a = c.a)
        UNION ALL
        SELECT 1, 2 FROM dbo.X ORDER BY 1 DESC
        BEGIN TRY
        UPDATE t SET t.a = s.a, t.b=-1 FROM dbo.T t INNER JOIN dbo.S s ON s.id = t.id WHERE t.a <> s.a /* changed */
        END TRY
        BEGIN CATCH
        IF @@TRANCOUNT > 0 ROLLBACK
        END CATCH
        MERGE INTO dbo.T AS tgt USING dbo.S AS src ON tgt.id = src.id WHEN MATCHED THEN UPDATE SET tgt.a = src.a WHEN NOT MATCHED THEN INSERT (id, a) VALUES (src.id, src.a);
        IF @x = 1 BEGIN SELECT 1 END ELSE IF @x = 2 SELECT 2 ELSE BEGIN SELECT 3 END
        GO
        CREATE FUNCTION dbo.fnInline(@a INT, @b INT = 2) RETURNS TABLE AS RETURN (SELECT a FROM dbo.T WHERE a = @a
        AND b = @b)
        GO

        """;

    private const string EdgeExpected = """
        with cte as (
            select a, b
            from dbo.T
            where x >= 1
        ),
        cte2 as (
            select a
            from cte
        )
        select c.a, (select count(*) from dbo.U u where u.a = c.a) as cnt
        from cte2 c
        where
            c.a in (select a from dbo.V)
            and exists (select 1
                        from dbo.W w
                        where w.a = c.a)
        union all
        select 1, 2
        from dbo.X
        order by 1 desc
        begin try
            update t
            set t.a = s.a, t.b = -1
            from dbo.T t
                inner join dbo.S s on s.id = t.id
            where t.a <> s.a /* changed */
        end try
        begin catch
            if @@TRANCOUNT > 0
                rollback
        end catch
        merge into dbo.T as tgt
        using dbo.S as src on tgt.id = src.id
        when matched then
            update set tgt.a = src.a
        when not matched then
            insert (id, a) values (src.id, src.a);
        if @x = 1
        begin
            select 1
        end
        else if @x = 2
            select 2
        else
        begin
            select 3
        end
        go
        create function dbo.fnInline(
            @a int,
            @b int = 2
            )
        returns table
        as
        return (
            select a
            from dbo.T
            where
                a = @a
                and b = @b
        )
        go

        """;

    [Fact]
    public void Ctes_subqueries_union_try_catch_merge_if_and_inline_functions_follow_the_defaults() =>
        Assert.Equal(EdgeExpected, Format(EdgeCases));

    [Fact]
    public void Identifiers_strings_comments_and_variables_keep_their_exact_text()
    {
        var text = "SELECT MyCol, [Weird Name], @MyVar, N'Mixed Case STRING' /* Keep THIS */ FROM dbo.MyTable MT -- Trailing COMMENT\n";
        Assert.Equal("select MyCol, [Weird Name], @MyVar, N'Mixed Case STRING' /* Keep THIS */\nfrom dbo.MyTable MT -- Trailing COMMENT\n", Format(text));
    }

    [Fact]
    public void User_functions_and_user_types_keep_their_case_but_built_ins_and_system_types_are_lowered()
    {
        var text = "DECLARE @a dbo.MyType, @b NVARCHAR(MAX) = dbo.GetGlobal(GETDATE())";
        Assert.Equal("declare @a dbo.MyType\n        ,@b nvarchar(max) = dbo.GetGlobal(getdate())", Format(text));
    }

    [Fact]
    public void A_single_line_comment_is_never_joined_with_the_code_after_it()
    {
        // The JOIN keyword is moved to its own line; the comment before it must still end its line.
        var text = "SELECT a FROM dbo.T t -- base\nINNER JOIN dbo.U u ON u.id = t.id";
        var formatted = Format(text);
        Assert.Contains("-- base\n", formatted, StringComparison.Ordinal);
        Assert.Equal("select a\nfrom dbo.T t -- base\n    inner join dbo.U u on u.id = t.id", formatted);
    }

    [Fact]
    public void Dynamic_sql_inside_strings_is_untouched()
    {
        var text = "DECLARE @sql NVARCHAR(MAX) = N'SELECT  *  FROM T\n   WHERE A=1'\nEXEC sp_executesql @sql";
        Assert.Equal("declare @sql nvarchar(max) = N'SELECT  *  FROM T\n   WHERE A=1'\nexec sp_executesql @sql", Format(text));
    }

    [Fact]
    public void Go_is_never_inserted_and_semicolons_are_never_added_or_removed()
    {
        var text = "SELECT 1; SELECT 2\nSELECT 3;";
        Assert.Equal("select 1;\nselect 2\nselect 3;", Format(text));
    }

    [Fact]
    public void Text_that_does_not_parse_is_refused_with_its_position()
    {
        var result = SqlFormatter.Format("declare @a int,\n,@b int");
        Assert.False(result.Success);
        Assert.Equal(2, result.ErrorLine);
        Assert.Empty(result.Edits);
        Assert.Contains("syntax error", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Upper_case_tabs_and_crlf_options_are_honoured()
    {
        var text = "if @a = 1\r\nbegin\r\nselect a from t where a = 1\r\nend\r\n";
        var formatted = Format(text, new SqlFormatOptions(UseTabs: true, KeywordCase: KeywordCase.Upper));
        Assert.Equal("IF @a = 1\r\nBEGIN\r\n\tSELECT a\r\n\tFROM t\r\n\tWHERE a = 1\r\nEND\r\n", formatted);
    }

    [Fact]
    public void Preserve_case_keeps_every_keyword_as_written()
    {
        Assert.Equal("SELECT a\nFrom t", Format("SELECT a From t", new SqlFormatOptions(KeywordCase: KeywordCase.Preserve)));
    }

    [Fact]
    public void Max_items_per_row_is_configurable()
    {
        Assert.Equal("select\n    a, b,\n    c\nfrom t", Format("select a, b, c from t", new SqlFormatOptions(MaxItemsPerRow: 2)));
    }

    [Fact]
    public void Whole_document_edits_reproduce_the_formatted_text()
    {
        var result = SqlFormatter.Format(InsertSample);
        Assert.True(result.Success);
        Assert.Equal(result.Text, Apply(InsertSample, result.Edits));
    }

    [Fact]
    public void Range_formatting_only_touches_the_selected_statements()
    {
        var text = "SELECT a FROM t\nSELECT b FROM u WHERE b=1\nSELECT c FROM v\n";
        // Line 2 only.
        var result = SqlFormatter.Format(text, range: new TextRange(2, 1, 2, 10));
        Assert.True(result.Success, result.Error);
        Assert.Equal("SELECT a FROM t\nselect b\nfrom u\nwhere b = 1\nSELECT c FROM v\n", Apply(text, result.Edits));
    }

    [Fact]
    public void Range_formatting_widens_a_partial_statement_selection_to_the_whole_statement()
    {
        var text = "SELECT a FROM t WHERE a=1 AND b=2\nSELECT c FROM v\n";
        // Selects only "WHERE a=1".
        var result = SqlFormatter.Format(text, range: new TextRange(1, 17, 1, 26));
        Assert.True(result.Success, result.Error);
        Assert.Equal("select a\nfrom t\nwhere\n    a = 1\n    and b = 2\nSELECT c FROM v\n", Apply(text, result.Edits));
    }

    [Fact]
    public void Range_formatting_still_works_when_another_part_of_the_document_does_not_parse()
    {
        var text = "SELECT FROM WHERE\n    SELECT b FROM u WHERE b=1\nSELEC broken\n";
        var result = SqlFormatter.Format(text, range: new TextRange(2, 1, 3, 1));
        Assert.True(result.Success, result.Error);
        Assert.Equal("SELECT FROM WHERE\n    select b\n    from u\n    where b = 1\nSELEC broken\n", Apply(text, result.Edits));
    }

    [Fact]
    public void Empty_and_comment_only_documents_succeed()
    {
        Assert.Equal(string.Empty, Format(string.Empty));
        Assert.Equal("-- only a comment\n", Format("-- only a comment\n"));
    }
}
