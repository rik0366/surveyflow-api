Imports System.Net
Imports System.Web.Http
Imports Newtonsoft.Json.Linq

' Saved questions, shared by everyone in the caller's account.
Public Class QuestionLibraryController
    Inherits ApiController

    Private Shared ReadOnly Repo As New JsonRepository(EntityRegistry.QuestionLibrary)

    <HttpGet, Route("QuestionLibrary")>
    Public Function List(Optional filter As String = "", Optional sort As String = "", Optional limit As Integer? = Nothing) As JArray
        Dim filterObj As JObject = Nothing
        If Not String.IsNullOrEmpty(filter) Then filterObj = JObject.Parse(filter)
        Return Repo.Find(filterObj, sort, limit, CurrentUser.Scope())
    End Function

    <HttpGet, Route("QuestionLibrary/{id}")>
    Public Function GetOne(id As String) As IHttpActionResult
        Dim row = Repo.GetById(id, CurrentUser.Scope())
        If row Is Nothing Then Return NotFound()
        Return Ok(row)
    End Function

    <HttpPost, Route("QuestionLibrary")>
    Public Function Create(<FromBody> data As JObject) As JObject
        Return Repo.Create(data, CurrentUser.Email(), CurrentUser.Scope())
    End Function

    <HttpPut, Route("QuestionLibrary/{id}")>
    Public Function Update(id As String, <FromBody> data As JObject) As IHttpActionResult
        Dim row = Repo.Update(id, data, CurrentUser.Scope())
        If row Is Nothing Then Return NotFound()
        Return Ok(row)
    End Function

    <HttpDelete, Route("QuestionLibrary/{id}")>
    Public Function Delete(id As String) As IHttpActionResult
        If Repo.Delete(id, CurrentUser.Scope()) Then Return StatusCode(HttpStatusCode.NoContent)
        Return NotFound()
    End Function

    <HttpPost, Route("QuestionLibrary/bulk")>
    Public Function Bulk(<FromBody> items As JArray) As JArray
        Return Repo.BulkCreate(items, CurrentUser.Email(), CurrentUser.Scope())
    End Function
End Class
