Imports System.Net
Imports System.Web.Http
Imports Newtonsoft.Json.Linq

' REST endpoints for MailingList entity. Mirrors the Base44 SDK's
' entities.MailingList.{list, get, filter, create, update, delete, bulkCreate}.
' All calls are limited to the caller's account.
'
' Routes:
'   GET    /api/MailingList?filter=...&sort=...&limit=N
'   GET    /api/MailingList/{id}
'   POST   /api/MailingList
'   PUT    /api/MailingList/{id}
'   DELETE /api/MailingList/{id}
'   POST   /api/MailingList/bulk
'
' "filter" is a URL-encoded JSON object. e.g.
'   GET /api/MailingList?filter=%7B%22created_by%22%3A%22me%40x.com%22%7D
Public Class MailingListController
    Inherits ApiController

    Private Shared ReadOnly Repo As New JsonRepository(EntityRegistry.MailingList)


    <HttpGet, Route("MailingList")>
    Public Function List(Optional filter As String = "", Optional sort As String = "", Optional limit As Integer? = Nothing) As JArray
        Dim filterObj As JObject = Nothing
        If Not String.IsNullOrEmpty(filter) Then
            filterObj = JObject.Parse(filter)
        End If
        Return Repo.Find(filterObj, sort, limit, CurrentUser.Scope())
    End Function

    <HttpGet, Route("MailingList/{id}")>
    Public Function GetOne(id As String) As IHttpActionResult
        Dim row = Repo.GetById(id, CurrentUser.Scope())
        If row Is Nothing Then Return NotFound()
        Return Ok(row)
    End Function

    <HttpPost, Route("MailingList")>
    Public Function Create(<FromBody> data As JObject) As JObject
        Return Repo.Create(data, CurrentUser.Email(), CurrentUser.Scope())
    End Function

    <HttpPut, Route("MailingList/{id}")>
    Public Function Update(id As String, <FromBody> data As JObject) As IHttpActionResult
        Dim row = Repo.Update(id, data, CurrentUser.Scope())
        If row Is Nothing Then Return NotFound()
        Return Ok(row)
    End Function

    <HttpDelete, Route("MailingList/{id}")>
    Public Function Delete(id As String) As IHttpActionResult
        If Repo.Delete(id, CurrentUser.Scope()) Then
            Return StatusCode(HttpStatusCode.NoContent)
        End If
        Return NotFound()
    End Function

    <HttpPost, Route("MailingList/bulk")>
    Public Function Bulk(<FromBody> items As JArray) As JArray
        Return Repo.BulkCreate(items, CurrentUser.Email(), CurrentUser.Scope())
    End Function

End Class
