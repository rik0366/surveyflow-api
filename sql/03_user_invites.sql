-- =============================================================================
-- Adds invite-flow columns to Users:
--   invite_token, invite_token_expires — populated by /api/auth/invite-user,
--   cleared by /api/auth/set-password.
--
-- Also relaxes password_hash to nullable: an invited user has no password
-- until they accept the invite via the /set-password page.
-- =============================================================================

USE SurveyFlow;
GO

-- Make password_hash nullable (so freshly invited users can exist before
-- they choose a password).
IF EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID(N'dbo.Users') AND name = N'password_hash' AND is_nullable = 0
)
BEGIN
    ALTER TABLE dbo.Users ALTER COLUMN password_hash NVARCHAR(255) NULL;
END;
GO

IF COL_LENGTH('dbo.Users', 'invite_token') IS NULL
BEGIN
    ALTER TABLE dbo.Users ADD invite_token NVARCHAR(100) NULL;
END;
GO

IF COL_LENGTH('dbo.Users', 'invite_token_expires') IS NULL
BEGIN
    ALTER TABLE dbo.Users ADD invite_token_expires DATETIME2(3) NULL;
END;
GO

-- Filtered index — most users have no token, so the index stays tiny.
IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE name = N'IX_Users_invite_token' AND object_id = OBJECT_ID(N'dbo.Users')
)
BEGIN
    CREATE INDEX IX_Users_invite_token ON dbo.Users (invite_token)
        WHERE invite_token IS NOT NULL;
END;
GO

PRINT 'Users invite columns added.';
GO
