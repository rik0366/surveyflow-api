Imports System.Data
Imports System.Data.SqlClient
Imports System.Linq
Imports System.Web.Http
Imports Newtonsoft.Json.Linq

' "Import from PIQ": copy one legacy PIQ survey definition (by Class_ID)
' into a client account as a DRAFT SurveyFlow survey. System admins only.
' Responses are not imported. See Services/PiqImportService.vb.
'
'   POST /api/admin/piq-import  { class_id, account_id, dry_run? }
'     dry_run = true  → preview only: title, counts, warnings, earlier imports
'     dry_run = false → also creates the draft and returns survey_id
Public Class PiqImportController
    Inherits ApiController

    Private Shared ReadOnly SurveyRepo As New JsonRepository(EntityRegistry.Survey)

    <HttpPost, Route("admin/piq-import")>
    Public Function Import(<FromBody> data As JObject) As IHttpActionResult
        CurrentUser.RequireSystemAdmin()
        Dim classId = If(data?.Value(Of Integer?)("class_id"), 0)
        If classId <= 0 Then Return BadRequest("Enter the PIQ survey's class_id (a number).")
        Dim accountId = If(data.Value(Of String)("account_id"), CurrentUser.Info().HomeAccountId)
        Dim dryRun = data.Value(Of Boolean?)("dry_run").GetValueOrDefault(True)
        If Not AccountIsActive(accountId) Then Return BadRequest("Choose an active client to import into.")

        Dim result As PiqImportResult
        Try
            result = PiqImportService.Convert(classId)
        Catch ex As InvalidOperationException
            Return BadRequest(ex.Message)
        Catch ex As SqlException
            Return BadRequest("Couldn't read the PIQ database: " & ex.Message)
        End Try
        If result Is Nothing Then Return BadRequest($"No PIQ survey found with class_id {classId}.")

        Dim out As New JObject From {
            {"class_id", classId},
            {"title", result.Title},
            {"question_count", result.QuestionCount},
            {"page_count", result.PageCount},
            {"type_counts", JObject.FromObject(result.TypeCounts)},
            {"warnings", New JArray(result.Warnings.ToArray())},
            {"earlier_imports", EarlierImports(classId)}
        }
        If dryRun Then Return Ok(out)

        Dim survey = result.Survey
        survey("status") = "draft"
        If result.Title.Length > 500 Then survey("title") = result.Title.Substring(0, 500)
        Dim created = SurveyRepo.Create(survey, CurrentUser.Email(), RepoScope.ForAccount(accountId))
        out("survey_id") = created.Value(Of String)("id")
        Return Ok(out)
    End Function

    ' Surveys already imported from this class_id (any client), newest first.
    Private Shared Function EarlierImports(classId As Integer) As JArray
        Dim arr As New JArray
        Using conn = Db.Open()
            Using cmd = conn.CreateCommand()
                cmd.CommandText = "
                    SELECT s.id, s.title, s.status, a.name AS account_name, s.created_date
                    FROM dbo.Surveys s LEFT JOIN dbo.Accounts a ON a.id = s.account_id
                    WHERE ISJSON(s.settings_json) = 1
                      AND JSON_VALUE(s.settings_json, '$.imported_from_piq.class_id') = @c
                    ORDER BY s.created_date DESC"
                cmd.Parameters.Add(New SqlParameter("@c", SqlDbType.NVarChar, 20) With {.Value = classId.ToString()})
                Using rdr = cmd.ExecuteReader()
                    While rdr.Read()
                        arr.Add(New JObject From {
                            {"id", rdr.GetString(0)}, {"title", rdr.GetString(1)},
                            {"status", If(rdr.IsDBNull(2), "", rdr.GetString(2))},
                            {"account_name", If(rdr.IsDBNull(3), "", rdr.GetString(3))},
                            {"created_date", rdr.GetDateTime(4)}})
                    End While
                End Using
            End Using
        End Using
        Return arr
    End Function

    Private Shared Function AccountIsActive(accountId As String) As Boolean
        If String.IsNullOrEmpty(accountId) Then Return False
        Using conn = Db.Open()
            Using cmd = conn.CreateCommand()
                cmd.CommandText = "SELECT COUNT(*) FROM dbo.Accounts WHERE id = @a AND is_active = 1"
                cmd.Parameters.Add(New SqlParameter("@a", SqlDbType.Char, 36) With {.Value = accountId})
                Return CInt(cmd.ExecuteScalar()) > 0
            End Using
        End Using
    End Function

End Class
