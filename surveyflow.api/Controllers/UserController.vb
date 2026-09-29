Imports System.Data
Imports System.Data.SqlClient
Imports System.Net
Imports System.Web.Http
Imports Newtonsoft.Json.Linq

' Admin-facing User CRUD. password_hash is never read or written through here
' — those operations live in AuthController. POST is intentionally absent;
' new users are created via /api/auth/seed-admin or future invite flows.
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
        Return Repo.Find(filterObj, sort, limit)
    End Function

    <HttpGet, Route("User/{id}")>
    Public Function GetOne(id As String) As IHttpActionResult
        Dim row = Repo.GetById(id)
        If row Is Nothing Then Return NotFound()
        Return Ok(row)
    End Function

    <HttpPut, Route("User/{id}")>
    Public Function Update(id As String, <FromBody> data As JObject) As IHttpActionResult
        Dim row = Repo.Update(id, data)
        If row Is Nothing Then Return NotFound()
        Return Ok(row)
    End Function

    ' Delete with two guards:
    '   1. Can't delete yourself (use Logout if you want to leave).
    '   2. Can't delete the last admin (would lock everyone out — the
    '      seed-admin endpoint only works on a fully empty Users table).
    <HttpDelete, Route("User/{id}")>
    Public Function Delete(id As String) As IHttpActionResult
        Dim target = Repo.GetById(id)
        If target Is Nothing Then Return NotFound()

        Dim callerEmail = If(System.Web.HttpContext.Current?.User?.Identity?.Name, "")
        If String.Equals(target.Value(Of String)("email"), callerEmail, StringComparison.OrdinalIgnoreCase) Then
            Return Content(HttpStatusCode.Conflict, New JObject From {
                {"status", "error"},
                {"reason", "self_delete"},
                {"message", "You can't delete your own account."}
            })
        End If

        Dim targetRole = target.Value(Of String)("role")
        If String.Equals(targetRole, "admin", StringComparison.OrdinalIgnoreCase) Then
            If CountActiveAdmins() <= 1 Then
                Return Content(HttpStatusCode.Conflict, New JObject From {
                    {"status", "error"},
                    {"reason", "last_admin"},
                    {"message", "Can't delete the last admin. Promote another user to admin first."}
                })
            End If
        End If

        If Repo.Delete(id) Then Return StatusCode(HttpStatusCode.NoContent)
        Return NotFound()
    End Function

    Private Shared Function CountActiveAdmins() As Integer
        Using conn = Db.Open()
            Using cmd = conn.CreateCommand()
                cmd.CommandText = "SELECT COUNT(*) FROM dbo.Users WHERE role = 'admin' AND is_active = 1"
                Return CInt(cmd.ExecuteScalar())
            End Using
        End Using
    End Function

End Class
