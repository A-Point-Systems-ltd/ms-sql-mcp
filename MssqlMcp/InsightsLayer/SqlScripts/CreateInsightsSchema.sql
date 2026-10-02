-- AI Insights Data Layer v2 + DDL audit (idempotent install)
-- No helper views or stored procedures for app logic (maintained in MCP host).

/* ---- Drop legacy objects from v1 documentation script (if present) ---- */
IF OBJECT_ID('AIInsights.usp_GetObjectInsights', 'P') IS NOT NULL
    DROP PROCEDURE AIInsights.usp_GetObjectInsights;
GO
IF OBJECT_ID('AIInsights.usp_LogQueryPatternUsage', 'P') IS NOT NULL
    DROP PROCEDURE AIInsights.usp_LogQueryPatternUsage;
GO
IF OBJECT_ID('AIInsights.usp_UpsertSchemaInsight', 'P') IS NOT NULL
    DROP PROCEDURE AIInsights.usp_UpsertSchemaInsight;
GO
IF OBJECT_ID('AIInsights.vw_TopQueryPatterns', 'V') IS NOT NULL
    DROP VIEW AIInsights.vw_TopQueryPatterns;
GO
IF OBJECT_ID('AIInsights.vw_RecentInsights', 'V') IS NOT NULL
    DROP VIEW AIInsights.vw_RecentInsights;
GO

IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = N'AIInsights')
    EXEC(N'CREATE SCHEMA AIInsights');
GO

/* ---- Core tables (create if missing) ---- */
IF OBJECT_ID('AIInsights.SchemaInsights', 'U') IS NULL
BEGIN
    CREATE TABLE AIInsights.SchemaInsights (
        InsightID INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_SchemaInsights PRIMARY KEY,
        ObjectType NVARCHAR(50) NOT NULL,
        SchemaName NVARCHAR(128) NULL,
        ObjectName NVARCHAR(128) NOT NULL,
        ColumnName NVARCHAR(128) NULL,
        Description NVARCHAR(MAX) NULL,
        BusinessPurpose NVARCHAR(MAX) NULL,
        DataPatterns NVARCHAR(MAX) NULL,
        UsageGuidelines NVARCHAR(MAX) NULL,
        RelatedObjects NVARCHAR(MAX) NULL,
        LLMModel NVARCHAR(100) NULL,
        Confidence DECIMAL(3,2) NULL,
        LastAnalyzed DATETIME2 NOT NULL CONSTRAINT DF_SchemaInsights_LastAnalyzed DEFAULT (GETDATE()),
        AnalyzedBy NVARCHAR(100) NULL,
        Version INT NOT NULL CONSTRAINT DF_SchemaInsights_Version DEFAULT (1),
        ModifyDateAtAnalysis DATETIME2 NULL,
        ObjectIdAtAnalysis INT NULL,
        SchemaFingerprint VARCHAR(64) NULL,
        RowCountAtAnalysis BIGINT NULL,
        CONSTRAINT CK_SchemaInsights_Confidence CHECK (Confidence IS NULL OR (Confidence >= 0 AND Confidence <= 1))
    );
END
GO

IF COL_LENGTH('AIInsights.SchemaInsights', 'ModifyDateAtAnalysis') IS NULL
    ALTER TABLE AIInsights.SchemaInsights ADD ModifyDateAtAnalysis DATETIME2 NULL;
GO
IF COL_LENGTH('AIInsights.SchemaInsights', 'ObjectIdAtAnalysis') IS NULL
    ALTER TABLE AIInsights.SchemaInsights ADD ObjectIdAtAnalysis INT NULL;
GO
IF COL_LENGTH('AIInsights.SchemaInsights', 'SchemaFingerprint') IS NULL
    ALTER TABLE AIInsights.SchemaInsights ADD SchemaFingerprint VARCHAR(64) NULL;
GO
IF COL_LENGTH('AIInsights.SchemaInsights', 'RowCountAtAnalysis') IS NULL
    ALTER TABLE AIInsights.SchemaInsights ADD RowCountAtAnalysis BIGINT NULL;
GO

/* ---- Ensure LastAnalyzed default uses local server time (GETDATE) on existing installs ---- */
IF EXISTS (
    SELECT 1
    FROM sys.default_constraints dc
    JOIN sys.columns c ON c.default_object_id = dc.object_id
    WHERE dc.parent_object_id = OBJECT_ID('AIInsights.SchemaInsights')
      AND c.name = N'LastAnalyzed'
      AND dc.definition <> '(getdate())'
)
BEGIN
    DECLARE @dfName SYSNAME;
    DECLARE @dropSql NVARCHAR(400);
    SELECT @dfName = dc.name
    FROM sys.default_constraints dc
    JOIN sys.columns c ON c.default_object_id = dc.object_id
    WHERE dc.parent_object_id = OBJECT_ID('AIInsights.SchemaInsights')
      AND c.name = N'LastAnalyzed';

    IF @dfName IS NOT NULL
    BEGIN
        -- EXEC() accepts only literals and variables, so the statement is built first.
        SET @dropSql = N'ALTER TABLE AIInsights.SchemaInsights DROP CONSTRAINT ' + QUOTENAME(@dfName);
        EXEC(@dropSql);
    END

    ALTER TABLE AIInsights.SchemaInsights
        ADD CONSTRAINT DF_SchemaInsights_LastAnalyzed DEFAULT (GETDATE()) FOR LastAnalyzed;
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_SchemaInsights_Object' AND object_id = OBJECT_ID('AIInsights.SchemaInsights'))
    CREATE INDEX IX_SchemaInsights_Object ON AIInsights.SchemaInsights(ObjectType, SchemaName, ObjectName);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_SchemaInsights_LastAnalyzed' AND object_id = OBJECT_ID('AIInsights.SchemaInsights'))
    CREATE INDEX IX_SchemaInsights_LastAnalyzed ON AIInsights.SchemaInsights(LastAnalyzed DESC);
GO

/* ---- Remove legacy/non-active tables from earlier schema versions (only when empty and unreferenced by a foreign key: never destroy data, never fail install) ---- */
IF OBJECT_ID('AIInsights.InsightFeedback', 'U') IS NOT NULL
    EXEC(N'IF NOT EXISTS (SELECT 1 FROM AIInsights.InsightFeedback) AND NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE referenced_object_id = OBJECT_ID(N''AIInsights.InsightFeedback'')) DROP TABLE AIInsights.InsightFeedback;');
GO
IF OBJECT_ID('AIInsights.AnalysisSessions', 'U') IS NOT NULL
    EXEC(N'IF NOT EXISTS (SELECT 1 FROM AIInsights.AnalysisSessions) AND NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE referenced_object_id = OBJECT_ID(N''AIInsights.AnalysisSessions'')) DROP TABLE AIInsights.AnalysisSessions;');
GO
IF OBJECT_ID('AIInsights.BusinessRules', 'U') IS NOT NULL
    EXEC(N'IF NOT EXISTS (SELECT 1 FROM AIInsights.BusinessRules) AND NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE referenced_object_id = OBJECT_ID(N''AIInsights.BusinessRules'')) DROP TABLE AIInsights.BusinessRules;');
GO
IF OBJECT_ID('AIInsights.DataQualityInsights', 'U') IS NOT NULL
    EXEC(N'IF NOT EXISTS (SELECT 1 FROM AIInsights.DataQualityInsights) AND NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE referenced_object_id = OBJECT_ID(N''AIInsights.DataQualityInsights'')) DROP TABLE AIInsights.DataQualityInsights;');
GO
IF OBJECT_ID('AIInsights.QueryPatterns', 'U') IS NOT NULL
    EXEC(N'IF NOT EXISTS (SELECT 1 FROM AIInsights.QueryPatterns) AND NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE referenced_object_id = OBJECT_ID(N''AIInsights.QueryPatterns'')) DROP TABLE AIInsights.QueryPatterns;');
GO

IF OBJECT_ID('AIInsights.InsightHistory', 'U') IS NULL
BEGIN
    CREATE TABLE AIInsights.InsightHistory (
        HistoryID INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_InsightHistory PRIMARY KEY,
        OriginalInsightID INT NULL,
        ObjectType NVARCHAR(50) NOT NULL,
        SchemaName NVARCHAR(128) NULL,
        ObjectName NVARCHAR(128) NOT NULL,
        ColumnName NVARCHAR(128) NULL,
        Description NVARCHAR(MAX) NULL,
        BusinessPurpose NVARCHAR(MAX) NULL,
        DataPatterns NVARCHAR(MAX) NULL,
        UsageGuidelines NVARCHAR(MAX) NULL,
        RelatedObjects NVARCHAR(MAX) NULL,
        LLMModel NVARCHAR(100) NULL,
        Confidence DECIMAL(3,2) NULL,
        LastAnalyzed DATETIME2 NULL,
        AnalyzedBy NVARCHAR(100) NULL,
        Version INT NULL,
        ModifyDateAtAnalysis DATETIME2 NULL,
        ObjectIdAtAnalysis INT NULL,
        SchemaFingerprint VARCHAR(64) NULL,
        RowCountAtAnalysis BIGINT NULL,
        ArchivedAt DATETIME2 NOT NULL CONSTRAINT DF_InsightHistory_Archived DEFAULT (SYSUTCDATETIME()),
        ArchiveReason NVARCHAR(200) NULL,
        ArchivedByEvent NVARCHAR(64) NULL,
        SourceDdlAuditID INT NULL
    );
END
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_InsightHistory_Object' AND object_id = OBJECT_ID('AIInsights.InsightHistory'))
    CREATE INDEX IX_InsightHistory_Object ON AIInsights.InsightHistory(SchemaName, ObjectName, ObjectType);
GO
IF COL_LENGTH('AIInsights.InsightHistory', 'RowCountAtAnalysis') IS NULL
    ALTER TABLE AIInsights.InsightHistory ADD RowCountAtAnalysis BIGINT NULL;
GO

IF OBJECT_ID('AIInsights.DdlChangeWatermark', 'U') IS NULL
BEGIN
    CREATE TABLE AIInsights.DdlChangeWatermark (
        SingletonId INT NOT NULL CONSTRAINT PK_DdlChangeWatermark PRIMARY KEY,
        LastProcessedAuditID INT NOT NULL CONSTRAINT DF_DdlWatermark_LastId DEFAULT (0),
        LastProcessedAt DATETIME2 NULL
    );
END
GO
IF NOT EXISTS (SELECT 1 FROM AIInsights.DdlChangeWatermark WHERE SingletonId = 1)
    INSERT INTO AIInsights.DdlChangeWatermark (SingletonId, LastProcessedAuditID) VALUES (1, 0);
GO

/* ---- dbo.DDL_AuditLog + database DDL trigger (verbatim from spec) ---- */
IF OBJECT_ID(N'dbo.DDL_AuditLog', N'U') IS NULL
BEGIN
    SET ANSI_NULLS ON;
    SET QUOTED_IDENTIFIER ON;

    CREATE TABLE [dbo].[DDL_AuditLog](
        [ID] [int] IDENTITY(1,1) NOT NULL,
        [PostTime] [datetime] NOT NULL,
        [HostName] [varchar](100) NULL,
        [LoginName] [varchar](100) NULL,
        [SchemaName] [varchar](100) NULL,
        [ObjectName] [varchar](100) NULL,
        [ObjectType] [varchar](100) NULL,
        [EventType] [varchar](64) NULL,
        [CommandText] [nvarchar](max) NULL,
        [CommandXML] [xml] NULL,
        [ProgramName] [varchar](100) NULL,
    PRIMARY KEY CLUSTERED 
    (
        [ID] ASC
    )WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]
    ) ON [PRIMARY] TEXTIMAGE_ON [PRIMARY];

    ALTER TABLE [dbo].[DDL_AuditLog] ADD  DEFAULT (getdate()) FOR [PostTime];

    -- Insight DDL-event lookups and the trigger's "last modified" lookup filter by ObjectName.
    -- Only a table created here gets it; an existing (possibly client-owned) table is never altered.
    CREATE NONCLUSTERED INDEX [IX_DDL_AuditLog_ObjectName] ON [dbo].[DDL_AuditLog] ([ObjectName], [ID]) INCLUDE ([SchemaName], [LoginName], [PostTime]);
END
GO

/* DDL_Audit database trigger is applied from embedded CreateDdlAuditTrigger.sql (plain batches, no dynamic SQL). */

PRINT N'AI Insights v2 schema + DDL_AuditLog table install batch completed.';
GO
