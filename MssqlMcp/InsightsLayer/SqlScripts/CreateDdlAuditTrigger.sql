-- Database-level DDL audit trigger. Applied only by an explicit install_insights_layer call, and only
-- when DDL_Audit does not exist yet: an existing trigger (possibly deployed by another team) is left untouched.
-- Compatible with SQL Server 2008 R2+.
-- dbo.DDL_AuditLog name columns are varchar(100): values are read as nvarchar and cut with LEFT(..., 100)
-- so long names never raise a truncation error that would roll back the caller's DDL.

SET ANSI_NULLS ON
GO

SET QUOTED_IDENTIFIER ON
GO

CREATE TRIGGER [DDL_Audit] ON DATABASE
FOR ddl_database_level_events
AS
SET NOCOUNT ON
DECLARE
   @CommandXML xml,
   @CommandText nvarchar(max),
   @ObjectName nvarchar(100)

SET @CommandXML = EVENTDATA()
SET @CommandText = LTRIM(RTRIM(@CommandXML.value('(/EVENT_INSTANCE/TSQLCommand/CommandText)[1]', 'NVARCHAR(MAX)')))
SET @ObjectName = LEFT(@CommandXML.value('(/EVENT_INSTANCE/ObjectName)[1]', 'NVARCHAR(128)'), 100)

INSERT INTO dbo.DDL_AuditLog (HostName, LoginName, SchemaName, ObjectName, ObjectType, EventType, CommandText, CommandXML, ProgramName)
VALUES (
    LEFT(HOST_NAME(), 100),
    LEFT(SUSER_SNAME(), 100),
    LEFT(@CommandXML.value('(/EVENT_INSTANCE/SchemaName)[1]', 'NVARCHAR(128)'), 100),
    @ObjectName,
    LEFT(@CommandXML.value('(/EVENT_INSTANCE/ObjectType)[1]', 'NVARCHAR(128)'), 100),
    LEFT(@CommandXML.value('(/EVENT_INSTANCE/EventType)[1]', 'NVARCHAR(128)'), 64),
    @CommandText,
    @CommandXML,
    LEFT(PROGRAM_NAME(), 100)
    )

-- Tell the developer who else changed this object in the last month (kept from the original spec).
DECLARE @s NVARCHAR(MAX) = N'last modified ' + ISNULL(@ObjectName, N'') + N':';

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
      AND LoginName <> LEFT(SUSER_SNAME(), 100)
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

IF @@rowcount > 0 PRINT @s
GO

ENABLE TRIGGER [DDL_Audit] ON DATABASE
GO