Imports System.Data.SqlClient
Imports System.Net
Imports System.Text.RegularExpressions
Imports System.Web.Http
Imports Newtonsoft.Json.Linq

' Custom contact field definitions (legacy ExtRespondentDataFields), one set
' per account. Values live on Contacts.custom_json keyed by field_key, which
' is fixed once created (it's referenced by stored values and merge tags
' {{custom.<field_key>}}). Anyone in the account can read them; changes
' need an account admin.
'
' Routes:
'   GET    /api/ContactField?include_inactive=
'   POST   /api/ContactField   { label, field_type?, field_key?, options?, survey_ids?, sort_order? }
'   PUT    /api/ContactField/{id}
'   DELETE /api/ContactField/{id}   deactivate (stored values are kept)
Public Class ContactFieldController
    Inherits ApiController

    Private Shared ReadOnly Repo As New JsonRepository(EntityRegistry.ContactField)
    Private Shared ReadOnly Types As New HashSet(Of String)({"text", "number", "date", "choice"})
    Private Shared ReadOnly KeyPattern As New Regex("^[a-z][a-z0-9_]{0,99}$")

    <HttpGet, Route("ContactField")>
    Public Function List(Optional include_inactive As Boolean = False) As JArray
        Dim filter = If(include_inactive, Nothing, New JObject From {{"is_active", True}})
        Return Repo.Find(filter, "sort_order", Nothing, CurrentUser.Scope())
    End Function

    <HttpPost, Route("ContactField")>
    Public Function Create(<FromBody> data As JObject) As IHttpActionResult
        CurrentUser.RequireAccountAdmin()
        If data Is Nothing Then Return BadRequest("Request body is required.")
        Dim label = ContactService.Clean(data.Value(Of String)("label"))
        If label Is Nothing Then Return BadRequest("'label' is required.")
        data("label") = label

        Dim key = ContactService.Clean(data.Value(Of String)("field_key"))
        If key Is Nothing Then key = KeyFromLabel(label)
        If Not KeyPattern.IsMatch(key) Then Return BadRequest("'field_key' must start with a letter and use only a-z, 0-9 and _.")
        data("field_key") = key
        If data("field_type") Is Nothing Then data("field_type") = "text"
        data.Remove("is_active")

        Dim bad = Validate(data)
        If bad IsNot Nothing Then Return BadRequest(bad)
        Try
            Return Ok(Repo.Create(data, CurrentUser.Email(), CurrentUser.Scope()))
        Catch ex As SqlException When ContactGroupController.IsUniqueViolation(ex)
            Return Content(HttpStatusCode.Conflict, New JObject From {
                {"reason", "duplicate"}, {"message", $"A field with key '{key}' already exists."}
            })
        End Try
    End Function

    <HttpPut, Route("ContactField/{id}")>
    Public Function Update(id As String, <FromBody> data As JObject) As IHttpActionResult
        CurrentUser.RequireAccountAdmin()
        If data Is Nothing Then Return BadRequest("Request body is required.")
        Dim current = Repo.GetById(id, CurrentUser.Scope())
        If current Is Nothing Then Return NotFound()
        If data("field_key") IsNot Nothing AndAlso data.Value(Of String)("field_key") <> current.Value(Of String)("field_key") Then
            Return BadRequest("'field_key' can't be changed once created.")
        End If
        If data("label") IsNot Nothing AndAlso ContactService.Clean(data.Value(Of String)("label")) Is Nothing Then
            Return BadRequest("'label' can't be blank.")
        End If

        ' Validate the field as it will look after the update.
        Dim merged = CType(current.DeepClone(), JObject)
        merged.Merge(data)
        Dim bad = Validate(merged)
        If bad IsNot Nothing Then Return BadRequest(bad)
        Return Ok(Repo.Update(id, data, CurrentUser.Scope()))
    End Function

    <HttpDelete, Route("ContactField/{id}")>
    Public Function Delete(id As String) As IHttpActionResult
        CurrentUser.RequireAccountAdmin()
        If Repo.Update(id, New JObject From {{"is_active", False}}, CurrentUser.Scope()) Is Nothing Then Return NotFound()
        Return StatusCode(HttpStatusCode.NoContent)
    End Function

    Private Shared Function Validate(f As JObject) As String
        Dim t = f.Value(Of String)("field_type")
        If Not Types.Contains(t) Then Return "'field_type' must be text, number, date or choice."
        If t = "choice" Then
            Dim opts = TryCast(f("options"), JArray)
            If opts Is Nothing OrElse opts.Count = 0 Then Return "A choice field needs at least one option."
            If opts.Any(Function(o) ContactService.Clean(o.ToString()) Is Nothing) Then Return "Options can't be blank."
        End If
        Dim surveys = TryCast(f("survey_ids"), JArray)
        If surveys IsNot Nothing Then
            For Each s In surveys
                If Not Ownership.SurveyInCallerAccount(s.ToString()) Then Return "Unknown survey in 'survey_ids'."
            Next
        End If
        Return Nothing
    End Function

    ' "Years of Service (Total)" → "years_of_service_total"
    Private Shared Function KeyFromLabel(label As String) As String
        Dim k = Regex.Replace(label.ToLowerInvariant(), "[^a-z0-9]+", "_").Trim("_"c)
        If k.Length = 0 OrElse Not Char.IsLetter(k(0)) Then k = "field_" & k
        Return If(k.Length > 100, k.Substring(0, 100).TrimEnd("_"c), k)
    End Function

End Class
