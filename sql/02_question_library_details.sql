-- =============================================================================
-- Add a catch-all JSON column to QuestionLibrary so the full question
-- definition (including type-specific fields like conjoint_attributes,
-- maxdiff_sets, image_options, etc.) round-trips losslessly through the API.
-- =============================================================================

USE SurveyFlow;
GO

IF COL_LENGTH('dbo.QuestionLibrary', 'details_json') IS NULL
BEGIN
    ALTER TABLE dbo.QuestionLibrary ADD details_json NVARCHAR(MAX) NULL;
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_QL_details')
BEGIN
    ALTER TABLE dbo.QuestionLibrary
        ADD CONSTRAINT CK_QL_details
        CHECK (details_json IS NULL OR ISJSON(details_json) = 1);
END;
GO

PRINT 'QuestionLibrary.details_json added.';
GO
