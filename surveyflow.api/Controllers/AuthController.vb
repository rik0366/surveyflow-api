Imports System.Configuration
Imports System.Data
Imports System.Data.SqlClient
Imports System.Net
Imports System.Threading.Tasks
Imports System.Web
Imports System.Web.Http
Imports System.Web.Security
Imports Newtonsoft.Json.Linq

' Authentication endpoints. Mirrors the Base44 SDK auth surface
' (auth.me / auth.login / auth.logout) plus a one-time seed-admin endpoint
' for bootstrapping the very first admin user.
'
' All routes:
'   POST /api/auth/login         — email/password → set forms cookie
'   POST /api/auth/logout        — clear forms cookie
'   GET  /api/auth/me            — current user (or 401)
'   POST /api/auth/seed-admin    — create first admin (only when zero users exist)
'   POST /api/auth/invite-user   — admin only; creates user with invite token + emails them
'   POST /api/auth/set-password  — anonymous; accepts {token, password}, sets hash, auto-login
Public Class AuthController
    Inherits ApiController


    <HttpPost, Route("auth/login"), AllowAnonymous>
    Public Function Login(<FromBody> req As LoginRequest) As IHttpActionResult
        If req Is Nothing OrElse String.IsNullOrEmpty(req.Email) OrElse String.IsNullOrEmpty(req.Password) Then
            Return BadRequest("email and password are required.")
        End If

        Dim row = LookupUser(req.Email)
        If row Is Nothing Then Return Unauthorized()

        Dim isActive = row.Value(Of Boolean?)("is_active")
        If isActive.HasValue AndAlso Not isActive.Value Then Return Unauthorized()

        Dim hash = row.Value(Of String)("password_hash")
        If Not PasswordHasher.Verify(req.Password, hash) Then Return Unauthorized()

        FormsAuthentication.SetAuthCookie(req.Email, createPersistentCookie:=False)
        row.Remove("password_hash")
        Return Ok(row)
    End Function


    <HttpPost, Route("auth/logout")>
    <AllowAnonymous>
    Public Function Logout() As IHttpActionResult
        FormsAuthentication.SignOut()
        Return Ok(New With {.status = "ok"})
    End Function


    <HttpGet, Route("auth/me")>
    Public Function Me_() As IHttpActionResult
        Dim email = If(System.Web.HttpContext.Current?.User?.Identity?.Name, "")
        If String.IsNullOrEmpty(email) Then Return Unauthorized()

        Dim row = LookupUser(email)
        If row Is Nothing Then Return Unauthorized()

        row.Remove("password_hash")
        Return Ok(row)
    End Function


    ' --- Self-service profile + password change ---------------------------
    ' Authenticated user updates their own profile. Currently scoped to
    ' full_name only — email is the auth identifier and shouldn't change here.
    <HttpPut, Route("auth/me")>
    Public Function UpdateMe(<FromBody> req As JObject) As IHttpActionResult
        Dim email = If(HttpContext.Current?.User?.Identity?.Name, "")
        If String.IsNullOrEmpty(email) Then Return Unauthorized()

        Dim newName = If(req?.Value(Of String)("full_name"), "").Trim()

        Using conn = Db.Open()
            Using cmd = conn.CreateCommand()
                cmd.CommandText = "
                    UPDATE dbo.Users
                    SET full_name = @fn, updated_date = SYSUTCDATETIME()
                    WHERE email = @email"
                cmd.Parameters.Add(New SqlParameter("@fn",    SqlDbType.NVarChar, 200) With {.Value = If(String.IsNullOrEmpty(newName), CObj(DBNull.Value), newName)})
                cmd.Parameters.Add(New SqlParameter("@email", SqlDbType.NVarChar, 320) With {.Value = email})
                cmd.ExecuteNonQuery()
            End Using
        End Using

        Dim row = LookupUser(email)
        If row IsNot Nothing Then row.Remove("password_hash")
        Return Ok(row)
    End Function


    ' Authenticated user changes their own password. Requires the current
    ' password to be verified before the new one is set — guards against
    ' someone walking up to an unattended browser session.
    <HttpPost, Route("auth/change-password")>
    Public Function ChangePassword(<FromBody> req As JObject) As IHttpActionResult
        Dim email = If(HttpContext.Current?.User?.Identity?.Name, "")
        If String.IsNullOrEmpty(email) Then Return Unauthorized()

        If req Is Nothing Then Return BadRequest("Request body is required.")
        Dim currentPassword = req.Value(Of String)("current_password")
        Dim newPassword = req.Value(Of String)("new_password")
        If String.IsNullOrEmpty(currentPassword) Then Return BadRequest("'current_password' is required.")
        If String.IsNullOrEmpty(newPassword) Then Return BadRequest("'new_password' is required.")
        If newPassword.Length < 8 Then Return BadRequest("New password must be at least 8 characters.")

        Dim row = LookupUser(email)
        If row Is Nothing Then Return Unauthorized()

        Dim hash = row.Value(Of String)("password_hash")
        If Not PasswordHasher.Verify(currentPassword, hash) Then
            Return Content(HttpStatusCode.Conflict, New JObject From {
                {"status", "error"},
                {"reason", "current_password_incorrect"},
                {"message", "Current password is incorrect."}
            })
        End If

        Dim newHash = PasswordHasher.Hash(newPassword)
        Using conn = Db.Open()
            Using cmd = conn.CreateCommand()
                cmd.CommandText = "
                    UPDATE dbo.Users
                    SET password_hash = @h, updated_date = SYSUTCDATETIME()
                    WHERE email = @email"
                cmd.Parameters.Add(New SqlParameter("@h",     SqlDbType.NVarChar, 255) With {.Value = newHash})
                cmd.Parameters.Add(New SqlParameter("@email", SqlDbType.NVarChar, 320) With {.Value = email})
                cmd.ExecuteNonQuery()
            End Using
        End Using

        Return Ok(New JObject From {{"status", "ok"}})
    End Function


    <HttpPost, Route("auth/seed-admin"), AllowAnonymous>
    Public Function SeedAdmin(<FromBody> req As SeedAdminRequest) As IHttpActionResult
        If req Is Nothing OrElse String.IsNullOrEmpty(req.Email) OrElse String.IsNullOrEmpty(req.Password) Then
            Return BadRequest("email and password are required.")
        End If

        ' Refuse if any user already exists — this is a one-shot bootstrap.
        If CountUsers() > 0 Then
            Return StatusCode(HttpStatusCode.Forbidden)
        End If

        Dim id = Guid.NewGuid().ToString()
        Dim hash = PasswordHasher.Hash(req.Password)

        ' The first user is PeriscopeIQ staff: system admin in the default
        ' account (created here on a brand-new database).
        Using conn = Db.Open()
            Using cmd = conn.CreateCommand()
                cmd.CommandText = "
                    IF NOT EXISTS (SELECT 1 FROM dbo.Accounts WHERE slug = N'periscopeiq')
                        INSERT INTO dbo.Accounts (id, name, slug, created_by)
                        VALUES (LOWER(CONVERT(CHAR(36), NEWID())), N'PeriscopeIQ', N'periscopeiq', @email);

                    INSERT INTO dbo.Users (id, email, password_hash, full_name, role, is_active, created_by, account_id, is_system_admin)
                    VALUES (@id, @email, @hash, @full_name, 'admin', 1, @email,
                            (SELECT id FROM dbo.Accounts WHERE slug = N'periscopeiq'), 1)"
                cmd.Parameters.Add(New SqlParameter("@id",        SqlDbType.Char,     36) With {.Value = id})
                cmd.Parameters.Add(New SqlParameter("@email",     SqlDbType.NVarChar, 320) With {.Value = req.Email})
                cmd.Parameters.Add(New SqlParameter("@hash",      SqlDbType.NVarChar, 255) With {.Value = hash})
                cmd.Parameters.Add(New SqlParameter("@full_name", SqlDbType.NVarChar, 200) With {.Value = If(CObj(req.FullName), DBNull.Value)})
                cmd.ExecuteNonQuery()
            End Using
        End Using

        FormsAuthentication.SetAuthCookie(req.Email, createPersistentCookie:=False)

        Dim row = LookupUser(req.Email)
        If row IsNot Nothing Then row.Remove("password_hash")
        Return Ok(row)
    End Function


    <HttpPost, Route("auth/invite-user"), Authorize>
    Public Async Function InviteUser(<FromBody> req As JObject) As Task(Of IHttpActionResult)
        ' Caller must be an admin of the account the new user joins.
        CurrentUser.RequireAccountAdmin()
        Dim callerEmail = CurrentUser.Email()
        Dim accountId = CurrentUser.AccountId()

        If req Is Nothing Then Return BadRequest("Request body is required.")
        Dim email = req.Value(Of String)("email")
        If String.IsNullOrEmpty(email) Then Return BadRequest("'email' is required.")
        email = email.Trim().ToLowerInvariant()

        Dim role = If(req.Value(Of String)("role"), "user")
        If role <> "user" AndAlso role <> "admin" Then Return BadRequest("'role' must be 'user' or 'admin'.")

        Dim fullName = req.Value(Of String)("full_name")

        Dim token = Guid.NewGuid().ToString("N")
        Dim expires = DateTime.UtcNow.AddDays(7)

        Dim existing = LookupUser(email)
        Dim userId As String

        If existing IsNot Nothing Then
            ' If they've already activated (have a password), refuse.
            Dim hashTok = existing("password_hash")
            If hashTok IsNot Nothing AndAlso hashTok.Type <> JTokenType.Null AndAlso Not String.IsNullOrEmpty(hashTok.ToString()) Then
                Return Conflict()
            End If
            ' A pending invite from another account can't be taken over.
            If Not String.Equals(existing.Value(Of String)("account_id"), accountId, StringComparison.OrdinalIgnoreCase) Then
                Return Conflict()
            End If
            ' Refresh the invite for an existing un-activated user.
            userId = existing.Value(Of String)("id")
            Using conn = Db.Open()
                Using cmd = conn.CreateCommand()
                    cmd.CommandText = "
                        UPDATE dbo.Users
                        SET invite_token = @t,
                            invite_token_expires = @e,
                            role = @r,
                            full_name = COALESCE(@fn, full_name),
                            updated_date = SYSUTCDATETIME()
                        WHERE id = @id"
                    cmd.Parameters.Add(New SqlParameter("@id", SqlDbType.Char,     36)  With {.Value = userId})
                    cmd.Parameters.Add(New SqlParameter("@t",  SqlDbType.NVarChar, 100) With {.Value = token})
                    cmd.Parameters.Add(New SqlParameter("@e",  SqlDbType.DateTime2)     With {.Value = expires})
                    cmd.Parameters.Add(New SqlParameter("@r",  SqlDbType.NVarChar, 20)  With {.Value = role})
                    cmd.Parameters.Add(New SqlParameter("@fn", SqlDbType.NVarChar, 200) With {.Value = If(CObj(fullName), DBNull.Value)})
                    cmd.ExecuteNonQuery()
                End Using
            End Using
        Else
            userId = Guid.NewGuid().ToString()
            Using conn = Db.Open()
                Using cmd = conn.CreateCommand()
                    cmd.CommandText = "
                        INSERT INTO dbo.Users
                            (id, email, role, is_active, invite_token, invite_token_expires, full_name, created_by, account_id)
                        VALUES
                            (@id, @email, @role, 1, @t, @e, @fn, @createdBy, @acct)"
                    cmd.Parameters.Add(New SqlParameter("@id",        SqlDbType.Char,     36)  With {.Value = userId})
                    cmd.Parameters.Add(New SqlParameter("@email",     SqlDbType.NVarChar, 320) With {.Value = email})
                    cmd.Parameters.Add(New SqlParameter("@role",      SqlDbType.NVarChar, 20)  With {.Value = role})
                    cmd.Parameters.Add(New SqlParameter("@t",         SqlDbType.NVarChar, 100) With {.Value = token})
                    cmd.Parameters.Add(New SqlParameter("@e",         SqlDbType.DateTime2)     With {.Value = expires})
                    cmd.Parameters.Add(New SqlParameter("@fn",        SqlDbType.NVarChar, 200) With {.Value = If(CObj(fullName), DBNull.Value)})
                    cmd.Parameters.Add(New SqlParameter("@createdBy", SqlDbType.NVarChar, 320) With {.Value = callerEmail})
                    cmd.Parameters.Add(New SqlParameter("@acct",      SqlDbType.Char,     36)  With {.Value = accountId})
                    cmd.ExecuteNonQuery()
                End Using
            End Using
        End If

        ' Build the invite link off the configured public base URL so emails
        ' point at production, not localhost. Caller can override per env in
        ' Web.config (App:PublicBaseUrl).
        Dim baseUrl = If(ConfigurationManager.AppSettings("App:PublicBaseUrl"), "")
        Dim link = $"{baseUrl.TrimEnd("/"c)}/set-password?token={token}"

        Try
            Await ResendEmailService.SendAsync(email, "You're invited to SurveyFlow", BuildInviteHtml(callerEmail, link))
        Catch ex As Exception
            ' User row exists; surface the email failure so admin can re-send.
            Dim errBody As New JObject From {
                {"status", "user_created_email_failed"},
                {"id", userId},
                {"email", email},
                {"invite_link", link},
                {"error", ex.Message}
            }
            Return Content(HttpStatusCode.PartialContent, errBody)
        End Try

        Return Ok(New JObject From {
            {"status", "ok"},
            {"id", userId},
            {"email", email}
        })
    End Function


    <HttpPost, Route("auth/set-password"), AllowAnonymous>
    Public Function SetPassword(<FromBody> req As JObject) As IHttpActionResult
        If req Is Nothing Then Return BadRequest("Request body is required.")
        Dim token = req.Value(Of String)("token")
        Dim password = req.Value(Of String)("password")
        If String.IsNullOrEmpty(token) Then Return BadRequest("'token' is required.")
        If String.IsNullOrEmpty(password) Then Return BadRequest("'password' is required.")
        If password.Length < 8 Then Return BadRequest("Password must be at least 8 characters.")

        ' Find user by token.
        Dim id As String = Nothing
        Dim email As String = Nothing
        Dim expires As DateTime? = Nothing
        Using conn = Db.Open()
            Using cmd = conn.CreateCommand()
                cmd.CommandText = "
                    SELECT id, email, invite_token_expires
                    FROM dbo.Users
                    WHERE invite_token = @t"
                cmd.Parameters.Add(New SqlParameter("@t", SqlDbType.NVarChar, 100) With {.Value = token})
                Using rdr = cmd.ExecuteReader()
                    If rdr.Read() Then
                        id = rdr.GetString(0)
                        email = rdr.GetString(1)
                        If Not rdr.IsDBNull(2) Then expires = rdr.GetDateTime(2)
                    End If
                End Using
            End Using
        End Using

        If id Is Nothing Then Return Unauthorized()
        If expires.HasValue AndAlso expires.Value < DateTime.UtcNow Then Return Unauthorized()

        Dim hash = PasswordHasher.Hash(password)
        Using conn = Db.Open()
            Using cmd = conn.CreateCommand()
                cmd.CommandText = "
                    UPDATE dbo.Users
                    SET password_hash = @h,
                        invite_token = NULL,
                        invite_token_expires = NULL,
                        updated_date = SYSUTCDATETIME()
                    WHERE id = @id"
                cmd.Parameters.Add(New SqlParameter("@h",  SqlDbType.NVarChar, 255) With {.Value = hash})
                cmd.Parameters.Add(New SqlParameter("@id", SqlDbType.Char,     36)  With {.Value = id})
                cmd.ExecuteNonQuery()
            End Using
        End Using

        FormsAuthentication.SetAuthCookie(email, createPersistentCookie:=False)

        Dim user = LookupUser(email)
        If user IsNot Nothing Then user.Remove("password_hash")
        Return Ok(user)
    End Function


    Private Shared Function BuildInviteHtml(invitedBy As String, link As String) As String
        Dim safeLink = System.Net.WebUtility.HtmlEncode(link)
        Dim safeInviter = System.Net.WebUtility.HtmlEncode(If(invitedBy, ""))
        Return $"
<!doctype html>
<html><body style=""font-family: Arial, sans-serif; max-width: 560px; margin: 0 auto; padding: 24px; color: #1f2937;"">
  <h2 style=""margin: 0 0 12px;"">You've been invited to SurveyFlow</h2>
  <p>{safeInviter} added you to SurveyFlow. Click the button below to choose a password and finish setting up your account.</p>
  <p style=""margin: 24px 0;"">
    <a href=""{safeLink}"" style=""display: inline-block; background: #1f2937; color: #fff; text-decoration: none; padding: 12px 24px; border-radius: 6px; font-weight: 500;"">Accept invitation</a>
  </p>
  <p style=""font-size: 12px; color: #6b7280;"">Or paste this link into your browser: <br><span style=""word-break: break-all;"">{safeLink}</span></p>
  <p style=""font-size: 12px; color: #6b7280; margin-top: 24px;"">This link expires in 7 days. If you didn't expect this invitation, you can safely ignore the email.</p>
</body></html>"
    End Function


    ' --- helpers ----------------------------------------------------------

    Private Function LookupUser(email As String) As JObject
        Using conn = Db.Open()
            Using cmd = conn.CreateCommand()
                cmd.CommandText = "
                    SELECT u.id, u.email, u.password_hash, u.full_name, u.role, u.is_active,
                           u.created_date, u.created_by, u.updated_date,
                           u.account_id, a.name AS account_name, a.contact_match_key, u.is_system_admin
                    FROM dbo.Users u
                    LEFT JOIN dbo.Accounts a ON a.id = u.account_id
                    WHERE u.email = @email"
                cmd.Parameters.Add(New SqlParameter("@email", SqlDbType.NVarChar, 320) With {.Value = email})
                Using rdr = cmd.ExecuteReader()
                    If Not rdr.Read() Then Return Nothing

                    Dim obj As New JObject
                    For i = 0 To rdr.FieldCount - 1
                        Dim name = rdr.GetName(i)
                        If rdr.IsDBNull(i) Then
                            obj(name) = JValue.CreateNull()
                        Else
                            obj(name) = JToken.FromObject(rdr.GetValue(i))
                        End If
                    Next
                    Return obj
                End Using
            End Using
        End Using
    End Function

    Private Function CountUsers() As Integer
        Using conn = Db.Open()
            Using cmd = conn.CreateCommand()
                cmd.CommandText = "SELECT COUNT(*) FROM dbo.Users"
                Return CInt(cmd.ExecuteScalar())
            End Using
        End Using
    End Function

End Class
