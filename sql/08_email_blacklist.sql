-- =============================================================================
-- Per-owner email blacklist. When a respondent opts out, their email goes
-- here keyed by the survey owner. Send-email paths (invite, reminder,
-- thank-you, blind, etc.) check this table before sending so opted-out
-- recipients never get another message from that owner.
--
-- Scope is per-owner-email (a SurveyFlow user's account email), NOT global:
-- if respondent@x.com opts out of admin1's surveys, admin2 can still email
-- them from admin2's surveys.
--
-- Idempotent. Safe to re-run.
-- =============================================================================

USE SurveyFlow;
GO

IF OBJECT_ID(N'dbo.EmailBlacklist', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.EmailBlacklist (
        id                       CHAR(36)        NOT NULL CONSTRAINT PK_EmailBlacklist PRIMARY KEY,
        -- The owning SurveyFlow user (the survey creator). Match by their
        -- account email so the blacklist follows them even if their
        -- display name changes.
        owner_user_email         NVARCHAR(320)   NOT NULL,
        -- The respondent's email being blacklisted.
        email                    NVARCHAR(320)   NOT NULL,
        -- Audit: which survey was the trigger for this opt-out. NULL for
        -- entries added manually via the blacklist management UI.
        opted_out_via_survey_id  CHAR(36)        NULL,
        opted_out_at             DATETIME2(3)    NOT NULL CONSTRAINT DF_EmailBlacklist_opted DEFAULT SYSUTCDATETIME(),
        -- Free-text reason (e.g. "unsubscribed via email link", "GDPR
        -- request"). Optional, mostly for admin record-keeping.
        reason                   NVARCHAR(500)   NULL,
        created_date             DATETIME2(3)    NOT NULL CONSTRAINT DF_EmailBlacklist_created DEFAULT SYSUTCDATETIME(),
        created_by               NVARCHAR(320)   NULL,
        updated_date             DATETIME2(3)    NOT NULL CONSTRAINT DF_EmailBlacklist_updated DEFAULT SYSUTCDATETIME()
    );
END;
GO

-- One blacklist entry per (owner, email). Case-insensitive thanks to the
-- server default collation (SQL_Latin1_General_CP1_CI_AS), so
-- "JOE@x.com" and "joe@x.com" can't both opt out from the same owner.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UQ_EmailBlacklist_owner_email' AND object_id = OBJECT_ID(N'dbo.EmailBlacklist'))
    CREATE UNIQUE INDEX UQ_EmailBlacklist_owner_email ON dbo.EmailBlacklist (owner_user_email, email);
GO

-- Lookup index for the send-email path: "is this (owner, email) on the
-- blacklist?" The unique index above already covers this lookup, but a
-- non-unique secondary on owner-only helps the management page list all
-- of a given admin's entries quickly.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_EmailBlacklist_owner_date' AND object_id = OBJECT_ID(N'dbo.EmailBlacklist'))
    CREATE INDEX IX_EmailBlacklist_owner_date ON dbo.EmailBlacklist (owner_user_email, opted_out_at DESC);
GO

PRINT 'EmailBlacklist table created.';
GO
