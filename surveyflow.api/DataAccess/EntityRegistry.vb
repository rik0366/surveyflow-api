Imports System.Data

' Descriptors for all SurveyFlow entities. Each descriptor declares which
' DB columns are scalar vs. JSON, and which columns the API will accept in
' filter/sort query strings.
'
' Add new entities here when they're introduced.
Public Module EntityRegistry

    Public ReadOnly MailingList As EntityDescriptor = BuildMailingList()
    Public ReadOnly Survey As EntityDescriptor = BuildSurvey()
    Public ReadOnly SurveyInvite As EntityDescriptor = BuildSurveyInvite()
    Public ReadOnly SurveyResponse As EntityDescriptor = BuildSurveyResponse()
    Public ReadOnly QuestionLibrary As EntityDescriptor = BuildQuestionLibrary()
    Public ReadOnly User As EntityDescriptor = BuildUser()
    Public ReadOnly EmailBlacklist As EntityDescriptor = BuildEmailBlacklist()


    ' password_hash is intentionally omitted — JsonRepository never reads or
    ' writes it, which means it can't leak through generic /api/User endpoints.
    ' AuthController handles password operations directly.
    Private Function BuildUser() As EntityDescriptor
        Dim d As New EntityDescriptor() With {.TableName = "Users"}
        d.ScalarColumns.Add(New ScalarColumn With {.ColumnName = "email",     .JsonName = "email",     .DbType = SqlDbType.NVarChar, .MaxLength = 320})
        d.ScalarColumns.Add(New ScalarColumn With {.ColumnName = "full_name", .JsonName = "full_name", .DbType = SqlDbType.NVarChar, .MaxLength = 200})
        d.ScalarColumns.Add(New ScalarColumn With {.ColumnName = "role",      .JsonName = "role",      .DbType = SqlDbType.NVarChar, .MaxLength = 20})
        d.ScalarColumns.Add(New ScalarColumn With {.ColumnName = "is_active", .JsonName = "is_active", .DbType = SqlDbType.Bit})
        d.FilterableColumns.UnionWith({"id", "email", "role", "is_active"})
        d.SortableColumns.UnionWith({"created_date", "updated_date", "email", "role"})
        d.RequireCreatedBy = False
        Return d
    End Function


    Private Function BuildMailingList() As EntityDescriptor
        Dim d As New EntityDescriptor() With {.TableName = "MailingLists"}
        d.ScalarColumns.Add(New ScalarColumn With {.ColumnName = "name",        .JsonName = "name",        .DbType = SqlDbType.NVarChar, .MaxLength = 200})
        d.ScalarColumns.Add(New ScalarColumn With {.ColumnName = "description", .JsonName = "description", .DbType = SqlDbType.NVarChar})
        d.JsonColumns.Add(New JsonColumn With {.ColumnName = "emails_json", .JsonName = "emails"})
        d.FilterableColumns.UnionWith({"id", "created_by", "name"})
        d.SortableColumns.UnionWith({"created_date", "updated_date", "name"})
        Return d
    End Function

    Private Function BuildSurvey() As EntityDescriptor
        Dim d As New EntityDescriptor() With {.TableName = "Surveys"}
        d.ScalarColumns.Add(New ScalarColumn With {.ColumnName = "title",       .JsonName = "title",       .DbType = SqlDbType.NVarChar, .MaxLength = 500})
        d.ScalarColumns.Add(New ScalarColumn With {.ColumnName = "description", .JsonName = "description", .DbType = SqlDbType.NVarChar})
        d.ScalarColumns.Add(New ScalarColumn With {.ColumnName = "status",      .JsonName = "status",      .DbType = SqlDbType.NVarChar, .MaxLength = 20})
        d.JsonColumns.Add(New JsonColumn With {.ColumnName = "questions_json", .JsonName = "questions"})
        d.JsonColumns.Add(New JsonColumn With {.ColumnName = "settings_json",  .JsonName = "settings"})
        d.FilterableColumns.UnionWith({"id", "created_by", "status", "title"})
        d.SortableColumns.UnionWith({"created_date", "updated_date", "title", "status"})
        Return d
    End Function

    Private Function BuildSurveyInvite() As EntityDescriptor
        Dim d As New EntityDescriptor() With {.TableName = "SurveyInvites"}
        d.ScalarColumns.Add(New ScalarColumn With {.ColumnName = "survey_id",     .JsonName = "survey_id",     .DbType = SqlDbType.Char,     .MaxLength = 36})
        d.ScalarColumns.Add(New ScalarColumn With {.ColumnName = "email",         .JsonName = "email",         .DbType = SqlDbType.NVarChar, .MaxLength = 320})
        d.ScalarColumns.Add(New ScalarColumn With {.ColumnName = "token",         .JsonName = "token",         .DbType = SqlDbType.NVarChar, .MaxLength = 100})
        d.ScalarColumns.Add(New ScalarColumn With {.ColumnName = "status",        .JsonName = "status",        .DbType = SqlDbType.NVarChar, .MaxLength = 20})
        d.ScalarColumns.Add(New ScalarColumn With {.ColumnName = "sent_at",       .JsonName = "sent_at",       .DbType = SqlDbType.DateTime2})
        ' Respondent metadata — powers the History report. respondent_id is
        ' filled by a SQL DEFAULT (NEXT VALUE FOR dbo.seq_respondent), so
        ' inserts that omit it still get a unique number. JsonRepository
        ' will round-trip it on subsequent updates like any other scalar.
        d.ScalarColumns.Add(New ScalarColumn With {.ColumnName = "respondent_id", .JsonName = "respondent_id", .DbType = SqlDbType.BigInt})
        d.ScalarColumns.Add(New ScalarColumn With {.ColumnName = "first_name",    .JsonName = "first_name",    .DbType = SqlDbType.NVarChar, .MaxLength = 100})
        d.ScalarColumns.Add(New ScalarColumn With {.ColumnName = "last_name",     .JsonName = "last_name",     .DbType = SqlDbType.NVarChar, .MaxLength = 100})
        d.ScalarColumns.Add(New ScalarColumn With {.ColumnName = "custom_id",     .JsonName = "custom_id",     .DbType = SqlDbType.NVarChar, .MaxLength = 100})
        d.ScalarColumns.Add(New ScalarColumn With {.ColumnName = "company",       .JsonName = "company",       .DbType = SqlDbType.NVarChar, .MaxLength = 200})
        d.ScalarColumns.Add(New ScalarColumn With {.ColumnName = "password",      .JsonName = "password",      .DbType = SqlDbType.NVarChar, .MaxLength = 20})
        d.ScalarColumns.Add(New ScalarColumn With {.ColumnName = "opened_at",     .JsonName = "opened_at",     .DbType = SqlDbType.DateTime2})
        d.ScalarColumns.Add(New ScalarColumn With {.ColumnName = "completed_at",  .JsonName = "completed_at",  .DbType = SqlDbType.DateTime2})
        d.FilterableColumns.UnionWith({"id", "created_by", "survey_id", "email", "token", "status", "respondent_id", "custom_id", "company"})
        d.SortableColumns.UnionWith({"created_date", "updated_date", "sent_at", "email", "status", "respondent_id", "last_name", "first_name", "company", "opened_at", "completed_at"})
        d.RequireCreatedBy = False  ' invites are created on behalf of survey owner; field populated separately
        Return d
    End Function

    Private Function BuildSurveyResponse() As EntityDescriptor
        Dim d As New EntityDescriptor() With {.TableName = "SurveyResponses"}
        d.ScalarColumns.Add(New ScalarColumn With {.ColumnName = "survey_id",              .JsonName = "survey_id",              .DbType = SqlDbType.Char,     .MaxLength = 36})
        d.ScalarColumns.Add(New ScalarColumn With {.ColumnName = "invite_id",              .JsonName = "invite_id",              .DbType = SqlDbType.Char,     .MaxLength = 36})
        d.ScalarColumns.Add(New ScalarColumn With {.ColumnName = "respondent_email",       .JsonName = "respondent_email",       .DbType = SqlDbType.NVarChar, .MaxLength = 320})
        d.ScalarColumns.Add(New ScalarColumn With {.ColumnName = "status",                 .JsonName = "status",                 .DbType = SqlDbType.NVarChar, .MaxLength = 20})
        d.ScalarColumns.Add(New ScalarColumn With {.ColumnName = "current_question_index", .JsonName = "current_question_index", .DbType = SqlDbType.Int})
        d.ScalarColumns.Add(New ScalarColumn With {.ColumnName = "device_type",            .JsonName = "device_type",            .DbType = SqlDbType.NVarChar, .MaxLength = 20})
        d.ScalarColumns.Add(New ScalarColumn With {.ColumnName = "started_at",             .JsonName = "started_at",             .DbType = SqlDbType.DateTime2})
        d.ScalarColumns.Add(New ScalarColumn With {.ColumnName = "completed_at",           .JsonName = "completed_at",           .DbType = SqlDbType.DateTime2})
        d.ScalarColumns.Add(New ScalarColumn With {.ColumnName = "sentiment_score",        .JsonName = "sentiment_score",        .DbType = SqlDbType.Float})
        d.ScalarColumns.Add(New ScalarColumn With {.ColumnName = "sentiment_label",        .JsonName = "sentiment_label",        .DbType = SqlDbType.NVarChar, .MaxLength = 50})
        d.JsonColumns.Add(New JsonColumn With {.ColumnName = "answers_json", .JsonName = "answers"})
        d.FilterableColumns.UnionWith({"id", "created_by", "survey_id", "invite_id", "status", "respondent_email"})
        d.SortableColumns.UnionWith({"created_date", "updated_date", "started_at", "completed_at"})
        d.RequireCreatedBy = False  ' anonymous respondents are allowed
        Return d
    End Function

    ' Per-owner opt-out list. Keyed by (owner_user_email, email). Used by
    ' the email-send paths to skip blacklisted recipients silently.
    Private Function BuildEmailBlacklist() As EntityDescriptor
        Dim d As New EntityDescriptor() With {.TableName = "EmailBlacklist"}
        d.ScalarColumns.Add(New ScalarColumn With {.ColumnName = "owner_user_email",        .JsonName = "owner_user_email",        .DbType = SqlDbType.NVarChar, .MaxLength = 320})
        d.ScalarColumns.Add(New ScalarColumn With {.ColumnName = "email",                   .JsonName = "email",                   .DbType = SqlDbType.NVarChar, .MaxLength = 320})
        d.ScalarColumns.Add(New ScalarColumn With {.ColumnName = "opted_out_via_survey_id", .JsonName = "opted_out_via_survey_id", .DbType = SqlDbType.Char,     .MaxLength = 36})
        d.ScalarColumns.Add(New ScalarColumn With {.ColumnName = "opted_out_at",            .JsonName = "opted_out_at",            .DbType = SqlDbType.DateTime2})
        d.ScalarColumns.Add(New ScalarColumn With {.ColumnName = "reason",                  .JsonName = "reason",                  .DbType = SqlDbType.NVarChar, .MaxLength = 500})
        d.FilterableColumns.UnionWith({"id", "owner_user_email", "email", "opted_out_via_survey_id"})
        d.SortableColumns.UnionWith({"created_date", "updated_date", "opted_out_at", "email"})
        d.RequireCreatedBy = True
        Return d
    End Function

    Private Function BuildQuestionLibrary() As EntityDescriptor
        Dim d As New EntityDescriptor() With {.TableName = "QuestionLibrary"}
        d.ScalarColumns.Add(New ScalarColumn With {.ColumnName = "name",                      .JsonName = "name",                      .DbType = SqlDbType.NVarChar, .MaxLength = 300})
        d.ScalarColumns.Add(New ScalarColumn With {.ColumnName = "type",                      .JsonName = "type",                      .DbType = SqlDbType.NVarChar, .MaxLength = 50})
        d.ScalarColumns.Add(New ScalarColumn With {.ColumnName = "text",                      .JsonName = "text",                      .DbType = SqlDbType.NVarChar})
        d.ScalarColumns.Add(New ScalarColumn With {.ColumnName = "description",               .JsonName = "description",               .DbType = SqlDbType.NVarChar})
        d.ScalarColumns.Add(New ScalarColumn With {.ColumnName = "required",                  .JsonName = "required",                  .DbType = SqlDbType.Bit})
        d.ScalarColumns.Add(New ScalarColumn With {.ColumnName = "grid_single_select",        .JsonName = "grid_single_select",        .DbType = SqlDbType.Bit})
        d.ScalarColumns.Add(New ScalarColumn With {.ColumnName = "rating_max",                .JsonName = "rating_max",                .DbType = SqlDbType.Int})
        d.ScalarColumns.Add(New ScalarColumn With {.ColumnName = "allocation_must_total_100", .JsonName = "allocation_must_total_100", .DbType = SqlDbType.Bit})
        d.JsonColumns.Add(New JsonColumn With {.ColumnName = "tags_json",             .JsonName = "tags"})
        d.JsonColumns.Add(New JsonColumn With {.ColumnName = "options_json",          .JsonName = "options"})
        d.JsonColumns.Add(New JsonColumn With {.ColumnName = "grid_rows_json",        .JsonName = "grid_rows"})
        d.JsonColumns.Add(New JsonColumn With {.ColumnName = "grid_columns_json",     .JsonName = "grid_columns"})
        d.JsonColumns.Add(New JsonColumn With {.ColumnName = "categories_json",       .JsonName = "categories"})
        d.JsonColumns.Add(New JsonColumn With {.ColumnName = "quality_analysis_json", .JsonName = "quality_analysis"})

        ' Catch-all for type-specific fields not mapped above
        ' (conjoint_attributes, maxdiff_sets, image_options, click_map_*,
        ' emoji_*, spreadsheet_*, nps_*, date_format, file_types, likert_*,
        ' numbers_only, etc.). Anything else round-trips losslessly via
        ' details_json. Mapped columns above take precedence on read.
        d.CatchAllJsonColumn = "details_json"

        d.FilterableColumns.UnionWith({"id", "created_by", "type", "name"})
        d.SortableColumns.UnionWith({"created_date", "updated_date", "name", "type"})
        Return d
    End Function

End Module
