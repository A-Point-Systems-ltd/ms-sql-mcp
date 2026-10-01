-- dbo.DDL_AuditLog for the extension's DDL history (ddl_history install). Created only when missing:
-- an existing table (possibly another team's) is never altered. Compatible with SQL Server 2008 R2+.
-- The DDL_Audit trigger is applied separately from CreateDdlAuditTrigger.sql.

SET ANSI_NULLS ON
GO

SET QUOTED_IDENTIFIER ON
GO

IF OBJECT_ID(N'dbo.DDL_AuditLog', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[DDL_AuditLog](
        [ID] [int] IDENTITY(1,1) NOT NULL,
        [PostTime] [datetime] NOT NULL CONSTRAINT [DF_DDL_Audit_PostTime] DEFAULT (getdate()),
        [HostName] [varchar](100) NULL,
        [LoginName] [varchar](100) NULL,
        [SchemaName] [varchar](100) NULL,
        [ObjectName] [varchar](100) NULL,
        [ObjectType] [varchar](100) NULL,
        [EventType] [varchar](64) NULL,
        [CommandText] [nvarchar](max) NULL,
        [CommandXML] [xml] NULL,
        [ProgramName] [varchar](100) NULL,
        CONSTRAINT [PK_DDL_AuditLog] PRIMARY KEY CLUSTERED ([ID] ASC)
    );
END
GO
