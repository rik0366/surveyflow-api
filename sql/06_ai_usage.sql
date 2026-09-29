-- =============================================================================
-- AiUsage — one row per OpenAI call, for billing/cost reporting.
--
-- Populated by AiUsageLogger.Log() from IntegrationsController.InvokeLLM
-- and JobsController.AnalyzeSentiment. Attribution rules:
--   • Admin actions (TextAnalytics, SaveToLibrary quality): logged-in admin's email
--   • Respondent-triggered calls (AI follow-up, sentiment batch): survey owner's email
-- =============================================================================

USE SurveyFlow;
GO

IF OBJECT_ID(N'dbo.AiUsage', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.AiUsage (
        id                CHAR(36)        NOT NULL CONSTRAINT PK_AiUsage PRIMARY KEY,
        called_at         DATETIME2(3)    NOT NULL CONSTRAINT DF_AiUsage_called  DEFAULT SYSUTCDATETIME(),
        user_email        NVARCHAR(320)   NULL,
        survey_id         CHAR(36)        NULL,
        endpoint          NVARCHAR(100)   NOT NULL,
        model             NVARCHAR(100)   NULL,
        prompt_tokens     INT             NULL,
        completion_tokens INT             NULL,
        total_tokens      INT             NULL,
        success           BIT             NOT NULL CONSTRAINT DF_AiUsage_success DEFAULT 1
    );
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_AiUsage_called_at' AND object_id = OBJECT_ID(N'dbo.AiUsage'))
    CREATE INDEX IX_AiUsage_called_at ON dbo.AiUsage (called_at DESC);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_AiUsage_user' AND object_id = OBJECT_ID(N'dbo.AiUsage'))
    CREATE INDEX IX_AiUsage_user ON dbo.AiUsage (user_email);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_AiUsage_survey' AND object_id = OBJECT_ID(N'dbo.AiUsage'))
    CREATE INDEX IX_AiUsage_survey ON dbo.AiUsage (survey_id);
GO

PRINT 'AiUsage table created.';
GO
