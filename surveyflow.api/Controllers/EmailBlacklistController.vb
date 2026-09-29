Imports System.Net
Imports System.Web.Http
Imports Newtonsoft.Json.Linq

' Per-account opt-out list. The email-send paths (sendSurveyEmail,
' sendScheduledReminders) check this table — plus the global
' EmailSuppression list — before delivering anything.
'
' Auth: all endpoints require login and are limited to the caller's
' account. owner_user_email is still written (the current UI reads it) but
' no longer decides scope.
Public Class EmailBlacklistController
    Inherits ApiController

    Private Shared ReadOnly Repo As New JsonRepository(EntityRegistry.EmailBlacklist)

    <HttpGet, Route("EmailBlacklist")>
    Public Function List(Optional filter As String = "", Optional sort As String = "", Optional limit As Integer? = Nothing) As JArray
        Dim filterObj As JObject = Nothing
        If Not String.IsNullOrEmpty(filter) Then filterObj = JObject.Parse(filter)
        ' Older UI filters by owner_user_email; within an account every
        ' colleague shares one list, so that key is dropped.
        filterObj?.Remove("owner_user_email")
        Return Repo.Find(filterObj, sort, limit, CurrentUser.Scope())
    End Function

    <HttpGet, Route("EmailBlacklist/{id}")>
    Public Function GetOne(id As String) As IHttpActionResult
        Dim row = Repo.GetById(id, CurrentUser.Scope())
        If row Is Nothing Then Return NotFound()
        Return Ok(row)
    End Function

    ' Add an opt-out for the caller's account. opted_out_at defaults to now.
    <HttpPost, Route("EmailBlacklist")>
    Public Function Create(<FromBody> data As JObject) As IHttpActionResult
        If data Is Nothing Then data = New JObject()
        Dim caller = CurrentUser.Email()
        data("owner_user_email") = caller
        If String.IsNullOrEmpty(data.Value(Of String)("opted_out_at")) Then
            data("opted_out_at") = DateTime.UtcNow.ToString("o")
        End If
        Try
            Dim created = Repo.Create(data, caller, CurrentUser.Scope())
            Return Ok(created)
        Catch ex As Exception
            ' Most likely a uniqueness violation (already blacklisted) —
            ' surface as a 409 so the UI can handle gracefully (silent
            ' skip when called from opt-out, friendly toast otherwise).
            Return Content(HttpStatusCode.Conflict, New JObject From {{"message", ex.Message}})
        End Try
    End Function

    <HttpDelete, Route("EmailBlacklist/{id}")>
    Public Function Delete(id As String) As IHttpActionResult
        If Repo.Delete(id, CurrentUser.Scope()) Then Return StatusCode(HttpStatusCode.NoContent)
        Return NotFound()
    End Function

    ' Given a list of recipient emails, returns which ones must not be
    ' emailed from the caller's account (account opt-outs + global
    ' suppression). owner_user_email in the body is accepted but ignored.
    '
    ' Request body: { "emails": ["a","b",...] }
    ' Response:     { "blacklisted": ["a", ...] }
    <HttpPost, Route("EmailBlacklist/check")>
    Public Function Check(<FromBody> data As JObject) As IHttpActionResult
        Dim emails = TryCast(data?("emails"), JArray)
        Dim hits As New JArray
        If emails Is Nothing OrElse emails.Count = 0 Then Return Ok(New JObject From {{"blacklisted", hits}})

        Dim accountId = CurrentUser.AccountId()
        For Each item As JToken In emails
            Dim s = item.ToString()
            If Not String.IsNullOrEmpty(s) AndAlso Ownership.IsSuppressed(accountId, s) Then hits.Add(s)
        Next
        Return Ok(New JObject From {{"blacklisted", hits}})
    End Function
End Class
