-- =============================================================================
-- SurveyFlow database schema
-- Target: SQL Server 2017 Standard or later
-- Run as a sysadmin (sa or equivalent) the first time. Idempotent — safe to
-- re-run; CREATE statements are guarded.
-- =============================================================================


-- -----------------------------------------------------------------------------
-- 1. Database
-- -----------------------------------------------------------------------------
IF DB_ID(N'SurveyFlow') IS NULL
BEGIN
    CREATE DATABASE SurveyFlow;
END;
GO

USE SurveyFlow;
GO


-- -----------------------------------------------------------------------------
-- 2. Users
--    Forms-auth login table. password_hash is BCrypt (handled by API layer).
-- -----------------------------------------------------------------------------
IF OBJECT_ID(N'dbo.Users', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.Users (
        id              CHAR(36)        NOT NULL CONSTRAINT PK_Users PRIMARY KEY,
        email           NVARCHAR(320)   NOT NULL,
        password_hash   NVARCHAR(255)   NOT NULL,
        full_name       NVARCHAR(200)   NULL,
        role            NVARCHAR(20)    NOT NULL CONSTRAINT DF_Users_role        DEFAULT 'user',
        is_active       BIT             NOT NULL CONSTRAINT DF_Users_is_active   DEFAULT 1,
        created_date    DATETIME2(3)    NOT NULL CONSTRAINT DF_Users_created     DEFAULT SYSUTCDATETIME(),
        created_by      NVARCHAR(320)   NULL,
        updated_date    DATETIME2(3)    NOT NULL CONSTRAINT DF_Users_updated     DEFAULT SYSUTCDATETIME(),
        CONSTRAINT UQ_Users_email UNIQUE (email),
        CONSTRAINT CK_Users_role  CHECK (role IN ('admin', 'user'))
    );
END;
GO


-- -----------------------------------------------------------------------------
-- 3. Surveys
--    questions / settings stored as JSON to mirror Base44 nested shape exactly,
--    so the existing React code does not need changes.
-- -----------------------------------------------------------------------------
IF OBJECT_ID(N'dbo.Surveys', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.Surveys (
        id              CHAR(36)        NOT NULL CONSTRAINT PK_Surveys PRIMARY KEY,
        title           NVARCHAR(500)   NOT NULL,
        description     NVARCHAR(MAX)   NULL,
        status          NVARCHAR(20)    NOT NULL CONSTRAINT DF_Surveys_status   DEFAULT 'draft',
        questions_json  NVARCHAR(MAX)   NULL,
        settings_json   NVARCHAR(MAX)   NULL,
        created_date    DATETIME2(3)    NOT NULL CONSTRAINT DF_Surveys_created  DEFAULT SYSUTCDATETIME(),
        created_by      NVARCHAR(320)   NOT NULL,
        updated_date    DATETIME2(3)    NOT NULL CONSTRAINT DF_Surveys_updated  DEFAULT SYSUTCDATETIME(),
        CONSTRAINT CK_Surveys_status     CHECK (status IN ('draft', 'active', 'closed')),
        CONSTRAINT CK_Surveys_questions  CHECK (questions_json IS NULL OR ISJSON(questions_json) = 1),
        CONSTRAINT CK_Surveys_settings   CHECK (settings_json  IS NULL OR ISJSON(settings_json)  = 1)
    );
END;
GO


-- -----------------------------------------------------------------------------
-- 4. SurveyInvites
--    One row per invitee. token is the secret used in the survey URL.
-- -----------------------------------------------------------------------------
IF OBJECT_ID(N'dbo.SurveyInvites', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.SurveyInvites (
        id              CHAR(36)        NOT NULL CONSTRAINT PK_SurveyInvites PRIMARY KEY,
        survey_id       CHAR(36)        NOT NULL,
        email           NVARCHAR(320)   NOT NULL,
        token           NVARCHAR(100)   NOT NULL,
        status          NVARCHAR(20)    NOT NULL CONSTRAINT DF_SurveyInvites_status  DEFAULT 'pending',
        sent_at         DATETIME2(3)    NULL,
        created_date    DATETIME2(3)    NOT NULL CONSTRAINT DF_SurveyInvites_created DEFAULT SYSUTCDATETIME(),
        created_by      NVARCHAR(320)   NULL,
        updated_date    DATETIME2(3)    NOT NULL CONSTRAINT DF_SurveyInvites_updated DEFAULT SYSUTCDATETIME(),
        CONSTRAINT FK_SurveyInvites_Survey
            FOREIGN KEY (survey_id) REFERENCES dbo.Surveys(id) ON DELETE CASCADE,
        CONSTRAINT UQ_SurveyInvites_token UNIQUE (token),
        CONSTRAINT CK_SurveyInvites_status
            CHECK (status IN ('pending', 'sent', 'opened', 'started', 'completed'))
    );
END;
GO


-- -----------------------------------------------------------------------------
-- 5. SurveyResponses
--    answers stored as JSON keyed by question id. sentiment_* columns are
--    placeholders for phase 2 (sentiment analysis); harmless when unused.
--
--    FK to SurveyInvites uses NO ACTION on purpose: SQL Server forbids two
--    cascade paths from Survey to SurveyResponses, and we already cascade
--    via Surveys.id. Deleting an invite directly will fail if responses
--    reference it — clean those up explicitly.
-- -----------------------------------------------------------------------------
IF OBJECT_ID(N'dbo.SurveyResponses', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.SurveyResponses (
        id                      CHAR(36)        NOT NULL CONSTRAINT PK_SurveyResponses PRIMARY KEY,
        survey_id               CHAR(36)        NOT NULL,
        invite_id               CHAR(36)        NULL,
        respondent_email        NVARCHAR(320)   NULL,
        status                  NVARCHAR(20)    NOT NULL CONSTRAINT DF_SurveyResponses_status DEFAULT 'in_progress',
        answers_json            NVARCHAR(MAX)   NULL,
        current_question_index  INT             NOT NULL CONSTRAINT DF_SurveyResponses_cqi    DEFAULT 0,
        device_type             NVARCHAR(20)    NULL,
        started_at              DATETIME2(3)    NULL,
        completed_at            DATETIME2(3)    NULL,
        sentiment_score         FLOAT           NULL,
        sentiment_label         NVARCHAR(50)    NULL,
        created_date            DATETIME2(3)    NOT NULL CONSTRAINT DF_SurveyResponses_created DEFAULT SYSUTCDATETIME(),
        created_by              NVARCHAR(320)   NULL,
        updated_date            DATETIME2(3)    NOT NULL CONSTRAINT DF_SurveyResponses_updated DEFAULT SYSUTCDATETIME(),
        CONSTRAINT FK_SurveyResponses_Survey
            FOREIGN KEY (survey_id) REFERENCES dbo.Surveys(id) ON DELETE CASCADE,
        CONSTRAINT FK_SurveyResponses_Invite
            FOREIGN KEY (invite_id) REFERENCES dbo.SurveyInvites(id) ON DELETE NO ACTION,
        CONSTRAINT CK_SurveyResponses_status
            CHECK (status IN ('in_progress', 'completed')),
        CONSTRAINT CK_SurveyResponses_device
            CHECK (device_type IS NULL OR device_type IN ('mobile', 'desktop')),
        CONSTRAINT CK_SurveyResponses_answers
            CHECK (answers_json IS NULL OR ISJSON(answers_json) = 1)
    );
END;
GO


-- -----------------------------------------------------------------------------
-- 6. MailingLists
-- -----------------------------------------------------------------------------
IF OBJECT_ID(N'dbo.MailingLists', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.MailingLists (
        id              CHAR(36)        NOT NULL CONSTRAINT PK_MailingLists PRIMARY KEY,
        name            NVARCHAR(200)   NOT NULL,
        description     NVARCHAR(MAX)   NULL,
        emails_json     NVARCHAR(MAX)   NULL,
        created_date    DATETIME2(3)    NOT NULL CONSTRAINT DF_MailingLists_created DEFAULT SYSUTCDATETIME(),
        created_by      NVARCHAR(320)   NOT NULL,
        updated_date    DATETIME2(3)    NOT NULL CONSTRAINT DF_MailingLists_updated DEFAULT SYSUTCDATETIME(),
        CONSTRAINT CK_MailingLists_emails CHECK (emails_json IS NULL OR ISJSON(emails_json) = 1)
    );
END;
GO


-- -----------------------------------------------------------------------------
-- 7. QuestionLibrary
--    Saved questions for reuse. Type-specific fields stored as JSON arrays
--    so the React form can write the same shape it does today.
-- -----------------------------------------------------------------------------
IF OBJECT_ID(N'dbo.QuestionLibrary', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.QuestionLibrary (
        id                          CHAR(36)        NOT NULL CONSTRAINT PK_QuestionLibrary PRIMARY KEY,
        name                        NVARCHAR(300)   NULL,
        tags_json                   NVARCHAR(MAX)   NULL,
        type                        NVARCHAR(50)    NOT NULL,
        text                        NVARCHAR(MAX)   NOT NULL,
        description                 NVARCHAR(MAX)   NULL,
        required                    BIT             NULL,
        options_json                NVARCHAR(MAX)   NULL,
        grid_rows_json              NVARCHAR(MAX)   NULL,
        grid_columns_json           NVARCHAR(MAX)   NULL,
        grid_single_select          BIT             NULL,
        rating_max                  INT             NULL,
        categories_json             NVARCHAR(MAX)   NULL,
        allocation_must_total_100   BIT             NULL,
        quality_analysis_json       NVARCHAR(MAX)   NULL,
        created_date                DATETIME2(3)    NOT NULL CONSTRAINT DF_QuestionLibrary_created DEFAULT SYSUTCDATETIME(),
        created_by                  NVARCHAR(320)   NOT NULL,
        updated_date                DATETIME2(3)    NOT NULL CONSTRAINT DF_QuestionLibrary_updated DEFAULT SYSUTCDATETIME(),
        CONSTRAINT CK_QL_tags             CHECK (tags_json             IS NULL OR ISJSON(tags_json)             = 1),
        CONSTRAINT CK_QL_options          CHECK (options_json          IS NULL OR ISJSON(options_json)          = 1),
        CONSTRAINT CK_QL_grid_rows        CHECK (grid_rows_json        IS NULL OR ISJSON(grid_rows_json)        = 1),
        CONSTRAINT CK_QL_grid_columns     CHECK (grid_columns_json     IS NULL OR ISJSON(grid_columns_json)     = 1),
        CONSTRAINT CK_QL_categories       CHECK (categories_json       IS NULL OR ISJSON(categories_json)       = 1),
        CONSTRAINT CK_QL_quality_analysis CHECK (quality_analysis_json IS NULL OR ISJSON(quality_analysis_json) = 1)
    );
END;
GO


-- -----------------------------------------------------------------------------
-- 8. Indexes
--    Driven by the queries the React code makes (see audit notes).
-- -----------------------------------------------------------------------------

-- Dashboard.jsx: Survey.filter({ created_by: me.email }, "-created_date")
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Surveys_created_by_date' AND object_id = OBJECT_ID(N'dbo.Surveys'))
    CREATE INDEX IX_Surveys_created_by_date ON dbo.Surveys (created_by, created_date DESC);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Surveys_status' AND object_id = OBJECT_ID(N'dbo.Surveys'))
    CREATE INDEX IX_Surveys_status ON dbo.Surveys (status);

-- SurveyDistribute.jsx: SurveyInvite.filter({ survey_id: id }, "-created_date")
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_SurveyInvites_survey' AND object_id = OBJECT_ID(N'dbo.SurveyInvites'))
    CREATE INDEX IX_SurveyInvites_survey ON dbo.SurveyInvites (survey_id, created_date DESC);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_SurveyInvites_email' AND object_id = OBJECT_ID(N'dbo.SurveyInvites'))
    CREATE INDEX IX_SurveyInvites_email ON dbo.SurveyInvites (email);

-- SurveyResults.jsx / LiveDashboard.jsx: SurveyResponse.filter({ survey_id: id }, "-created_date")
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_SurveyResponses_survey' AND object_id = OBJECT_ID(N'dbo.SurveyResponses'))
    CREATE INDEX IX_SurveyResponses_survey ON dbo.SurveyResponses (survey_id, created_date DESC);

-- TakeSurvey.jsx: SurveyResponse.filter({ invite_id: invite.id })
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_SurveyResponses_invite' AND object_id = OBJECT_ID(N'dbo.SurveyResponses'))
    CREATE INDEX IX_SurveyResponses_invite ON dbo.SurveyResponses (invite_id);

-- MailingLists.jsx: MailingList.filter({ created_by: me.email })
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_MailingLists_created_by_date' AND object_id = OBJECT_ID(N'dbo.MailingLists'))
    CREATE INDEX IX_MailingLists_created_by_date ON dbo.MailingLists (created_by, created_date DESC);

-- QuestionLibrary.jsx: QuestionLibrary.filter({ created_by: me.email })
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_QuestionLibrary_created_by_date' AND object_id = OBJECT_ID(N'dbo.QuestionLibrary'))
    CREATE INDEX IX_QuestionLibrary_created_by_date ON dbo.QuestionLibrary (created_by, created_date DESC);

GO

PRINT 'SurveyFlow schema created.';
GO
