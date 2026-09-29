Imports System.Net
Imports System.Web.Http
Imports Newtonsoft.Json.Linq

' Notes on a contact (legacy Address_Book_Notes). 'system' notes are the
' automatic history written by ContactService and can't be edited or
' deleted; 'note' entries are written by people.
'
' Routes:
'   GET    /api/ContactNote?contact_id=      newest first
'   POST   /api/ContactNote                  { contact_id, note, survey_id? }
'   DELETE /api/ContactNote/{id}             author or account admin; not system notes
Public Class ContactNoteController
    Inherits ApiController

    Private Shared ReadOnly Repo As New JsonRepository(EntityRegistry.ContactNote)
    Private Shared ReadOnly ContactRepo As New JsonRepository(EntityRegistry.Contact)

    <HttpGet, Route("ContactNote")>
    Public Function List(contact_id As String) As JArray
        Return Repo.Find(New JObject From {{"contact_id", contact_id}}, "-created_date", Nothing, CurrentUser.Scope())
    End Function

    <HttpPost, Route("ContactNote")>
    Public Function Create(<FromBody> data As JObject) As IHttpActionResult
        Dim contactId = ContactService.Clean(data?.Value(Of String)("contact_id"))
        Dim note = ContactService.Clean(data?.Value(Of String)("note"))
        If contactId Is Nothing OrElse note Is Nothing Then Return BadRequest("'contact_id' and 'note' are required.")
        If ContactRepo.GetById(contactId, CurrentUser.Scope()) Is Nothing Then Return NotFound()

        Dim surveyId = ContactService.Clean(data.Value(Of String)("survey_id"))
        If surveyId IsNot Nothing AndAlso Not Ownership.SurveyInCallerAccount(surveyId) Then Return BadRequest("Unknown survey.")

        Dim row As New JObject From {{"contact_id", contactId}, {"note", note}, {"note_type", "note"}}
        If surveyId IsNot Nothing Then row("survey_id") = surveyId
        Return Ok(Repo.Create(row, CurrentUser.Email(), CurrentUser.Scope()))
    End Function

    <HttpDelete, Route("ContactNote/{id}")>
    Public Function Delete(id As String) As IHttpActionResult
        Dim row = Repo.GetById(id, CurrentUser.Scope())
        If row Is Nothing Then Return NotFound()
        If row.Value(Of String)("note_type") = "system" Then Return BadRequest("History entries can't be deleted.")
        Dim isAuthor = String.Equals(row.Value(Of String)("created_by"), CurrentUser.Email(), StringComparison.OrdinalIgnoreCase)
        If Not isAuthor Then CurrentUser.RequireAccountAdmin()
        Repo.Delete(id, CurrentUser.Scope())
        Return StatusCode(HttpStatusCode.NoContent)
    End Function

End Class
