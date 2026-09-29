-- =============================================================================
-- 09 — Accounts (tenancy) + Address Book
--
-- Ports the legacy PeriscopeIQ address book (Address_Book, Group_List,
-- Address_Groups, ExtRespondentData*, Address_Book_Notes, BlockList) into
-- SurveyFlow, and introduces Accounts so several users at one client share
-- one address book, surveys, and opt-out list (legacy "Admin" == Account).
--
-- Decisions baked in (2026-09-29):
--   * Ownership is per Account, enforced server-side via account_id.
--   * Company stays a free-text column on Contacts (as legacy).
--   * Suppression is two-tier: global EmailSuppression (bounces, "never email
--     me", legacy BlockList) + per-account EmailBlacklist (opt-outs).
--   * Each account picks its contact match key: 'email' or 'id_number'.
--     The API writes Contacts.match_key accordingly; the unique index below
--     enforces it. (Legacy applied ID-centric rules only on manual add and
--     ignored them on Excel upload — we apply them everywhere.)
--   * Custom fields are defined once per account (legacy: per survey) and
--     stored on the contact in custom_json. Invites snapshot the values at
--     send time so historical reports don't shift when a contact is edited.
--
-- NON-BREAKING: every new account_id column is NULLable and nothing the
-- current API writes is required. A later script (after the API sets
-- account_id on every insert) tightens account_id to NOT NULL.
--
-- Idempotent. Safe to re-run.
-- =============================================================================

USE SurveyFlow;
GO


-- -----------------------------------------------------------------------------
-- 1. Accounts
--    legacy_admin_id maps to PeriscopeIQ.dbo.Admins.Admin_ID for migration.
--    settings_json holds low-stakes toggles (e.g. show_company) so they don't
--    each need a migration.
-- -----------------------------------------------------------------------------
IF OBJECT_ID(N'dbo.Accounts', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.Accounts (
        id                  CHAR(36)        NOT NULL CONSTRAINT PK_Accounts PRIMARY KEY,
        name                NVARCHAR(200)   NOT NULL,
        slug                NVARCHAR(100)   NOT NULL,
        contact_match_key   NVARCHAR(20)    NOT NULL CONSTRAINT DF_Accounts_match_key DEFAULT 'email',
        is_active           BIT             NOT NULL CONSTRAINT DF_Accounts_is_active DEFAULT 1,
        settings_json       NVARCHAR(MAX)   NULL,
        legacy_admin_id     INT             NULL,
        created_date        DATETIME2(3)    NOT NULL CONSTRAINT DF_Accounts_created DEFAULT SYSUTCDATETIME(),
        created_by          NVARCHAR(320)   NULL,
        updated_date        DATETIME2(3)    NOT NULL CONSTRAINT DF_Accounts_updated DEFAULT SYSUTCDATETIME(),
        CONSTRAINT UQ_Accounts_slug UNIQUE (slug),
        CONSTRAINT CK_Accounts_match_key CHECK (contact_match_key IN ('email', 'id_number')),
        CONSTRAINT CK_Accounts_settings  CHECK (settings_json IS NULL OR ISJSON(settings_json) = 1)
    );
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UQ_Accounts_legacy_admin' AND object_id = OBJECT_ID(N'dbo.Accounts'))
    CREATE UNIQUE INDEX UQ_Accounts_legacy_admin ON dbo.Accounts (legacy_admin_id) WHERE legacy_admin_id IS NOT NULL;
GO


-- -----------------------------------------------------------------------------
-- 2. account_id on existing tenant-owned tables (NULLable for now)
--    Invites/Responses carry it too (denormalized from the survey) so the
--    generic JsonRepository can scope every entity the same way.
-- -----------------------------------------------------------------------------
IF COL_LENGTH('dbo.Users',           'account_id') IS NULL ALTER TABLE dbo.Users           ADD account_id CHAR(36) NULL;
IF COL_LENGTH('dbo.Surveys',         'account_id') IS NULL ALTER TABLE dbo.Surveys         ADD account_id CHAR(36) NULL;
IF COL_LENGTH('dbo.SurveyInvites',   'account_id') IS NULL ALTER TABLE dbo.SurveyInvites   ADD account_id CHAR(36) NULL;
IF COL_LENGTH('dbo.SurveyResponses', 'account_id') IS NULL ALTER TABLE dbo.SurveyResponses ADD account_id CHAR(36) NULL;
IF COL_LENGTH('dbo.MailingLists',    'account_id') IS NULL ALTER TABLE dbo.MailingLists    ADD account_id CHAR(36) NULL;
IF COL_LENGTH('dbo.QuestionLibrary', 'account_id') IS NULL ALTER TABLE dbo.QuestionLibrary ADD account_id CHAR(36) NULL;
IF COL_LENGTH('dbo.EmailBlacklist',  'account_id') IS NULL ALTER TABLE dbo.EmailBlacklist  ADD account_id CHAR(36) NULL;
IF COL_LENGTH('dbo.AiUsage',         'account_id') IS NULL ALTER TABLE dbo.AiUsage         ADD account_id CHAR(36) NULL;

-- PeriscopeIQ staff who can see/switch between all accounts. Users.role
-- ('admin'/'user') becomes the role WITHIN the user's account.
IF COL_LENGTH('dbo.Users', 'is_system_admin') IS NULL
    ALTER TABLE dbo.Users ADD is_system_admin BIT NOT NULL CONSTRAINT DF_Users_is_system_admin DEFAULT 0;
GO

IF OBJECT_ID(N'FK_Users_Account')           IS NULL ALTER TABLE dbo.Users           ADD CONSTRAINT FK_Users_Account           FOREIGN KEY (account_id) REFERENCES dbo.Accounts(id);
IF OBJECT_ID(N'FK_Surveys_Account')         IS NULL ALTER TABLE dbo.Surveys         ADD CONSTRAINT FK_Surveys_Account         FOREIGN KEY (account_id) REFERENCES dbo.Accounts(id);
IF OBJECT_ID(N'FK_SurveyInvites_Account')   IS NULL ALTER TABLE dbo.SurveyInvites   ADD CONSTRAINT FK_SurveyInvites_Account   FOREIGN KEY (account_id) REFERENCES dbo.Accounts(id);
IF OBJECT_ID(N'FK_SurveyResponses_Account') IS NULL ALTER TABLE dbo.SurveyResponses ADD CONSTRAINT FK_SurveyResponses_Account FOREIGN KEY (account_id) REFERENCES dbo.Accounts(id);
IF OBJECT_ID(N'FK_MailingLists_Account')    IS NULL ALTER TABLE dbo.MailingLists    ADD CONSTRAINT FK_MailingLists_Account    FOREIGN KEY (account_id) REFERENCES dbo.Accounts(id);
IF OBJECT_ID(N'FK_QuestionLibrary_Account') IS NULL ALTER TABLE dbo.QuestionLibrary ADD CONSTRAINT FK_QuestionLibrary_Account FOREIGN KEY (account_id) REFERENCES dbo.Accounts(id);
IF OBJECT_ID(N'FK_EmailBlacklist_Account')  IS NULL ALTER TABLE dbo.EmailBlacklist  ADD CONSTRAINT FK_EmailBlacklist_Account  FOREIGN KEY (account_id) REFERENCES dbo.Accounts(id);
IF OBJECT_ID(N'FK_AiUsage_Account')         IS NULL ALTER TABLE dbo.AiUsage         ADD CONSTRAINT FK_AiUsage_Account         FOREIGN KEY (account_id) REFERENCES dbo.Accounts(id);
GO

-- Replaces the created_by-based dashboard index once the API scopes by account.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Surveys_account_date' AND object_id = OBJECT_ID(N'dbo.Surveys'))
    CREATE INDEX IX_Surveys_account_date ON dbo.Surveys (account_id, created_date DESC);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Users_account' AND object_id = OBJECT_ID(N'dbo.Users'))
    CREATE INDEX IX_Users_account ON dbo.Users (account_id);
GO


-- -----------------------------------------------------------------------------
-- 3. Backfill: one default account owns everything that exists today
--    (SurveyFlow is pre-launch; all current users are PeriscopeIQ staff).
--    Existing role='admin' users become system admins.
-- -----------------------------------------------------------------------------
IF NOT EXISTS (SELECT 1 FROM dbo.Accounts WHERE slug = N'periscopeiq')
    INSERT INTO dbo.Accounts (id, name, slug, created_by)
    VALUES (LOWER(CONVERT(CHAR(36), NEWID())), N'PeriscopeIQ', N'periscopeiq', N'09_accounts_address_book.sql');
GO

DECLARE @default CHAR(36) = (SELECT id FROM dbo.Accounts WHERE slug = N'periscopeiq');

UPDATE dbo.Users SET account_id = @default WHERE account_id IS NULL;
UPDATE dbo.Users SET is_system_admin = 1 WHERE role = 'admin' AND is_system_admin = 0;

UPDATE s SET account_id = COALESCE(u.account_id, @default)
FROM dbo.Surveys s LEFT JOIN dbo.Users u ON u.email = s.created_by
WHERE s.account_id IS NULL;

UPDATE i SET account_id = s.account_id
FROM dbo.SurveyInvites i JOIN dbo.Surveys s ON s.id = i.survey_id
WHERE i.account_id IS NULL;

UPDATE r SET account_id = s.account_id
FROM dbo.SurveyResponses r JOIN dbo.Surveys s ON s.id = r.survey_id
WHERE r.account_id IS NULL;

UPDATE m SET account_id = COALESCE(u.account_id, @default)
FROM dbo.MailingLists m LEFT JOIN dbo.Users u ON u.email = m.created_by
WHERE m.account_id IS NULL;

UPDATE q SET account_id = COALESCE(u.account_id, @default)
FROM dbo.QuestionLibrary q LEFT JOIN dbo.Users u ON u.email = q.created_by
WHERE q.account_id IS NULL;

UPDATE b SET account_id = COALESCE(u.account_id, @default)
FROM dbo.EmailBlacklist b LEFT JOIN dbo.Users u ON u.email = b.owner_user_email
WHERE b.account_id IS NULL;

UPDATE a SET account_id = u.account_id
FROM dbo.AiUsage a JOIN dbo.Users u ON u.email = a.user_email
WHERE a.account_id IS NULL;
GO


-- -----------------------------------------------------------------------------
-- 4. EmailBlacklist becomes per-account opt-outs.
--    Two users in one account may have opted out the same email separately;
--    keep the earliest before enforcing (account_id, email) uniqueness.
--    owner_user_email stays for now (current API reads it); drop it in the
--    tightening script.
-- -----------------------------------------------------------------------------
;WITH d AS (
    SELECT id, ROW_NUMBER() OVER (PARTITION BY account_id, email ORDER BY opted_out_at, id) AS rn
    FROM dbo.EmailBlacklist
    WHERE account_id IS NOT NULL
)
DELETE FROM d WHERE rn > 1
  AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UQ_EmailBlacklist_account_email' AND object_id = OBJECT_ID(N'dbo.EmailBlacklist'));
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UQ_EmailBlacklist_account_email' AND object_id = OBJECT_ID(N'dbo.EmailBlacklist'))
    CREATE UNIQUE INDEX UQ_EmailBlacklist_account_email ON dbo.EmailBlacklist (account_id, email) WHERE account_id IS NOT NULL;
GO


-- -----------------------------------------------------------------------------
-- 5. EmailSuppression — GLOBAL do-not-send list (not account-owned).
--    Checked by every send path in addition to EmailBlacklist. Fed by
--    Resend bounce/complaint webhooks, "never email me again" links, and the
--    legacy BlockList migration.
-- -----------------------------------------------------------------------------
IF OBJECT_ID(N'dbo.EmailSuppression', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.EmailSuppression (
        id              CHAR(36)        NOT NULL CONSTRAINT PK_EmailSuppression PRIMARY KEY,
        email           NVARCHAR(320)   NOT NULL,
        reason          NVARCHAR(30)    NOT NULL,
        detail          NVARCHAR(500)   NULL,
        suppressed_at   DATETIME2(3)    NOT NULL CONSTRAINT DF_EmailSuppression_at      DEFAULT SYSUTCDATETIME(),
        created_date    DATETIME2(3)    NOT NULL CONSTRAINT DF_EmailSuppression_created DEFAULT SYSUTCDATETIME(),
        created_by      NVARCHAR(320)   NULL,
        updated_date    DATETIME2(3)    NOT NULL CONSTRAINT DF_EmailSuppression_updated DEFAULT SYSUTCDATETIME(),
        CONSTRAINT UQ_EmailSuppression_email UNIQUE (email),
        CONSTRAINT CK_EmailSuppression_reason
            CHECK (reason IN ('hard_bounce', 'complaint', 'unsubscribe_all', 'manual', 'legacy'))
    );
END;
GO


-- -----------------------------------------------------------------------------
-- 6. Contacts — the address book (legacy Address_Book)
--
--    email is NULLable: paper/no-email respondents no longer need fake
--    "@piq.com" / "@piqemail.com" addresses. A contact needs an email or an
--    id_number.
--
--    match_key is maintained by the API: LOWER(TRIM(email)) for accounts
--    matching on email, TRIM(id_number) for accounts matching on ID number.
--    The filtered unique index makes it impossible to have two ACTIVE
--    contacts with the same key in one account (legacy only checked in code,
--    with LIKE, so '_' in an email caused false duplicates). Switching an
--    account's match mode = recompute match_key; duplicates surface as an
--    error instead of silently merging.
--
--    is_active=0 is legacy "Deactivated" (soft delete). is_hidden is legacy
--    "Hidden" — carried over as-is until we confirm what it's for.
--
--    custom_json: { "<field_key>": value, ... } — see ContactFields.
-- -----------------------------------------------------------------------------
IF OBJECT_ID(N'dbo.Contacts', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.Contacts (
        id                  CHAR(36)        NOT NULL CONSTRAINT PK_Contacts PRIMARY KEY,
        account_id          CHAR(36)        NOT NULL,
        first_name          NVARCHAR(100)   NULL,
        last_name           NVARCHAR(100)   NULL,
        email               NVARCHAR(320)   NULL,
        id_number           NVARCHAR(100)   NULL,
        company             NVARCHAR(200)   NULL,
        custom_json         NVARCHAR(MAX)   NULL,
        match_key           NVARCHAR(320)   NOT NULL,
        is_active           BIT             NOT NULL CONSTRAINT DF_Contacts_is_active DEFAULT 1,
        is_hidden           BIT             NOT NULL CONSTRAINT DF_Contacts_is_hidden DEFAULT 0,
        deactivated_at      DATETIME2(3)    NULL,
        legacy_address_id   INT             NULL,
        created_date        DATETIME2(3)    NOT NULL CONSTRAINT DF_Contacts_created DEFAULT SYSUTCDATETIME(),
        created_by          NVARCHAR(320)   NULL,
        updated_date        DATETIME2(3)    NOT NULL CONSTRAINT DF_Contacts_updated DEFAULT SYSUTCDATETIME(),
        CONSTRAINT FK_Contacts_Account FOREIGN KEY (account_id) REFERENCES dbo.Accounts(id),
        CONSTRAINT CK_Contacts_email_or_id
            CHECK (NULLIF(LTRIM(RTRIM(email)), '') IS NOT NULL OR NULLIF(LTRIM(RTRIM(id_number)), '') IS NOT NULL),
        CONSTRAINT CK_Contacts_custom CHECK (custom_json IS NULL OR ISJSON(custom_json) = 1)
    );
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UQ_Contacts_account_match_active' AND object_id = OBJECT_ID(N'dbo.Contacts'))
    CREATE UNIQUE INDEX UQ_Contacts_account_match_active ON dbo.Contacts (account_id, match_key) WHERE is_active = 1;
-- Address book grid: default sort last/first within account.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Contacts_account_name' AND object_id = OBJECT_ID(N'dbo.Contacts'))
    CREATE INDEX IX_Contacts_account_name ON dbo.Contacts (account_id, is_active, last_name, first_name);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Contacts_account_email' AND object_id = OBJECT_ID(N'dbo.Contacts'))
    CREATE INDEX IX_Contacts_account_email ON dbo.Contacts (account_id, email);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Contacts_account_company' AND object_id = OBJECT_ID(N'dbo.Contacts'))
    CREATE INDEX IX_Contacts_account_company ON dbo.Contacts (account_id, company);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UQ_Contacts_legacy' AND object_id = OBJECT_ID(N'dbo.Contacts'))
    CREATE UNIQUE INDEX UQ_Contacts_legacy ON dbo.Contacts (legacy_address_id) WHERE legacy_address_id IS NOT NULL;
GO


-- -----------------------------------------------------------------------------
-- 7. ContactGroups (legacy Group_List) + members (legacy Address_Groups)
--    Real keys (legacy computed Group_ID as max+1). Names unique per account
--    among active groups. Membership edits are add/remove deltas in the API
--    (legacy "save" wiped the group and re-added only the visible page).
-- -----------------------------------------------------------------------------
IF OBJECT_ID(N'dbo.ContactGroups', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.ContactGroups (
        id                      CHAR(36)        NOT NULL CONSTRAINT PK_ContactGroups PRIMARY KEY,
        account_id              CHAR(36)        NOT NULL,
        name                    NVARCHAR(200)   NOT NULL,
        description             NVARCHAR(MAX)   NULL,
        is_panel                BIT             NOT NULL CONSTRAINT DF_ContactGroups_is_panel  DEFAULT 0,
        is_active               BIT             NOT NULL CONSTRAINT DF_ContactGroups_is_active DEFAULT 1,
        copied_from_group_id    CHAR(36)        NULL,
        legacy_group_id         INT             NULL,
        created_date            DATETIME2(3)    NOT NULL CONSTRAINT DF_ContactGroups_created DEFAULT SYSUTCDATETIME(),
        created_by              NVARCHAR(320)   NULL,
        updated_date            DATETIME2(3)    NOT NULL CONSTRAINT DF_ContactGroups_updated DEFAULT SYSUTCDATETIME(),
        CONSTRAINT FK_ContactGroups_Account FOREIGN KEY (account_id) REFERENCES dbo.Accounts(id)
    );
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UQ_ContactGroups_account_name_active' AND object_id = OBJECT_ID(N'dbo.ContactGroups'))
    CREATE UNIQUE INDEX UQ_ContactGroups_account_name_active ON dbo.ContactGroups (account_id, name) WHERE is_active = 1;
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UQ_ContactGroups_legacy' AND object_id = OBJECT_ID(N'dbo.ContactGroups'))
    CREATE UNIQUE INDEX UQ_ContactGroups_legacy ON dbo.ContactGroups (legacy_group_id) WHERE legacy_group_id IS NOT NULL;
GO

IF OBJECT_ID(N'dbo.ContactGroupMembers', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.ContactGroupMembers (
        group_id        CHAR(36)        NOT NULL,
        contact_id      CHAR(36)        NOT NULL,
        added_date      DATETIME2(3)    NOT NULL CONSTRAINT DF_ContactGroupMembers_added DEFAULT SYSUTCDATETIME(),
        added_by        NVARCHAR(320)   NULL,
        CONSTRAINT PK_ContactGroupMembers PRIMARY KEY (group_id, contact_id),
        CONSTRAINT FK_ContactGroupMembers_Group
            FOREIGN KEY (group_id)   REFERENCES dbo.ContactGroups(id) ON DELETE CASCADE,
        CONSTRAINT FK_ContactGroupMembers_Contact
            FOREIGN KEY (contact_id) REFERENCES dbo.Contacts(id)      ON DELETE CASCADE
    );
END;
GO

-- "Which groups is this contact in?"
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_ContactGroupMembers_contact' AND object_id = OBJECT_ID(N'dbo.ContactGroupMembers'))
    CREATE INDEX IX_ContactGroupMembers_contact ON dbo.ContactGroupMembers (contact_id);
GO

-- Groups offered when distributing a given survey (legacy Group_List_Class).
-- No rows for a survey = all groups offered.
IF OBJECT_ID(N'dbo.ContactGroupSurveys', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.ContactGroupSurveys (
        group_id        CHAR(36)        NOT NULL,
        survey_id       CHAR(36)        NOT NULL,
        CONSTRAINT PK_ContactGroupSurveys PRIMARY KEY (group_id, survey_id),
        CONSTRAINT FK_ContactGroupSurveys_Group
            FOREIGN KEY (group_id)  REFERENCES dbo.ContactGroups(id) ON DELETE CASCADE,
        CONSTRAINT FK_ContactGroupSurveys_Survey
            FOREIGN KEY (survey_id) REFERENCES dbo.Surveys(id)       ON DELETE CASCADE
    );
END;
GO


-- -----------------------------------------------------------------------------
-- 8. ContactFields — custom/demographic field definitions
--    (legacy ExtRespondentDataFields). field_key is the stable key used in
--    Contacts.custom_json, invite snapshots, and merge tags {{custom.<key>}}.
--    survey_ids_json: NULL = applies to all surveys, else JSON array of
--    survey ids (legacy ExtRespondentDataClasses).
-- -----------------------------------------------------------------------------
IF OBJECT_ID(N'dbo.ContactFields', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.ContactFields (
        id                  CHAR(36)        NOT NULL CONSTRAINT PK_ContactFields PRIMARY KEY,
        account_id          CHAR(36)        NOT NULL,
        field_key           NVARCHAR(100)   NOT NULL,
        label               NVARCHAR(200)   NOT NULL,
        field_type          NVARCHAR(20)    NOT NULL CONSTRAINT DF_ContactFields_type DEFAULT 'text',
        options_json        NVARCHAR(MAX)   NULL,
        survey_ids_json     NVARCHAR(MAX)   NULL,
        sort_order          INT             NOT NULL CONSTRAINT DF_ContactFields_sort DEFAULT 0,
        is_active           BIT             NOT NULL CONSTRAINT DF_ContactFields_is_active DEFAULT 1,
        legacy_field_id     INT             NULL,
        created_date        DATETIME2(3)    NOT NULL CONSTRAINT DF_ContactFields_created DEFAULT SYSUTCDATETIME(),
        created_by          NVARCHAR(320)   NULL,
        updated_date        DATETIME2(3)    NOT NULL CONSTRAINT DF_ContactFields_updated DEFAULT SYSUTCDATETIME(),
        CONSTRAINT FK_ContactFields_Account FOREIGN KEY (account_id) REFERENCES dbo.Accounts(id),
        CONSTRAINT UQ_ContactFields_account_key UNIQUE (account_id, field_key),
        CONSTRAINT CK_ContactFields_type    CHECK (field_type IN ('text', 'number', 'date', 'choice')),
        CONSTRAINT CK_ContactFields_options CHECK (options_json    IS NULL OR ISJSON(options_json)    = 1),
        CONSTRAINT CK_ContactFields_surveys CHECK (survey_ids_json IS NULL OR ISJSON(survey_ids_json) = 1)
    );
END;
GO


-- -----------------------------------------------------------------------------
-- 9. ContactNotes (legacy Address_Book_Notes)
--    note_type 'system' = automatic history ("Created via import", "Email
--    changed from x to y"), replacing the legacy proc-written audit notes.
-- -----------------------------------------------------------------------------
IF OBJECT_ID(N'dbo.ContactNotes', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.ContactNotes (
        id              CHAR(36)        NOT NULL CONSTRAINT PK_ContactNotes PRIMARY KEY,
        account_id      CHAR(36)        NOT NULL,
        contact_id      CHAR(36)        NOT NULL,
        survey_id       CHAR(36)        NULL,
        note_type       NVARCHAR(20)    NOT NULL CONSTRAINT DF_ContactNotes_type DEFAULT 'note',
        note            NVARCHAR(MAX)   NOT NULL,
        created_date    DATETIME2(3)    NOT NULL CONSTRAINT DF_ContactNotes_created DEFAULT SYSUTCDATETIME(),
        created_by      NVARCHAR(320)   NULL,
        updated_date    DATETIME2(3)    NOT NULL CONSTRAINT DF_ContactNotes_updated DEFAULT SYSUTCDATETIME(),
        CONSTRAINT FK_ContactNotes_Account FOREIGN KEY (account_id) REFERENCES dbo.Accounts(id),
        CONSTRAINT FK_ContactNotes_Contact FOREIGN KEY (contact_id) REFERENCES dbo.Contacts(id) ON DELETE CASCADE,
        CONSTRAINT FK_ContactNotes_Survey  FOREIGN KEY (survey_id)  REFERENCES dbo.Surveys(id)  ON DELETE SET NULL,
        CONSTRAINT CK_ContactNotes_type CHECK (note_type IN ('note', 'system'))
    );
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_ContactNotes_contact' AND object_id = OBJECT_ID(N'dbo.ContactNotes'))
    CREATE INDEX IX_ContactNotes_contact ON dbo.ContactNotes (contact_id, created_date DESC);
GO


-- -----------------------------------------------------------------------------
-- 10. SurveyInvites ↔ Contacts
--
--   contact_id       — the address-book contact this invite was sent to.
--                      NULL for ad-hoc invites (typed-in emails, duplicates).
--   custom_json      — snapshot of the contact's custom field values at
--                      invite time (first_name/last_name/company/custom_id
--                      already act as the snapshot for the built-ins;
--                      custom_id carries the contact's id_number).
--   reminder_count / last_reminder_at — replaces legacy's four fixed
--                      DateRemSent..DateFourthRemSent columns.
--
--   Uniqueness moves from (survey, email) to (survey, contact): legacy
--   ID-centric accounts allow two contacts to share an email, and legacy's
--   invite proc created a DUPLICATE invite when a contact's email changed.
--   (survey, email) stays unique for ad-hoc invites only.
--
--   email becomes NULLable so no-email contacts can get "deploy without
--   email" links. The two email indexes must be dropped to alter the column.
-- -----------------------------------------------------------------------------
IF COL_LENGTH('dbo.SurveyInvites', 'contact_id')       IS NULL ALTER TABLE dbo.SurveyInvites ADD contact_id       CHAR(36)      NULL;
IF COL_LENGTH('dbo.SurveyInvites', 'custom_json')      IS NULL ALTER TABLE dbo.SurveyInvites ADD custom_json      NVARCHAR(MAX) NULL;
IF COL_LENGTH('dbo.SurveyInvites', 'reminder_count')   IS NULL ALTER TABLE dbo.SurveyInvites ADD reminder_count   INT NOT NULL CONSTRAINT DF_SurveyInvites_reminder_count DEFAULT 0;
IF COL_LENGTH('dbo.SurveyInvites', 'last_reminder_at') IS NULL ALTER TABLE dbo.SurveyInvites ADD last_reminder_at DATETIME2(3)  NULL;
GO

IF OBJECT_ID(N'FK_SurveyInvites_Contact') IS NULL
    ALTER TABLE dbo.SurveyInvites ADD CONSTRAINT FK_SurveyInvites_Contact
        FOREIGN KEY (contact_id) REFERENCES dbo.Contacts(id) ON DELETE SET NULL;
IF OBJECT_ID(N'CK_SurveyInvites_custom') IS NULL
    ALTER TABLE dbo.SurveyInvites ADD CONSTRAINT CK_SurveyInvites_custom
        CHECK (custom_json IS NULL OR ISJSON(custom_json) = 1);
GO

IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.SurveyInvites') AND name = N'email' AND is_nullable = 0)
BEGIN
    IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UQ_SurveyInvites_survey_email' AND object_id = OBJECT_ID(N'dbo.SurveyInvites'))
        DROP INDEX UQ_SurveyInvites_survey_email ON dbo.SurveyInvites;
    IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_SurveyInvites_email' AND object_id = OBJECT_ID(N'dbo.SurveyInvites'))
        DROP INDEX IX_SurveyInvites_email ON dbo.SurveyInvites;

    ALTER TABLE dbo.SurveyInvites ALTER COLUMN email NVARCHAR(320) NULL;
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_SurveyInvites_email' AND object_id = OBJECT_ID(N'dbo.SurveyInvites'))
    CREATE INDEX IX_SurveyInvites_email ON dbo.SurveyInvites (email);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UQ_SurveyInvites_survey_contact' AND object_id = OBJECT_ID(N'dbo.SurveyInvites'))
    CREATE UNIQUE INDEX UQ_SurveyInvites_survey_contact ON dbo.SurveyInvites (survey_id, contact_id) WHERE contact_id IS NOT NULL;
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UQ_SurveyInvites_survey_email_adhoc' AND object_id = OBJECT_ID(N'dbo.SurveyInvites'))
    CREATE UNIQUE INDEX UQ_SurveyInvites_survey_email_adhoc ON dbo.SurveyInvites (survey_id, email) WHERE contact_id IS NULL AND email IS NOT NULL;
-- "Every survey this contact was invited to" (contact detail page).
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_SurveyInvites_contact' AND object_id = OBJECT_ID(N'dbo.SurveyInvites'))
    CREATE INDEX IX_SurveyInvites_contact ON dbo.SurveyInvites (contact_id) WHERE contact_id IS NOT NULL;
GO

PRINT 'Accounts + address book schema applied.';
GO
