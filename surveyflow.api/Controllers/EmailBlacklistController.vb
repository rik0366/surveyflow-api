Imports System.Net
Imports System.Web.Http
Imports Newtonsoft.Json.Linq

' Per-owner opt-out list. The email-send paths (sendSurveyEmail,
' sendScheduledReminders) check this table before delivering anything.
'
' Auth: all endpoints require login. The blacklist is implicitly scoped
' to the caller — `IsBlacklisted` filters by `owner_user_email` so admins
' can only check / manipulate their own list. (The List endpoint will
' return rows for any owner if the caller passes a filter, which is fine
' for now since SurveyFlow has no organization-level sharing yet; revisit
' if/when multiple admins share an owner identity.)
Public Class EmailBlacklistController
    Inherits ApiController

    Private Shared ReadOnly Repo As New JsonRepository(EntityRegistry.EmailBlacklist)

    <HttpGet, Route("EmailBlacklist")>
    Public Function List(Optional filter As String = "", Optional sort As String = "", Optional limit As Integer? = Nothing) As JArray
        Dim filterObj As JObject = Nothing
        If Not String.IsNullOrEmpty(filter) Then filterObj = JObject.Parse(filter)
        Return Repo.Find(filterObj, sort, limit)
    End Function

    <HttpGet, Route("EmailBlacklist/{id}")>
    Public Function GetOne(id As String) As IHttpActionResult
        Dim row = Repo.GetById(id)
        If row Is Nothing Then Return NotFound()
        Return Ok(row)
    End Function

    ' Add a (owner, email) entry. owner_user_email defaults to the caller
    ' so admins can't accidentally blacklist someone on another owner's
    ' behalf via the UI. opted_out_at defaults to now.
    <HttpPost, Route("EmailBlacklist")>
    Public Function Create(<FromBody> data As JObject) As IHttpActionResult
        If data Is Nothing Then data = New JObject()
        Dim caller = CurrentUser.Email()
        If String.IsNullOrEmpty(data.Value(Of String)("owner_user_email")) Then
            data("owner_user_email") = caller
        End If
        If String.IsNullOrEmpty(data.Value(Of String)("opted_out_at")) Then
            data("opted_out_at") = DateTime.UtcNow.ToString("o")
        End If
        Try
            Dim created = Repo.Create(data, caller)
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
        If Repo.Delete(id) Then Return StatusCode(HttpStatusCode.NoContent)
        Return NotFound()
    End Function

    ' Server-side helper for the email-send paths. Given an owner email
    ' and a list of recipient emails, returns which ones are blacklisted.
    ' Implemented as a single round-trip rather than N filter() calls.
    '
    ' Request body: { "owner_user_email": "...", "emails": ["a","b",...] }
    ' Response:     { "blacklisted": ["a", ...] }
    <HttpPost, Route("EmailBlacklist/check")>
    Public Function Check(<FromBody> data As JObject) As IHttpActionResult
        Dim owner = If(data?.Value(Of String)("owner_user_email"), "")
        Dim emails = TryCast(data?("emails"), JArray)
        If String.IsNullOrEmpty(owner) OrElse emails Is Nothing OrElse emails.Count = 0 Then
            Return Ok(New JObject From {{"blacklisted", New JArray()}})
        End If
        ' Pull every entry for this owner and intersect in-memory. Even
        ' busy customers have at most a few thousand opt-outs, so this is
        ' cheap. If it ever grows, switch to a parameterised IN query.
        Dim filter = New JObject From {{"owner_user_email", owner}}
        Dim rows = Repo.Find(filter, "", 100000)
        Dim listed As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
        For Each r In rows.OfType(Of JObject)()
            Dim e = r.Value(Of String)("email")
            If Not String.IsNullOrEmpty(e) Then listed.Add(e)
        Next
        Dim hits As New JArray
        For Each item As JToken In emails
            Dim s = item.ToString()
            If Not String.IsNullOrEmpty(s) AndAlso listed.Contains(s) Then
                hits.Add(s)
            End If
        Next
        Return Ok(New JObject From {{"blacklisted", hits}})
    End Function
End Class
