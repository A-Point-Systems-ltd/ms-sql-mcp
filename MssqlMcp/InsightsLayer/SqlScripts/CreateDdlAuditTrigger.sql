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
   @CommandText varchar(max)

SET @CommandXML=eventdata( )
SET @CommandText=@CommandXML.value( '(/EVENT_INSTANCE/TSQLCommand/CommandText)[1]', 'VARCHAR(max)' )
SET @CommandText=ltrim( rtrim( replace( @CommandText, '', '' ) ) )

IF 1=0
BEGIN
	ROLLBACK;
	RETURN
END

INSERT INTO dbo.DDL_AuditLog (HostName, LoginName, SchemaName, ObjectName, ObjectType, EventType, CommandText, CommandXML, ProgramName)
VALUES (host_name( ), 
	SUSER_SNAME(),
	@CommandXML.value( '(/EVENT_INSTANCE/SchemaName)[1]', 'VARCHAR(100)'),
	@CommandXML.value( '(/EVENT_INSTANCE/ObjectName)[1]', 'VARCHAR(100)'),
	@CommandXML.value( '(/EVENT_INSTANCE/ObjectType)[1]', 'VARCHAR(100)'),
	@CommandXML.value( '(/EVENT_INSTANCE/EventType)[1]', 'VARCHAR(64)'),
	@CommandText,
	@CommandXML,
	PROGRAM_NAME()
	)

	DECLARE @ObjectName VARCHAR(100) =
    @CommandXML.value('(/EVENT_INSTANCE/ObjectName)[1]', 'VARCHAR(100)');

DECLARE @s NVARCHAR(MAX) = N'last modified ' + @ObjectName + N':';

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
      AND LoginName <> SUSER_SNAME()
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
