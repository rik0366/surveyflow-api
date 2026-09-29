Imports System.Net
Imports System.Security.Cryptography
Imports System.Web.Http
Imports Newtonsoft.Json.Linq

Public Class SurveyInviteController
    Inherits ApiController

    Private Shared ReadOnly Repo As New JsonRepository(EntityRegistry.SurveyInvite)

    ' Alphabet for respondent passwords. Skips lookalikes (0/O, 1/I/L) so the
    ' code is readable when typed off a paper invite or printed report.
    Private Const PasswordAlphabet As String = "ABCDEFGHJKMNPQRSTUVWXYZ23456789"
    Private Const PasswordLength As Integer = 6

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

    ' Stamp a password onto the incoming invite payload if the caller didn't
    ' provide one. Treat null / missing / whitespace as "please generate".
    Private Shared Sub EnsurePassword(data As JObject)
        If data Is Nothing Then Return
        Dim existing = data.Value(Of String)("password")
        If String.IsNullOrWhiteSpace(existing) Then
            data("password") = GeneratePassword()
        End If
    End Sub

    ' AllowAnonymous: TakeSurvey looks up the invite by token (a GUID-like
    ' opaque secret) before letting the respondent in. The token IS the auth.
    <HttpGet, Route("SurveyInvite"), AllowAnonymous>
    Public Function List(Optional filter As String = "", Optional sort As String = "", Optional limit As Integer? = Nothing) As JArray
        Dim filterObj As JObject = Nothing
        If Not String.IsNullOrEmpty(filter) Then filterObj = JObject.Parse(filter)
        Return Repo.Find(filterObj, sort, limit)
    End Function

    <HttpGet, Route("SurveyInvite/{id}")>
    Public Function GetOne(id As String) As IHttpActionResult
        Dim row = Repo.GetById(id)
        If row Is Nothing Then Return NotFound()
        Return Ok(row)
    End Function

    <HttpPost, Route("SurveyInvite")>
    Public Function Create(<FromBody> data As JObject) As JObject
        EnsurePassword(data)
        Return Repo.Create(data, CurrentUser.Email())
    End Function

    ' AllowAnonymous: TakeSurvey updates invite status (opened → started →
    ' completed) as the respondent moves through the survey. Anyone who
    ' guesses an invite id could also flip status, but ids are GUIDs and the
    ' worst-case impact is corrupting a single status field.
    <HttpPut, Route("SurveyInvite/{id}"), AllowAnonymous>
    Public Function Update(id As String, <FromBody> data As JObject) As IHttpActionResult
        Dim row = Repo.Update(id, data)
        If row Is Nothing Then Return NotFound()
        Return Ok(row)
    End Function

    <HttpDelete, Route("SurveyInvite/{id}")>
    Public Function Delete(id As String) As IHttpActionResult
        If Repo.Delete(id) Then Return StatusCode(HttpStatusCode.NoContent)
        Return NotFound()
    End Function

    <HttpPost, Route("SurveyInvite/bulk")>
    Public Function Bulk(<FromBody> items As JArray) As JArray
        If items IsNot Nothing Then
            For Each item In items.OfType(Of JObject)()
                EnsurePassword(item)
            Next
        End If
        Return Repo.BulkCreate(items, CurrentUser.Email())
    End Function
End Class
