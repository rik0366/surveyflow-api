Imports System.Data
Imports System.Data.SqlClient
Imports System.Net
Imports System.Web.Http
Imports Newtonsoft.Json.Linq

' User CRUD within the caller's account. password_hash is never read or
' written through here — those operations live in AuthController. POST is
' intentionally absent; new users are created via /api/auth/invite-user.
' Changes (PUT/DELETE) require an account admin.
'
' Routes:
'   GET    /api/User?filter=...&sort=...&limit=N
'   GET    /api/User/{id}
'   PUT    /api/User/{id}
'   DELETE /api/User/{id}
Public Class UserController
    Inherits ApiController

    Private Shared ReadOnly Repo As New JsonRepository(EntityRegistry.User)

    <HttpGet, Route("User")>
    Public Function List(Optional filter As String = "", Optional sort As String = "", Optional limit As Integer? = Nothing) As JArray
        Dim filterObj As JObject = Nothing
        If Not String.IsNullOrEmpty(filter) Then filterObj = JObject.Parse(filter)
        Return Repo.Find(filterObj, sort, limit, CurrentUser.Scope())
    End Function

    <HttpGet, Route("User/{id}")>
    Public Function GetOne(id As String) As IHttpActionResult
        Dim row = Repo.GetById(id, CurrentUser.Scope())
        If row Is Nothing Then Return NotFound()
        Return Ok(row)
    End Function

    <HttpPut, Route("User/{id}")>
    Public Function Update(id As String, <FromBody> data As JObject) As IHttpActionResult
        CurrentUser.RequireAccountAdmin()
        Dim target = Repo.GetById(id, CurrentUser.Scope())
        If target Is Nothing Then Return NotFound()

        ' Demoting / deactivating the account's last admin would leave nobody
        ' able to manage its users.
        Dim losingAdmin = String.Equals(target.Value(Of String)("role"), "admin", StringComparison.OrdinalIgnoreCase) AndAlso
            ((data?("role") IsNot Nothing AndAlso Not String.Equals(data.Value(Of String)("role"), "admin", StringComparison.OrdinalIgnoreCase)) OrElse
             (data?("is_active") IsNot Nothing AndAlso Not data.Value(Of Boolean)("is_active")))
        If losingAdmin AndAlso CountActiveAdmins(CurrentUser.AccountId()) <= 1 Then
            Return LastAdminConflict()
        End If

        Return Ok(Repo.Update(id, data, CurrentUser.Scope()))
    End Function

    ' Delete with two guards:
    '   1. Can't delete yourself (use Logout if you want to leave).
    '   2. Can't delete the account's last admin (would leave nobody able to
    '      manage the account's users).
    <HttpDelete, Route("User/{id}")>
    Public Function Delete(id As String) As IHttpActionResult
        CurrentUser.RequireAccountAdmin()
        Dim target = Repo.GetById(id, CurrentUser.Scope())
        If target Is Nothing Then Return NotFound()

        If String.Equals(target.Value(Of String)("email"), CurrentUser.Email(), StringComparison.OrdinalIgnoreCase) Then
            Return Content(HttpStatusCode.Conflict, New JObject From {
                {"status", "error"},
                {"reason", "self_delete"},
                {"message", "You can't delete your own account."}
            })
        End If

        Dim targetRole = target.Value(Of String)("role")
        If String.Equals(targetRole, "admin", StringComparison.OrdinalIgnoreCase) Then
            If CountActiveAdmins(CurrentUser.AccountId()) <= 1 Then Return LastAdminConflict()
        End If

        If Repo.Delete(id, CurrentUser.Scope()) Then Return StatusCode(HttpStatusCode.NoContent)
        Return NotFound()
    End Function

    Private Function LastAdminConflict() As IHttpActionResult
        Return Content(HttpStatusCode.Conflict, New JObject From {
            {"status", "error"},
            {"reason", "last_admin"},
            {"message", "Can't remove the last admin. Promote another user to admin first."}
        })
    End Function

    Private Shared Function CountActiveAdmins(accountId As String) As Integer
        Using conn = Db.Open()
            Using cmd = conn.CreateCommand()
                cmd.CommandText = "SELECT COUNT(*) FROM dbo.Users WHERE role = 'admin' AND is_active = 1 AND account_id = @a"
                cmd.Parameters.Add(New SqlParameter("@a", SqlDbType.Char, 36) With {.Value = accountId})
                Return CInt(cmd.ExecuteScalar())
            End Using
        End Using
    End Function

End Class
