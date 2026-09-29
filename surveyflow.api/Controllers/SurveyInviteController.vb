Imports System.Net
Imports System.Security.Cryptography
Imports System.Web.Http
Imports Newtonsoft.Json.Linq

' Invites. Two kinds of caller:
'   * Account users (logged in, invite's survey in their account) — full CRUD,
'     limited to their account.
'   * Respondents (TakeSurvey; anonymous, or logged in to another account) —
'     may only look an invite up by its token and advance its progress fields.
Public Class SurveyInviteController
    Inherits ApiController

    Private Shared ReadOnly Repo As New JsonRepository(EntityRegistry.SurveyInvite)

    ' Alphabet for respondent passwords. Skips lookalikes (0/O, 1/I/L) so the
    ' code is readable when typed off a paper invite or printed report.
    Private Const PasswordAlphabet As String = "ABCDEFGHJKMNPQRSTUVWXYZ23456789"
    Private Const PasswordLength As Integer = 6

    ' Statuses a respondent may set while taking the survey.
    Private Shared ReadOnly RespondentStatuses As New HashSet(Of String)({"opened", "started", "completed"})

    ' Cryptographically-random short code. Uses RandomNumberGenerator so we
    ' don't lean on Random (which is seeded from the clock and can repeat
    ' under burst inserts). Six chars from a 31-char alphabet ≈ 30 bits of
    ' entropy — fine for a per-invite shared secret that's also gated by the
    ' invite token / respondent_id.
    Private Shared Function GeneratePassword() As String
        Dim sb As New System.Text.StringBuilder(PasswordLength)
        Using rng = RandomNumberGenerator.Create()
            Dim buf(PasswordLength - 1) As Byte
            rng.GetBytes(buf)
            For i = 0 To PasswordLength - 1
                sb.Append(PasswordAlphabet(buf(i) Mod PasswordAlphabet.Length))
            Next
        End Using
        Return sb.ToString()
    End Function

    ' 128-bit random URL token, used when the client doesn't supply one.
    Private Shared Function GenerateToken() As String
        Dim buf(15) As Byte
        Using rng = RandomNumberGenerator.Create()
            rng.GetBytes(buf)
        End Using
        Return BitConverter.ToString(buf).Replace("-", "").ToLowerInvariant()
    End Function

    ' Stamp a password and token onto the incoming invite payload if the
    ' caller didn't provide them. Treat null / missing / whitespace as
    ' "please generate".
    Private Shared Sub EnsureSecrets(data As JObject)
        If data Is Nothing Then Return
        If String.IsNullOrWhiteSpace(data.Value(Of String)("password")) Then data("password") = GeneratePassword()
        If String.IsNullOrWhiteSpace(data.Value(Of String)("token")) Then data("token") = GenerateToken()
    End Sub

    ' Invites must belong to a survey in the caller's account.
    Private Function CheckSurvey(data As JObject) As IHttpActionResult
        Dim surveyId = data?.Value(Of String)("survey_id")
        If String.IsNullOrEmpty(surveyId) Then Return BadRequest("'survey_id' is required.")
        If Not Ownership.SurveyInCallerAccount(surveyId) Then Return NotFound()
        Return Nothing
    End Function

    ' AllowAnonymous: TakeSurvey looks the invite up by token (an opaque
    ' secret) before letting the respondent in — the token IS the auth, so
    ' that lookup is unscoped and may filter on nothing else. Every other
    ' listing requires login and is limited to the caller's account.
    <HttpGet, Route("SurveyInvite"), AllowAnonymous>
    Public Function List(Optional filter As String = "", Optional sort As String = "", Optional limit As Integer? = Nothing) As IHttpActionResult
        Dim filterObj As JObject = Nothing
        If Not String.IsNullOrEmpty(filter) Then filterObj = JObject.Parse(filter)

        Dim token = filterObj?.Value(Of String)("token")
        If Not String.IsNullOrEmpty(token) Then
            Return Ok(Repo.Find(New JObject From {{"token", token}}, "", 1, RepoScope.Unscoped))
        End If

        If CurrentUser.Info() Is Nothing Then Return Unauthorized()
        Return Ok(Repo.Find(filterObj, sort, limit, CurrentUser.Scope()))
    End Function

    <HttpGet, Route("SurveyInvite/{id}")>
    Public Function GetOne(id As String) As IHttpActionResult
        Dim row = Repo.GetById(id, CurrentUser.Scope())
        If row Is Nothing Then Return NotFound()
        Return Ok(row)
    End Function

    <HttpPost, Route("SurveyInvite")>
    Public Function Create(<FromBody> data As JObject) As IHttpActionResult
        Dim bad = CheckSurvey(data)
        If bad IsNot Nothing Then Return bad
        EnsureSecrets(data)
        Return Ok(Repo.Create(data, CurrentUser.Email(), CurrentUser.Scope()))
    End Function

    ' AllowAnonymous: TakeSurvey advances invite status (opened → started →
    ' completed) as the respondent moves through the survey. Account users
    ' editing their own invites get a full update; anyone else is treated as
    ' the respondent and may only set status/opened_at/completed_at.
    <HttpPut, Route("SurveyInvite/{id}"), AllowAnonymous>
    Public Function Update(id As String, <FromBody> data As JObject) As IHttpActionResult
        If data Is Nothing Then Return BadRequest("Request body is required.")

        If CurrentUser.Info() IsNot Nothing AndAlso Repo.GetById(id, CurrentUser.Scope()) IsNot Nothing Then
            If data("survey_id") IsNot Nothing AndAlso Not Ownership.SurveyInCallerAccount(data.Value(Of String)("survey_id")) Then
                Return NotFound()
            End If
            Return Ok(Repo.Update(id, data, CurrentUser.Scope()))
        End If

        Dim respondentData = Ownership.Pick(data, "status", "opened_at", "completed_at")
        Dim status = respondentData.Value(Of String)("status")
        If status IsNot Nothing AndAlso Not RespondentStatuses.Contains(status) Then
            Return BadRequest($"Status '{status}' can't be set here.")
        End If
        If respondentData.Count = 0 Then Return BadRequest("Nothing to update.")

        Dim row = Repo.Update(id, respondentData, RepoScope.Unscoped)
        If row Is Nothing Then Return NotFound()
        Return Ok(New JObject From {{"id", row("id")}, {"status", row("status")}})
    End Function

    <HttpDelete, Route("SurveyInvite/{id}")>
    Public Function Delete(id As String) As IHttpActionResult
        If Repo.Delete(id, CurrentUser.Scope()) Then Return StatusCode(HttpStatusCode.NoContent)
        Return NotFound()
    End Function

    <HttpPost, Route("SurveyInvite/bulk")>
    Public Function Bulk(<FromBody> items As JArray) As IHttpActionResult
        If items Is Nothing Then Return BadRequest("Request body is required.")
        For Each item In items.OfType(Of JObject)()
            Dim bad = CheckSurvey(item)
            If bad IsNot Nothing Then Return bad
            EnsureSecrets(item)
        Next
        Return Ok(Repo.BulkCreate(items, CurrentUser.Email(), CurrentUser.Scope()))
    End Function
End Class
