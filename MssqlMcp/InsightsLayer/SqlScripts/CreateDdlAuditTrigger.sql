-- Database-level DDL audit trigger, shared by install_insights_layer and ddl_history install. Applied only when
-- DDL_Audit does not exist yet: an existing trigger (possibly deployed by another team) is left untouched.
-- Compatible with SQL Server 2008 R2+ (only syntax that 2008 R2 accepts).
-- dbo.DDL_AuditLog name columns are varchar(100): values are read as nvarchar and cut with LEFT(..., 100)
-- so long names never raise a truncation error.
--
-- Hardened so it never blocks another principal's DDL:
-- - WITH EXECUTE AS 'dbo': callers need no INSERT permission on dbo.DDL_AuditLog (creating it needs db_owner or
--   IMPERSONATE on dbo). ORIGINAL_LOGIN() still records the real login, and the "other users" check compares it.
-- - The SET options the xml methods need are forced in the body, so sessions with non-ANSI options (legacy
--   ODBC / Access clients) do not fail with error 1934. ANSI_NULLS and QUOTED_IDENTIFIER cannot be changed inside
--   a module (the creation-time values always apply), so they are the script-level SETs below.
-- - SET XACT_ABORT OFF plus TRY/CATCH: triggers start with XACT_ABORT ON, under which any caught error still
--   dooms the transaction and SQL Server rolls the DDL back (3616). With it OFF, a failing audit INSERT
--   (truncation, constraint, ...) leaves the transaction committable, and the DDL goes through unlogged.
--   An error that dooms the transaction by itself (for example one raised inside a DML trigger on
--   dbo.DDL_AuditLog, a ROLLBACK there, or a severity 20+ error) still rolls the DDL back: SQL Server allows nothing else.
-- Rollback: drop the database trigger DDL_Audit (exact statement in README, "DDL history"). This script never drops anything.

SET ANSI_NULLS ON
GO

SET QUOTED_IDENTIFIER ON
GO

CREATE TRIGGER [DDL_Audit] ON DATABASE
WITH EXECUTE AS 'dbo'
FOR ddl_database_level_events
AS
SET NOCOUNT ON;
SET ANSI_WARNINGS ON;
SET ANSI_PADDING ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET ARITHABORT ON;
SET NUMERIC_ROUNDABORT OFF;
SET XACT_ABORT OFF;

DECLARE
   @CommandXML xml,
   @CommandText nvarchar(max),
   @ObjectName nvarchar(100),
   @s nvarchar(max);

BEGIN TRY
    SET @CommandXML = EVENTDATA();
    SET @CommandText = LTRIM(RTRIM(@CommandXML.value('(/EVENT_INSTANCE/TSQLCommand/CommandText)[1]', 'NVARCHAR(MAX)')));
    SET @ObjectName = LEFT(@CommandXML.value('(/EVENT_INSTANCE/ObjectName)[1]', 'NVARCHAR(128)'), 100);

    INSERT INTO dbo.DDL_AuditLog (HostName, LoginName, SchemaName, ObjectName, ObjectType, EventType, CommandText, CommandXML, ProgramName)
    VALUES (
        LEFT(HOST_NAME(), 100),
        LEFT(ORIGINAL_LOGIN(), 100),
        LEFT(@CommandXML.value('(/EVENT_INSTANCE/SchemaName)[1]', 'NVARCHAR(128)'), 100),
        @ObjectName,
        LEFT(@CommandXML.value('(/EVENT_INSTANCE/ObjectType)[1]', 'NVARCHAR(128)'), 100),
        LEFT(@CommandXML.value('(/EVENT_INSTANCE/EventType)[1]', 'NVARCHAR(128)'), 64),
        @CommandText,
        @CommandXML,
        LEFT(PROGRAM_NAME(), 100)
        );

    -- Tell the developer who else changed this object in the last month (kept from the original spec).
    SET @s = N'last modified ' + ISNULL(@ObjectName, N'') + N':';

    ;WITH LastPerUser AS
    (
        SELECT
            LoginName,
            PostTime,
            rn = ROW_NUMBER() OVER (
                    PARTITION BY LoginName
                    ORDER BY PostTime DESC
                 )
        FROM dbo.DDL_AuditLog
        WHERE ObjectName = @ObjectName
          AND LoginName <> LEFT(ORIGINAL_LOGIN(), 100)
          AND DATEADD(month, 1, PostTime) > GETDATE()
    )
    SELECT @s = @s +
        (
            SELECT
                CHAR(13) +
                CONVERT(NVARCHAR(10), PostTime, 103) + N' ' +
                CONVERT(NVARCHAR(8),  PostTime, 108) +
                N', LoginName : ' + ISNULL(LoginName, N'')
            FROM LastPerUser
            WHERE rn = 1
            ORDER BY PostTime DESC
            FOR XML PATH(''), TYPE
        ).value('.', 'NVARCHAR(MAX)');

    IF @@rowcount > 0 PRINT @s;
END TRY
BEGIN CATCH
    -- Never re-raise: the audit must not fail the user's DDL. Only a short warning, no command text.
    PRINT N'DDL_Audit: this change was not logged (error ' + CAST(ERROR_NUMBER() AS nvarchar(11)) + N').';
END CATCH
GO

ENABLE TRIGGER [DDL_Audit] ON DATABASE
GO
