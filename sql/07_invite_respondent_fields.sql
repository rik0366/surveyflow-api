-- =============================================================================
-- Expand SurveyInvites with richer respondent metadata for the upcoming
-- History report. New columns:
--
--   respondent_id  BIGINT  — server-issued unique number (e.g. 9000001+).
--                            Populated by a SEQUENCE so JsonRepository
--                            doesn't need to know anything special — the
--                            DEFAULT fires whenever the INSERT omits the
--                            column, exactly like IDENTITY would.
--   first_name, last_name        — respondent personal name
--   custom_id                    — client-defined identifier (free text)
--   company                      — respondent's organization (free text)
--   password                     — 6-char readable code, server-generated
--                                  by the API at invite-creation time
--   opened_at, completed_at      — timestamps stamped by TakeSurvey when
--                                  the respondent first opens / submits
--
-- Idempotent. Safe to run on existing data.
-- =============================================================================

USE SurveyFlow;
GO

-- Sequence backing respondent_id. Start at a 7-digit number so respondent
-- IDs always look uniform (matches the legacy product's appearance).
IF NOT EXISTS (SELECT 1 FROM sys.sequences WHERE name = N'seq_respondent' AND schema_id = SCHEMA_ID(N'dbo'))
BEGIN
    CREATE SEQUENCE dbo.seq_respondent
        AS BIGINT
        START WITH 9000001
        INCREMENT BY 1
        NO CYCLE
        CACHE 50;
END;
GO

-- Add the new columns. Each guarded so the script is re-runnable.
IF COL_LENGTH('dbo.SurveyInvites', 'respondent_id') IS NULL
BEGIN
    ALTER TABLE dbo.SurveyInvites
        ADD respondent_id BIGINT NULL
            CONSTRAINT DF_SurveyInvites_respondent_id DEFAULT (NEXT VALUE FOR dbo.seq_respondent);
END;
GO

IF COL_LENGTH('dbo.SurveyInvites', 'first_name')  IS NULL ALTER TABLE dbo.SurveyInvites ADD first_name  NVARCHAR(100)  NULL;
IF COL_LENGTH('dbo.SurveyInvites', 'last_name')   IS NULL ALTER TABLE dbo.SurveyInvites ADD last_name   NVARCHAR(100)  NULL;
IF COL_LENGTH('dbo.SurveyInvites', 'custom_id')   IS NULL ALTER TABLE dbo.SurveyInvites ADD custom_id   NVARCHAR(100)  NULL;
IF COL_LENGTH('dbo.SurveyInvites', 'company')     IS NULL ALTER TABLE dbo.SurveyInvites ADD company     NVARCHAR(200)  NULL;
IF COL_LENGTH('dbo.SurveyInvites', 'password')    IS NULL ALTER TABLE dbo.SurveyInvites ADD [password]  NVARCHAR(20)   NULL;
IF COL_LENGTH('dbo.SurveyInvites', 'opened_at')   IS NULL ALTER TABLE dbo.SurveyInvites ADD opened_at   DATETIME2(3)   NULL;
IF COL_LENGTH('dbo.SurveyInvites', 'completed_at') IS NULL ALTER TABLE dbo.SurveyInvites ADD completed_at DATETIME2(3) NULL;
GO

-- Backfill respondent_id for existing rows (DEFAULT only fires on new rows).
UPDATE dbo.SurveyInvites
SET respondent_id = NEXT VALUE FOR dbo.seq_respondent
WHERE respondent_id IS NULL;
GO

-- Now that all rows have a value, tighten to NOT NULL + UNIQUE.
ALTER TABLE dbo.SurveyInvites ALTER COLUMN respondent_id BIGINT NOT NULL;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UQ_SurveyInvites_respondent_id' AND object_id = OBJECT_ID(N'dbo.SurveyInvites'))
    CREATE UNIQUE INDEX UQ_SurveyInvites_respondent_id ON dbo.SurveyInvites (respondent_id);
GO

-- One row per (survey, email). The React UI already dedupes on import, but
-- enforcing it at the DB level closes the door on concurrent imports and
-- direct API calls. Case-insensitive via the server default collation
-- (SQL_Latin1_General_CP1_CI_AS), matching how the UI compares today.
--
-- If this CREATE fails with "duplicate key", run the diagnostic:
--   SELECT survey_id, email, COUNT(*) FROM dbo.SurveyInvites
--   GROUP BY survey_id, email HAVING COUNT(*) > 1;
-- ...and dedupe before re-running this script.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UQ_SurveyInvites_survey_email' AND object_id = OBJECT_ID(N'dbo.SurveyInvites'))
    CREATE UNIQUE INDEX UQ_SurveyInvites_survey_email ON dbo.SurveyInvites (survey_id, email);
GO

PRINT 'SurveyInvites respondent fields added.';
GO
