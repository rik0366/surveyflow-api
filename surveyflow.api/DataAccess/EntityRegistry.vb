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
    Public ReadOnly Contact As EntityDescriptor = BuildContact()
    Public ReadOnly ContactGroup As EntityDescriptor = BuildContactGroup()
    Public ReadOnly ContactField As EntityDescriptor = BuildContactField()
    Public ReadOnly ContactNote As EntityDescriptor = BuildContactNote()


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
        ' Address-book link (09). custom = snapshot of the contact's custom
        ' field values at invite time.
        d.ScalarColumns.Add(New ScalarColumn With {.ColumnName = "contact_id",       .JsonName = "contact_id",       .DbType = SqlDbType.Char, .MaxLength = 36})
        d.ScalarColumns.Add(New ScalarColumn With {.ColumnName = "reminder_count",   .JsonName = "reminder_count",   .DbType = SqlDbType.Int})
        d.ScalarColumns.Add(New ScalarColumn With {.ColumnName = "last_reminder_at", .JsonName = "last_reminder_at", .DbType = SqlDbType.DateTime2})
        d.JsonColumns.Add(New JsonColumn With {.ColumnName = "custom_json", .JsonName = "custom"})
        d.FilterableColumns.UnionWith({"id", "created_by", "survey_id", "email", "token", "status", "respondent_id", "custom_id", "company", "contact_id"})
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

    ' --- Address book (09) -------------------------------------------------
    ' Contacts are written through ContactService (normalisation, match_key,
    ' duplicate rules); this descriptor serves reads and the generic filter.
    Private Function BuildContact() As EntityDescriptor
        Dim d As New EntityDescriptor() With {.TableName = "Contacts"}
        d.ScalarColumns.Add(New ScalarColumn With {.ColumnName = "first_name",     .JsonName = "first_name",     .DbType = SqlDbType.NVarChar, .MaxLength = 100})
        d.ScalarColumns.Add(New ScalarColumn With {.ColumnName = "last_name",      .JsonName = "last_name",      .DbType = SqlDbType.NVarChar, .MaxLength = 100})
        d.ScalarColumns.Add(New ScalarColumn With {.ColumnName = "email",          .JsonName = "email",          .DbType = SqlDbType.NVarChar, .MaxLength = 320})
        d.ScalarColumns.Add(New ScalarColumn With {.ColumnName = "id_number",      .JsonName = "id_number",      .DbType = SqlDbType.NVarChar, .MaxLength = 100})
        d.ScalarColumns.Add(New ScalarColumn With {.ColumnName = "company",        .JsonName = "company",        .DbType = SqlDbType.NVarChar, .MaxLength = 200})
        d.ScalarColumns.Add(New ScalarColumn With {.ColumnName = "is_active",      .JsonName = "is_active",      .DbType = SqlDbType.Bit})
        d.ScalarColumns.Add(New ScalarColumn With {.ColumnName = "is_hidden",      .JsonName = "is_hidden",      .DbType = SqlDbType.Bit})
        d.ScalarColumns.Add(New ScalarColumn With {.ColumnName = "deactivated_at", .JsonName = "deactivated_at", .DbType = SqlDbType.DateTime2})
        d.JsonColumns.Add(New JsonColumn With {.ColumnName = "custom_json", .JsonName = "custom"})
        d.FilterableColumns.UnionWith({"id", "email", "id_number", "company", "last_name", "is_active", "is_hidden"})
        d.SortableColumns.UnionWith({"created_date", "updated_date", "last_name", "first_name", "email", "company", "id_number"})
        Return d
    End Function

    Private Function BuildContactGroup() As EntityDescriptor
        Dim d As New EntityDescriptor() With {.TableName = "ContactGroups"}
        d.ScalarColumns.Add(New ScalarColumn With {.ColumnName = "name",                 .JsonName = "name",                 .DbType = SqlDbType.NVarChar, .MaxLength = 200})
        d.ScalarColumns.Add(New ScalarColumn With {.ColumnName = "description",          .JsonName = "description",          .DbType = SqlDbType.NVarChar})
        d.ScalarColumns.Add(New ScalarColumn With {.ColumnName = "is_panel",             .JsonName = "is_panel",             .DbType = SqlDbType.Bit})
        d.ScalarColumns.Add(New ScalarColumn With {.ColumnName = "is_active",            .JsonName = "is_active",            .DbType = SqlDbType.Bit})
        d.ScalarColumns.Add(New ScalarColumn With {.ColumnName = "copied_from_group_id", .JsonName = "copied_from_group_id", .DbType = SqlDbType.Char, .MaxLength = 36})
        d.FilterableColumns.UnionWith({"id", "name", "is_active", "is_panel"})
        d.SortableColumns.UnionWith({"created_date", "updated_date", "name"})
        Return d
    End Function

    Private Function BuildContactField() As EntityDescriptor
        Dim d As New EntityDescriptor() With {.TableName = "ContactFields"}
        d.ScalarColumns.Add(New ScalarColumn With {.ColumnName = "field_key",  .JsonName = "field_key",  .DbType = SqlDbType.NVarChar, .MaxLength = 100})
        d.ScalarColumns.Add(New ScalarColumn With {.ColumnName = "label",      .JsonName = "label",      .DbType = SqlDbType.NVarChar, .MaxLength = 200})
        d.ScalarColumns.Add(New ScalarColumn With {.ColumnName = "field_type", .JsonName = "field_type", .DbType = SqlDbType.NVarChar, .MaxLength = 20})
        d.ScalarColumns.Add(New ScalarColumn With {.ColumnName = "sort_order", .JsonName = "sort_order", .DbType = SqlDbType.Int})
        d.ScalarColumns.Add(New ScalarColumn With {.ColumnName = "is_active",  .JsonName = "is_active",  .DbType = SqlDbType.Bit})
        d.JsonColumns.Add(New JsonColumn With {.ColumnName = "options_json",    .JsonName = "options"})
        d.JsonColumns.Add(New JsonColumn With {.ColumnName = "survey_ids_json", .JsonName = "survey_ids"})
        d.FilterableColumns.UnionWith({"id", "field_key", "is_active"})
        d.SortableColumns.UnionWith({"created_date", "sort_order", "label"})
        Return d
    End Function

    Private Function BuildContactNote() As EntityDescriptor
        Dim d As New EntityDescriptor() With {.TableName = "ContactNotes"}
        d.ScalarColumns.Add(New ScalarColumn With {.ColumnName = "contact_id", .JsonName = "contact_id", .DbType = SqlDbType.Char,     .MaxLength = 36})
        d.ScalarColumns.Add(New ScalarColumn With {.ColumnName = "survey_id",  .JsonName = "survey_id",  .DbType = SqlDbType.Char,     .MaxLength = 36})
        d.ScalarColumns.Add(New ScalarColumn With {.ColumnName = "note_type",  .JsonName = "note_type",  .DbType = SqlDbType.NVarChar, .MaxLength = 20})
        d.ScalarColumns.Add(New ScalarColumn With {.ColumnName = "note",       .JsonName = "note",       .DbType = SqlDbType.NVarChar})
        d.FilterableColumns.UnionWith({"id", "contact_id", "survey_id", "note_type"})
        d.SortableColumns.UnionWith({"created_date"})
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
