Imports System.Net
Imports System.Web.Http
Imports Newtonsoft.Json.Linq

' Responses. Two kinds of caller:
'   * Account users (logged in, response's survey in their account) — full
'     CRUD, limited to their account.
'   * Respondents (TakeSurvey; anonymous, or logged in to another account) —
'     may create a response, resume it by invite_id, and save answers until
'     it's completed.
' A response always lands in its survey's account, whoever creates it.
Public Class SurveyResponseController
    Inherits ApiController

    Private Shared ReadOnly Repo As New JsonRepository(EntityRegistry.SurveyResponse)
    Private Shared ReadOnly InviteRepo As New JsonRepository(EntityRegistry.SurveyInvite)

    ' Fields a respondent may write.
    Private Shared ReadOnly RespondentCreateFields As String() =
        {"survey_id", "invite_id", "respondent_email", "answers", "status",
         "current_question_index", "device_type", "started_at", "completed_at"}
    Private Shared ReadOnly RespondentUpdateFields As String() =
        {"answers", "status", "current_question_index", "completed_at"}
    Private Shared ReadOnly RespondentStatuses As New HashSet(Of String)({"in_progress", "completed"})

    ' AllowAnonymous: TakeSurvey filters by invite_id to resume an
    ' in-progress response. That lookup is unscoped (the invite id comes from
    ' the token lookup) and may filter on nothing else. Every other listing
    ' requires login and is limited to the caller's account.
    <HttpGet, Route("SurveyResponse"), AllowAnonymous>
    Public Function List(Optional filter As String = "", Optional sort As String = "", Optional limit As Integer? = Nothing) As IHttpActionResult
        Dim filterObj As JObject = Nothing
        If Not String.IsNullOrEmpty(filter) Then filterObj = JObject.Parse(filter)

        If CurrentUser.Info() Is Nothing Then
            Dim inviteId = filterObj?.Value(Of String)("invite_id")
            If String.IsNullOrEmpty(inviteId) Then Return Unauthorized()
            Return Ok(Repo.Find(New JObject From {{"invite_id", inviteId}}, "created_date", Nothing, RepoScope.Unscoped))
        End If

        Return Ok(Repo.Find(filterObj, sort, limit, CurrentUser.Scope()))
    End Function

    <HttpGet, Route("SurveyResponse/{id}")>
    Public Function GetOne(id As String) As IHttpActionResult
        Dim row = Repo.GetById(id, CurrentUser.Scope())
        If row Is Nothing Then Return NotFound()
        Return Ok(row)
    End Function

    ' AllowAnonymous: respondents are (intentionally) not logged in.
    <HttpPost, Route("SurveyResponse"), AllowAnonymous>
    Public Function Create(<FromBody> data As JObject) As IHttpActionResult
        If data Is Nothing Then Return BadRequest("Request body is required.")
        Dim surveyId = data.Value(Of String)("survey_id")
        Dim surveyAccount = Ownership.SurveyAccountId(surveyId)
        If surveyAccount Is Nothing Then Return BadRequest("Unknown survey.")

        Dim isOwner = CurrentUser.Info() IsNot Nothing AndAlso
                      String.Equals(CurrentUser.AccountId(), surveyAccount, StringComparison.OrdinalIgnoreCase)
        If Not isOwner Then
            data = Ownership.Pick(data, RespondentCreateFields)
            Dim status = data.Value(Of String)("status")
            If status IsNot Nothing AndAlso Not RespondentStatuses.Contains(status) Then Return BadRequest($"Status '{status}' can't be set here.")
        End If

        ' An invite, if given, must belong to the same survey.
        Dim inviteId = data.Value(Of String)("invite_id")
        If Not String.IsNullOrEmpty(inviteId) Then
            Dim invite = InviteRepo.GetById(inviteId, RepoScope.ForAccount(surveyAccount))
            If invite Is Nothing OrElse Not String.Equals(invite.Value(Of String)("survey_id"), surveyId, StringComparison.OrdinalIgnoreCase) Then
                Return BadRequest("Invite doesn't belong to this survey.")
            End If
        End If

        Return Ok(Repo.Create(data, CurrentUser.Email(), RepoScope.ForAccount(surveyAccount)))
    End Function

    ' AllowAnonymous: progressive saves while taking the survey. Account
    ' users get a full update of their own responses; anyone else may only
    ' save answers/progress, and not once the response is completed.
    <HttpPut, Route("SurveyResponse/{id}"), AllowAnonymous>
    Public Function Update(id As String, <FromBody> data As JObject) As IHttpActionResult
        If data Is Nothing Then Return BadRequest("Request body is required.")

        If CurrentUser.Info() IsNot Nothing AndAlso Repo.GetById(id, CurrentUser.Scope()) IsNot Nothing Then
            If data("survey_id") IsNot Nothing AndAlso Not Ownership.SurveyInCallerAccount(data.Value(Of String)("survey_id")) Then
                Return NotFound()
            End If
            Return Ok(Repo.Update(id, data, CurrentUser.Scope()))
        End If

        Dim existing = Repo.GetById(id, RepoScope.Unscoped)
        If existing Is Nothing Then Return NotFound()
        If existing.Value(Of String)("status") = "completed" Then
            Return Content(HttpStatusCode.Conflict, New JObject From {{"message", "This response has already been submitted."}})
        End If

        Dim respondentData = Ownership.Pick(data, RespondentUpdateFields)
        Dim status = respondentData.Value(Of String)("status")
        If status IsNot Nothing AndAlso Not RespondentStatuses.Contains(status) Then Return BadRequest($"Status '{status}' can't be set here.")
        If respondentData.Count = 0 Then Return BadRequest("Nothing to update.")

        Return Ok(Repo.Update(id, respondentData, RepoScope.Unscoped))
    End Function

    <HttpDelete, Route("SurveyResponse/{id}")>
    Public Function Delete(id As String) As IHttpActionResult
        If Repo.Delete(id, CurrentUser.Scope()) Then Return StatusCode(HttpStatusCode.NoContent)
        Return NotFound()
    End Function

    <HttpPost, Route("SurveyResponse/bulk")>
    Public Function Bulk(<FromBody> items As JArray) As IHttpActionResult
        If items Is Nothing Then Return BadRequest("Request body is required.")
        For Each item In items.OfType(Of JObject)()
            If Not Ownership.SurveyInCallerAccount(item.Value(Of String)("survey_id")) Then Return NotFound()
        Next
        Return Ok(Repo.BulkCreate(items, CurrentUser.Email(), CurrentUser.Scope()))
    End Function
End Class
