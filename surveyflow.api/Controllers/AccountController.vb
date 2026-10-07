Imports System.Data
Imports System.Data.SqlClient
Imports System.Net
Imports System.Text.RegularExpressions
Imports System.Web.Http
Imports Newtonsoft.Json.Linq

' Client accounts, for PeriscopeIQ staff (system admins) only. Each client
' is its own account — surveys, address book, opt-outs and users are kept
' apart; staff switch into a client with the X-Account-Id header.
'
' Routes:
'   GET  /api/admin/accounts            → accounts with survey counts; is_home marks the caller's own account
'   POST /api/admin/accounts            { name }
'   PUT  /api/admin/accounts/{id}       { name?, is_active? }
'   GET  /api/admin/surveys?account_id= → surveys across accounts ("all" or one id), each with
'                                          account_name and completed_responses (dashboard "All" view)
Public Class AccountController
    Inherits ApiController

    <HttpGet, Route("admin/accounts")>
    Public Function List() As JArray
        CurrentUser.RequireSystemAdmin()
        Dim home = CurrentUser.Info().HomeAccountId
        Dim out As New JArray
        Using conn = Db.Open()
            Using cmd = conn.CreateCommand()
                cmd.CommandText = "
                    SELECT a.id, a.name, a.slug, a.is_active, a.created_date,
                           (SELECT COUNT(*) FROM dbo.Surveys s WHERE s.account_id = a.id) AS survey_count,
                           (SELECT COUNT(*) FROM dbo.Surveys s WHERE s.account_id = a.id AND s.status = 'active') AS active_count
                    FROM dbo.Accounts a
                    ORDER BY a.name"
                Using rdr = cmd.ExecuteReader()
                    While rdr.Read()
                        out.Add(New JObject From {
                            {"id", rdr.GetString(0)},
                            {"name", rdr.GetString(1)},
                            {"slug", rdr.GetString(2)},
                            {"is_active", rdr.GetBoolean(3)},
                            {"created_date", rdr.GetDateTime(4)},
                            {"survey_count", rdr.GetInt32(5)},
                            {"active_survey_count", rdr.GetInt32(6)},
                            {"is_home", String.Equals(rdr.GetString(0), home, StringComparison.OrdinalIgnoreCase)}
                        })
                    End While
                End Using
            End Using
        End Using
        Return out
    End Function

    <HttpPost, Route("admin/accounts")>
    Public Function Create(<FromBody> data As JObject) As IHttpActionResult
        CurrentUser.RequireSystemAdmin()
        Dim name = ContactService.Clean(data?.Value(Of String)("name"))
        If name Is Nothing Then Return BadRequest("'name' is required.")
        If name.Length > 200 Then Return BadRequest("Name is too long (200 max).")
        If NameTaken(name, Nothing) Then Return NameConflict(name)

        Dim id = Guid.NewGuid().ToString()
        Using conn = Db.Open()
            Using cmd = conn.CreateCommand()
                cmd.CommandText = "INSERT INTO dbo.Accounts (id, name, slug, created_by) VALUES (@id, @n, @s, @u)"
                cmd.Parameters.Add(New SqlParameter("@id", SqlDbType.Char, 36) With {.Value = id})
                cmd.Parameters.Add(New SqlParameter("@n", SqlDbType.NVarChar, 200) With {.Value = name})
                cmd.Parameters.Add(New SqlParameter("@s", SqlDbType.NVarChar, 100) With {.Value = UniqueSlug(conn, name)})
                cmd.Parameters.Add(New SqlParameter("@u", SqlDbType.NVarChar, 320) With {.Value = CurrentUser.Email()})
                cmd.ExecuteNonQuery()
            End Using
        End Using
        Return Ok(List().OfType(Of JObject)().First(Function(a) a.Value(Of String)("id") = id))
    End Function

    <HttpPut, Route("admin/accounts/{id}")>
    Public Function Update(id As String, <FromBody> data As JObject) As IHttpActionResult
        CurrentUser.RequireSystemAdmin()
        If data Is Nothing Then Return BadRequest("Request body is required.")
        Dim name = If(data("name") Is Nothing, Nothing, ContactService.Clean(data.Value(Of String)("name")))
        If data("name") IsNot Nothing AndAlso name Is Nothing Then Return BadRequest("Name can't be blank.")
        If name IsNot Nothing AndAlso NameTaken(name, id) Then Return NameConflict(name)
        Dim active = data.Value(Of Boolean?)("is_active")
        If active.HasValue AndAlso Not active.Value AndAlso
           String.Equals(id, CurrentUser.Info().HomeAccountId, StringComparison.OrdinalIgnoreCase) Then
            Return BadRequest("You can't deactivate your own account.")
        End If

        Using conn = Db.Open()
            Using cmd = conn.CreateCommand()
                cmd.CommandText = "
                    UPDATE dbo.Accounts
                    SET name = COALESCE(@n, name), is_active = COALESCE(@act, is_active), updated_date = SYSUTCDATETIME()
                    WHERE id = @id"
                cmd.Parameters.Add(New SqlParameter("@id", SqlDbType.Char, 36) With {.Value = id})
                cmd.Parameters.Add(New SqlParameter("@n", SqlDbType.NVarChar, 200) With {.Value = If(CObj(name), DBNull.Value)})
                cmd.Parameters.Add(New SqlParameter("@act", SqlDbType.Bit) With {.Value = If(active.HasValue, CObj(active.Value), DBNull.Value)})
                If cmd.ExecuteNonQuery() = 0 Then Return NotFound()
            End Using
        End Using
        Return Ok(List().OfType(Of JObject)().First(Function(a) a.Value(Of String)("id") = id))
    End Function

    ' Surveys across accounts for the dashboard. account_id = "all" or an id.
    <HttpGet, Route("admin/surveys")>
    Public Function Surveys(Optional account_id As String = "all") As JArray
        CurrentUser.RequireSystemAdmin()
        Dim all = String.IsNullOrEmpty(account_id) OrElse account_id = "all"
        Dim out As New JArray
        Using conn = Db.Open()
            Using cmd = conn.CreateCommand()
                cmd.CommandText = "
                    SELECT s.id, s.title, s.status, s.questions_json, s.created_date, s.created_by, s.updated_date,
                           s.account_id, a.name,
                           (SELECT COUNT(*) FROM dbo.SurveyResponses r WHERE r.survey_id = s.id AND r.status = 'completed') AS completed
                    FROM dbo.Surveys s
                    JOIN dbo.Accounts a ON a.id = s.account_id
                    WHERE (@all = 1 OR s.account_id = @a)
                    ORDER BY s.created_date DESC"
                cmd.Parameters.Add(New SqlParameter("@all", SqlDbType.Bit) With {.Value = all})
                cmd.Parameters.Add(New SqlParameter("@a", SqlDbType.Char, 36) With {.Value = If(all, CObj(DBNull.Value), account_id)})
                Using rdr = cmd.ExecuteReader()
                    While rdr.Read()
                        Dim qCount = 0
                        If Not rdr.IsDBNull(3) Then
                            Dim q = TryCast(JToken.Parse(rdr.GetString(3)), JArray)
                            If q IsNot Nothing Then qCount = q.Count
                        End If
                        out.Add(New JObject From {
                            {"id", rdr.GetString(0)},
                            {"title", rdr.GetString(1)},
                            {"status", rdr.GetString(2)},
                            {"question_count", qCount},
                            {"created_date", rdr.GetDateTime(4)},
                            {"created_by", rdr.GetString(5)},
                            {"updated_date", rdr.GetDateTime(6)},
                            {"account_id", rdr.GetString(7)},
                            {"account_name", rdr.GetString(8)},
                            {"completed_responses", rdr.GetInt32(9)}
                        })
                    End While
                End Using
            End Using
        End Using
        Return out
    End Function


    ' Users across accounts for User Management. account_id = "all" or an id.
    <HttpGet, Route("admin/users")>
    Public Function Users(Optional account_id As String = "all") As JArray
        CurrentUser.RequireSystemAdmin()
        Dim all = String.IsNullOrEmpty(account_id) OrElse account_id = "all"
        Dim out As New JArray
        Using conn = Db.Open()
            Using cmd = conn.CreateCommand()
                cmd.CommandText = "
                    SELECT u.id, u.email, u.full_name, u.role, u.is_active, u.is_system_admin, u.created_date,
                           u.account_id, a.name, CASE WHEN u.password_hash IS NULL THEN 1 ELSE 0 END AS invite_pending
                    FROM dbo.Users u
                    LEFT JOIN dbo.Accounts a ON a.id = u.account_id
                    WHERE (@all = 1 OR u.account_id = @a)
                    ORDER BY a.name, u.email"
                cmd.Parameters.Add(New SqlParameter("@all", SqlDbType.Bit) With {.Value = all})
                cmd.Parameters.Add(New SqlParameter("@a", SqlDbType.Char, 36) With {.Value = If(all, CObj(DBNull.Value), account_id)})
                Using rdr = cmd.ExecuteReader()
                    While rdr.Read()
                        out.Add(New JObject From {
                            {"id", rdr.GetString(0)},
                            {"email", rdr.GetString(1)},
                            {"full_name", If(rdr.IsDBNull(2), Nothing, rdr.GetString(2))},
                            {"role", rdr.GetString(3)},
                            {"is_active", rdr.GetBoolean(4)},
                            {"is_system_admin", rdr.GetBoolean(5)},
                            {"created_date", rdr.GetDateTime(6)},
                            {"account_id", If(rdr.IsDBNull(7), Nothing, rdr.GetString(7))},
                            {"account_name", If(rdr.IsDBNull(8), Nothing, rdr.GetString(8))},
                            {"invite_pending", rdr.GetInt32(9) = 1}
                        })
                    End While
                End Using
            End Using
        End Using
        Return out
    End Function

    ' Move a user to another client account. Their surveys etc. stay where
    ' they are (those belong to the account, not the person).
    <HttpPut, Route("admin/users/{id}/account")>
    Public Function MoveUser(id As String, <FromBody> data As JObject) As IHttpActionResult
        CurrentUser.RequireSystemAdmin()
        Dim target = ContactService.Clean(data?.Value(Of String)("account_id"))
        If target Is Nothing Then Return BadRequest("'account_id' is required.")
        If String.Equals(id, CurrentUser.Info().Id, StringComparison.OrdinalIgnoreCase) Then Return BadRequest("You can't move yourself.")
        Using conn = Db.Open()
            Using cmd = conn.CreateCommand()
                cmd.CommandText = "SELECT COUNT(*) FROM dbo.Accounts WHERE id = @a AND is_active = 1"
                cmd.Parameters.Add(New SqlParameter("@a", SqlDbType.Char, 36) With {.Value = target})
                If CInt(cmd.ExecuteScalar()) = 0 Then Return BadRequest("Unknown or inactive client.")
            End Using
            Using cmd = conn.CreateCommand()
                ' Someone moved out of PeriscopeIQ must not keep staff powers.
                cmd.CommandText = "
                    UPDATE dbo.Users SET account_id = @a, is_system_admin = 0, updated_date = SYSUTCDATETIME()
                    WHERE id = @id"
                cmd.Parameters.Add(New SqlParameter("@a", SqlDbType.Char, 36) With {.Value = target})
                cmd.Parameters.Add(New SqlParameter("@id", SqlDbType.Char, 36) With {.Value = id})
                If cmd.ExecuteNonQuery() = 0 Then Return NotFound()
            End Using
        End Using
        Return Ok(Users(target).OfType(Of JObject)().First(Function(u) u.Value(Of String)("id") = id))
    End Function


    ' --- helpers ----------------------------------------------------------

    Private Function NameConflict(name As String) As IHttpActionResult
        Return Content(HttpStatusCode.Conflict, New JObject From {
            {"reason", "duplicate"}, {"message", $"A client named '{name}' already exists."}
        })
    End Function

    Private Shared Function NameTaken(name As String, exceptId As String) As Boolean
        Using conn = Db.Open()
            Using cmd = conn.CreateCommand()
                cmd.CommandText = "SELECT COUNT(*) FROM dbo.Accounts WHERE name = @n AND (@x IS NULL OR id <> @x)"
                cmd.Parameters.Add(New SqlParameter("@n", SqlDbType.NVarChar, 200) With {.Value = name})
                cmd.Parameters.Add(New SqlParameter("@x", SqlDbType.Char, 36) With {.Value = If(CObj(exceptId), DBNull.Value)})
                Return CInt(cmd.ExecuteScalar()) > 0
            End Using
        End Using
    End Function

    ' "Healthcare Council" → "healthcare-council" (suffixed -2, -3… if taken).
    Private Shared Function UniqueSlug(conn As SqlConnection, name As String) As String
        Dim baseSlug = Regex.Replace(name.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim("-"c)
        If baseSlug.Length = 0 Then baseSlug = "client"
        If baseSlug.Length > 90 Then baseSlug = baseSlug.Substring(0, 90).TrimEnd("-"c)
        Dim slug = baseSlug
        Dim n = 1
        Do
            Using cmd = conn.CreateCommand()
                cmd.CommandText = "SELECT COUNT(*) FROM dbo.Accounts WHERE slug = @s"
                cmd.Parameters.Add(New SqlParameter("@s", SqlDbType.NVarChar, 100) With {.Value = slug})
                If CInt(cmd.ExecuteScalar()) = 0 Then Return slug
            End Using
            n += 1
            slug = $"{baseSlug}-{n}"
        Loop
    End Function

End Class
