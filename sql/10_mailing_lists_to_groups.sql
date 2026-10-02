-- =============================================================================
-- 10 — Convert Mailing Lists into Address Book groups
--
-- Each MailingList becomes a ContactGroup in the same account; each email
-- on it becomes (or is matched to) a Contact and is added to the group.
-- The Mailing Lists page is retired in the UI after this runs; the table
-- stays (read-only history) until a later cleanup script drops it.
--
-- Rules — the same ones the address-book import applies:
--   * Match an existing ACTIVE contact by email (exact, case-insensitive).
--     Email-matching accounts: a missing contact is created (name blank).
--     ID-number-matching accounts: a missing contact can't be created
--     (no ID on a mailing list) — that email is skipped and reported.
--   * Invalid emails and suppressed emails (global list or the account's
--     opt-outs) are skipped and reported.
--   * Duplicate emails within a list count once.
--   * Group name = list name; if an active group already has that name,
--     " (mailing list)" is appended (then a number if still taken).
--
-- Idempotent: each list records the group it became
-- (MailingLists.converted_group_id) and is never converted twice. Lists
-- created after this script (the API still accepts them) are picked up by
-- re-running it.
--
-- Prints a per-list summary. Run in a transaction so a failure changes
-- nothing.
-- =============================================================================

USE SurveyFlow;
GO

IF COL_LENGTH('dbo.MailingLists', 'converted_group_id') IS NULL
    ALTER TABLE dbo.MailingLists ADD converted_group_id CHAR(36) NULL;
GO

SET NOCOUNT ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

DECLARE @Report TABLE (
    list_name NVARCHAR(200), group_name NVARCHAR(200), emails INT,
    matched INT, created INT, skipped_invalid INT, skipped_suppressed INT, skipped_no_id INT
);

DECLARE @list_id CHAR(36), @account_id CHAR(36), @name NVARCHAR(200), @description NVARCHAR(MAX),
        @emails NVARCHAR(MAX), @created_by NVARCHAR(320), @match_mode NVARCHAR(20),
        @group_name NVARCHAR(200), @group_id CHAR(36), @n INT;
DECLARE @E TABLE (email NVARCHAR(320) PRIMARY KEY, status NVARCHAR(20), contact_id CHAR(36));

DECLARE lists CURSOR LOCAL FAST_FORWARD FOR
    SELECT m.id, m.account_id, m.name, m.description, m.emails_json, m.created_by, a.contact_match_key
    FROM dbo.MailingLists m
    JOIN dbo.Accounts a ON a.id = m.account_id
    WHERE m.converted_group_id IS NULL
    ORDER BY m.created_date;

OPEN lists;
FETCH NEXT FROM lists INTO @list_id, @account_id, @name, @description, @emails, @created_by, @match_mode;

WHILE @@FETCH_STATUS = 0
BEGIN
    -- Distinct, trimmed emails from the list.
    DELETE FROM @E;
    INSERT INTO @E (email, status)
    SELECT DISTINCT LOWER(LTRIM(RTRIM(CAST(j.value AS NVARCHAR(320))))), 'pending'
    FROM OPENJSON(ISNULL(@emails, '[]')) j
    WHERE j.type = 1 AND LTRIM(RTRIM(CAST(j.value AS NVARCHAR(320)))) <> '';

    -- Invalid (same shape check the API's regex enforces: x@y.z, no
    -- whitespace). Tabs and line breaks matter: lists were sometimes filled
    -- by pasting whole spreadsheet rows ("first<TAB>last<TAB>email<TAB>title").
    UPDATE @E SET status = 'invalid'
    WHERE email NOT LIKE '%_@_%._%' OR email LIKE '%@%@%'
       OR email LIKE '% %' OR email LIKE '%' + CHAR(9) + '%'
       OR email LIKE '%' + CHAR(10) + '%' OR email LIKE '%' + CHAR(13) + '%';

    -- Suppressed: global list or this account's opt-outs.
    UPDATE e SET status = 'suppressed'
    FROM @E e
    WHERE e.status = 'pending'
      AND (EXISTS (SELECT 1 FROM dbo.EmailSuppression s WHERE s.email = e.email)
           OR EXISTS (SELECT 1 FROM dbo.EmailBlacklist b WHERE b.email = e.email AND b.account_id = @account_id));

    -- Existing active contacts (by email, whatever the account's match mode).
    UPDATE e SET status = 'matched', contact_id = c.id
    FROM @E e
    CROSS APPLY (SELECT TOP 1 id FROM dbo.Contacts c
                 WHERE c.account_id = @account_id AND c.is_active = 1 AND c.email = e.email
                 ORDER BY c.created_date) c
    WHERE e.status = 'pending';

    -- Remaining: create (email accounts) or skip (ID-number accounts).
    IF @match_mode = 'id_number'
        UPDATE @E SET status = 'no_id' WHERE status = 'pending';
    ELSE
    BEGIN
        UPDATE @E SET status = 'created', contact_id = LOWER(CONVERT(CHAR(36), NEWID())) WHERE status = 'pending';

        INSERT INTO dbo.Contacts (id, account_id, email, match_key, created_by)
        SELECT contact_id, @account_id, email, email, ISNULL(@created_by, '10_mailing_lists_to_groups.sql')
        FROM @E WHERE status = 'created';

        INSERT INTO dbo.ContactNotes (id, account_id, contact_id, note_type, note, created_by)
        SELECT LOWER(CONVERT(CHAR(36), NEWID())), @account_id, contact_id, 'system',
               N'Contact created from mailing list "' + @name + N'".', '10_mailing_lists_to_groups.sql'
        FROM @E WHERE status = 'created';
    END;

    -- Group with a free name.
    SET @group_name = LEFT(@name, 200);
    SET @n = 1;
    IF EXISTS (SELECT 1 FROM dbo.ContactGroups WHERE account_id = @account_id AND is_active = 1 AND name = @group_name)
        SET @group_name = LEFT(@name, 185) + N' (mailing list)';
    WHILE EXISTS (SELECT 1 FROM dbo.ContactGroups WHERE account_id = @account_id AND is_active = 1 AND name = @group_name)
    BEGIN
        SET @n += 1;
        SET @group_name = LEFT(@name, 180) + N' (mailing list ' + CAST(@n AS NVARCHAR(10)) + N')';
    END;

    SET @group_id = LOWER(CONVERT(CHAR(36), NEWID()));
    INSERT INTO dbo.ContactGroups (id, account_id, name, description, created_by)
    VALUES (@group_id, @account_id, @group_name, @description, ISNULL(@created_by, '10_mailing_lists_to_groups.sql'));

    INSERT INTO dbo.ContactGroupMembers (group_id, contact_id, added_by)
    SELECT @group_id, contact_id, '10_mailing_lists_to_groups.sql'
    FROM @E WHERE status IN ('matched', 'created');

    UPDATE dbo.MailingLists SET converted_group_id = @group_id, updated_date = SYSUTCDATETIME() WHERE id = @list_id;

    INSERT INTO @Report
    SELECT @name, @group_name, COUNT(*),
           SUM(CASE WHEN status = 'matched' THEN 1 ELSE 0 END),
           SUM(CASE WHEN status = 'created' THEN 1 ELSE 0 END),
           SUM(CASE WHEN status = 'invalid' THEN 1 ELSE 0 END),
           SUM(CASE WHEN status = 'suppressed' THEN 1 ELSE 0 END),
           SUM(CASE WHEN status = 'no_id' THEN 1 ELSE 0 END)
    FROM @E;

    FETCH NEXT FROM lists INTO @list_id, @account_id, @name, @description, @emails, @created_by, @match_mode;
END;

CLOSE lists;
DEALLOCATE lists;

COMMIT TRANSACTION;

IF EXISTS (SELECT 1 FROM @Report)
    SELECT list_name, group_name, emails, matched AS matched_existing, created AS new_contacts,
           skipped_invalid, skipped_suppressed, skipped_no_id
    FROM @Report;
ELSE
    PRINT 'No unconverted mailing lists.';
GO
