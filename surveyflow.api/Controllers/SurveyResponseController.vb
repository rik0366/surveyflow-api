Imports System.Net
Imports System.Web.Http
Imports Newtonsoft.Json.Linq

Public Class SurveyResponseController
    Inherits ApiController

    Private Shared ReadOnly Repo As New JsonRepository(EntityRegistry.SurveyResponse)

    ' AllowAnonymous: TakeSurvey filters by invite_id to resume an in-progress
    ' response. Locking down would break the resume flow.
    <HttpGet, Route("SurveyResponse"), AllowAnonymous>
    Public Function List(Optional filter As String = "", Optional sort As String = "", Optional limit As Integer? = Nothing) As JArray
        Dim filterObj As JObject = Nothing
        If Not String.IsNullOrEmpty(filter) Then filterObj = JObject.Parse(filter)
        Return Repo.Find(filterObj, sort, limit)
    End Function

    <HttpGet, Route("SurveyResponse/{id}")>
    Public Function GetOne(id As String) As IHttpActionResult
        Dim row = Repo.GetById(id)
        If row Is Nothing Then Return NotFound()
        Return Ok(row)
    End Function

    ' AllowAnonymous: respondents are (intentionally) not logged in.
    <HttpPost, Route("SurveyResponse"), AllowAnonymous>
    Public Function Create(<FromBody> data As JObject) As JObject
        Return Repo.Create(data, CurrentUser.Email())
    End Function

    ' AllowAnonymous: progressive saves while taking the survey.
    <HttpPut, Route("SurveyResponse/{id}"), AllowAnonymous>
    Public Function Update(id As String, <FromBody> data As JObject) As IHttpActionResult
        Dim row = Repo.Update(id, data)
        If row Is Nothing Then Return NotFound()
        Return Ok(row)
    End Function

    <HttpDelete, Route("SurveyResponse/{id}")>
    Public Function Delete(id As String) As IHttpActionResult
        If Repo.Delete(id) Then Return StatusCode(HttpStatusCode.NoContent)
        Return NotFound()
    End Function

    <HttpPost, Route("SurveyResponse/bulk")>
    Public Function Bulk(<FromBody> items As JArray) As JArray
        Return Repo.BulkCreate(items, CurrentUser.Email())
    End Function
End Class
