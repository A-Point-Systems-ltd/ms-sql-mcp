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
        LastAnalyzed DATETIME2 NOT NULL CONSTRAINT DF_SchemaInsights_LastAnalyzed DEFAULT (SYSUTCDATETIME()),
        AnalyzedBy NVARCHAR(100) NULL,
        Version INT NOT NULL CONSTRAINT DF_SchemaInsights_Version DEFAULT (1),
        ModifyDateAtAnalysis DATETIME2 NULL,
        ObjectIdAtAnalysis INT NULL,
        SchemaFingerprint VARCHAR(64) NULL,
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

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_SchemaInsights_Object' AND object_id = OBJECT_ID('AIInsights.SchemaInsights'))
    CREATE INDEX IX_SchemaInsights_Object ON AIInsights.SchemaInsights(ObjectType, SchemaName, ObjectName);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_SchemaInsights_LastAnalyzed' AND object_id = OBJECT_ID('AIInsights.SchemaInsights'))
    CREATE INDEX IX_SchemaInsights_LastAnalyzed ON AIInsights.SchemaInsights(LastAnalyzed DESC);
GO

IF OBJECT_ID('AIInsights.QueryPatterns', 'U') IS NULL
BEGIN
    CREATE TABLE AIInsights.QueryPatterns (
        PatternID INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_QueryPatterns PRIMARY KEY,
        PatternName NVARCHAR(200) NOT NULL,
        QueryTemplate NVARCHAR(MAX) NOT NULL,
        Purpose NVARCHAR(MAX) NULL,
        TypicalUseCase NVARCHAR(MAX) NULL,
        PerformanceNotes NVARCHAR(MAX) NULL,
        ExampleParameters NVARCHAR(MAX) NULL,
        UsageCount INT NOT NULL CONSTRAINT DF_QueryPatterns_UsageCount DEFAULT (0),
        AvgExecutionTimeMS INT NULL,
        LastUsed DATETIME2 NULL,
        CreatedDate DATETIME2 NOT NULL CONSTRAINT DF_QueryPatterns_CreatedDate DEFAULT (SYSUTCDATETIME()),
        CreatedBy NVARCHAR(100) NULL,
        LLMModel NVARCHAR(100) NULL,
        Tags NVARCHAR(500) NULL
    );
END
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_QueryPatterns_Tags' AND object_id = OBJECT_ID('AIInsights.QueryPatterns'))
    CREATE INDEX IX_QueryPatterns_Tags ON AIInsights.QueryPatterns(Tags);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_QueryPatterns_UsageCount' AND object_id = OBJECT_ID('AIInsights.QueryPatterns'))
    CREATE INDEX IX_QueryPatterns_UsageCount ON AIInsights.QueryPatterns(UsageCount DESC);
GO

IF OBJECT_ID('AIInsights.DataQualityInsights', 'U') IS NULL
BEGIN
    CREATE TABLE AIInsights.DataQualityInsights (
        QualityInsightID INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_DataQualityInsights PRIMARY KEY,
        SchemaName NVARCHAR(128) NULL,
        TableName NVARCHAR(128) NOT NULL,
        ColumnName NVARCHAR(128) NULL,
        IssueType NVARCHAR(100) NULL,
        IssueSeverity NVARCHAR(20) NULL,
        IssueDescription NVARCHAR(MAX) NULL,
        AffectedRowsEstimate INT NULL,
        RecommendedFix NVARCHAR(MAX) NULL,
        PreventionStrategy NVARCHAR(MAX) NULL,
        Status NVARCHAR(50) NOT NULL CONSTRAINT DF_DataQuality_Status DEFAULT (N'Open'),
        DetectedDate DATETIME2 NOT NULL CONSTRAINT DF_DataQuality_Detected DEFAULT (SYSUTCDATETIME()),
        ResolvedDate DATETIME2 NULL,
        AssignedTo NVARCHAR(100) NULL
    );
END
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_DataQuality_Status' AND object_id = OBJECT_ID('AIInsights.DataQualityInsights'))
    CREATE INDEX IX_DataQuality_Status ON AIInsights.DataQualityInsights(Status, IssueSeverity);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_DataQuality_Table' AND object_id = OBJECT_ID('AIInsights.DataQualityInsights'))
    CREATE INDEX IX_DataQuality_Table ON AIInsights.DataQualityInsights(SchemaName, TableName);
GO

IF OBJECT_ID('AIInsights.BusinessRules', 'U') IS NULL
BEGIN
    CREATE TABLE AIInsights.BusinessRules (
        RuleID INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_BusinessRules PRIMARY KEY,
        RuleName NVARCHAR(200) NOT NULL,
        RuleCategory NVARCHAR(100) NULL,
        RuleDescription NVARCHAR(MAX) NULL,
        SQLExpression NVARCHAR(MAX) NULL,
        AffectedObjects NVARCHAR(MAX) NULL,
        IsImplemented BIT NOT NULL CONSTRAINT DF_BusinessRules_IsImplemented DEFAULT (0),
        ImplementedIn NVARCHAR(MAX) NULL,
        DiscoveredDate DATETIME2 NOT NULL CONSTRAINT DF_BusinessRules_Discovered DEFAULT (SYSUTCDATETIME()),
        DiscoveredBy NVARCHAR(100) NULL,
        Confidence DECIMAL(3,2) NULL,
        ValidationStatus NVARCHAR(50) NOT NULL CONSTRAINT DF_BusinessRules_Validation DEFAULT (N'Pending'),
        ValidatedBy NVARCHAR(100) NULL,
        ValidatedDate DATETIME2 NULL
    );
END
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_BusinessRules_Category' AND object_id = OBJECT_ID('AIInsights.BusinessRules'))
    CREATE INDEX IX_BusinessRules_Category ON AIInsights.BusinessRules(RuleCategory);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_BusinessRules_ValidationStatus' AND object_id = OBJECT_ID('AIInsights.BusinessRules'))
    CREATE INDEX IX_BusinessRules_ValidationStatus ON AIInsights.BusinessRules(ValidationStatus);
GO

IF OBJECT_ID('AIInsights.AnalysisSessions', 'U') IS NULL
BEGIN
    CREATE TABLE AIInsights.AnalysisSessions (
        SessionID INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_AnalysisSessions PRIMARY KEY,
        SessionGUID UNIQUEIDENTIFIER NOT NULL CONSTRAINT DF_AnalysisSessions_Guid DEFAULT (NEWID()),
        AnalysisType NVARCHAR(100) NULL,
        Scope NVARCHAR(MAX) NULL,
        InsightsGenerated INT NOT NULL CONSTRAINT DF_AnalysisSessions_InsightsGen DEFAULT (0),
        IssuesFound INT NOT NULL CONSTRAINT DF_AnalysisSessions_Issues DEFAULT (0),
        Duration INT NULL,
        StartTime DATETIME2 NOT NULL CONSTRAINT DF_AnalysisSessions_Start DEFAULT (SYSUTCDATETIME()),
        EndTime DATETIME2 NULL,
        PerformedBy NVARCHAR(100) NULL,
        LLMModel NVARCHAR(100) NULL,
        Status NVARCHAR(50) NOT NULL CONSTRAINT DF_AnalysisSessions_Status DEFAULT (N'Running')
    );
END
GO

IF OBJECT_ID('AIInsights.InsightFeedback', 'U') IS NULL
BEGIN
    CREATE TABLE AIInsights.InsightFeedback (
        FeedbackID INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_InsightFeedback PRIMARY KEY,
        InsightType NVARCHAR(50) NULL,
        InsightID INT NOT NULL,
        IsHelpful BIT NULL,
        IsAccurate BIT NULL,
        Rating INT NULL,
        Comments NVARCHAR(MAX) NULL,
        ProvidedBy NVARCHAR(100) NULL,
        ProvidedDate DATETIME2 NOT NULL CONSTRAINT DF_InsightFeedback_Provided DEFAULT (SYSUTCDATETIME())
    );
END
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Feedback_Insight' AND object_id = OBJECT_ID('AIInsights.InsightFeedback'))
    CREATE INDEX IX_Feedback_Insight ON AIInsights.InsightFeedback(InsightType, InsightID);
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
END
GO

/* DDL_Audit database trigger is applied from embedded CreateDdlAuditTrigger.sql (plain batches, no dynamic SQL). */

PRINT N'AI Insights v2 schema + DDL_AuditLog table install batch completed.';
GO
