Imports System.Net
Imports System.Web.Http
Imports Newtonsoft.Json.Linq

' Surveys, shared by everyone in the caller's account.
Public Class SurveyController
    Inherits ApiController

    Private Shared ReadOnly Repo As New JsonRepository(EntityRegistry.Survey)

    <HttpGet, Route("Survey")>
    Public Function List(Optional filter As String = "", Optional sort As String = "", Optional limit As Integer? = Nothing) As JArray
        Dim filterObj As JObject = Nothing
        If Not String.IsNullOrEmpty(filter) Then filterObj = JObject.Parse(filter)
        Return Repo.Find(filterObj, sort, limit, CurrentUser.Scope())
    End Function

    ' AllowAnonymous + unscoped: TakeSurvey loads the survey definition by id
    ' without the respondent being logged in (and a logged-in user may open
    ' another account's survey link). The survey id is a GUID so it isn't
    ' guessable; harvest risk is low. Tighten with token-bound loading.
    ' Writes below stay account-scoped.
    <HttpGet, Route("Survey/{id}"), AllowAnonymous>
    Public Function GetOne(id As String) As IHttpActionResult
        Dim row = Repo.GetById(id, RepoScope.Unscoped)
        If row Is Nothing Then Return NotFound()
        ' Client branding for the survey pages and invite emails.
        row("account_logo_url") = AccountLogoController.LogoUrlFor(row.Value(Of String)("account_id"))
        Return Ok(row)
    End Function

    <HttpPost, Route("Survey")>
    Public Function Create(<FromBody> data As JObject) As JObject
        Return Repo.Create(data, CurrentUser.Email(), CurrentUser.Scope())
    End Function

    <HttpPut, Route("Survey/{id}")>
    Public Function Update(id As String, <FromBody> data As JObject) As IHttpActionResult
        Dim row = Repo.Update(id, data, CurrentUser.Scope())
        If row Is Nothing Then Return NotFound()
        Return Ok(row)
    End Function

    <HttpDelete, Route("Survey/{id}")>
    Public Function Delete(id As String) As IHttpActionResult
        If Repo.Delete(id, CurrentUser.Scope()) Then Return StatusCode(HttpStatusCode.NoContent)
        Return NotFound()
    End Function

    <HttpPost, Route("Survey/bulk")>
    Public Function Bulk(<FromBody> items As JArray) As JArray
        Return Repo.BulkCreate(items, CurrentUser.Email(), CurrentUser.Scope())
    End Function
End Class
