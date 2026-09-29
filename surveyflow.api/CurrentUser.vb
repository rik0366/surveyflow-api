Imports System.Data
Imports System.Data.SqlClient
Imports System.Net
Imports System.Net.Http
Imports System.Web.Http

' Reads the authenticated user from the current request.
'
' Forms auth stores the username we passed to FormsAuthentication.SetAuthCookie
' (the user's email) in HttpContext.Current.User.Identity.Name. Email()
' returns "anonymous" for unauthenticated requests — used so anonymous survey
' respondents can still POST a SurveyResponse without erroring.
'
' Info() loads the user's row once per request (cached in HttpContext.Items)
' so controllers can scope data by account. A cookie for a user that was
' deleted or deactivated after login yields Nothing, same as anonymous.
Public Module CurrentUser

    Private Const CacheKey As String = "SurveyFlow.CurrentUser"

    ' System admins may act inside another account by sending this header.
    Public Const AccountHeader As String = "X-Account-Id"

    Public Function Email() As String
        Dim ctx = System.Web.HttpContext.Current
        If ctx IsNot Nothing AndAlso
           ctx.User IsNot Nothing AndAlso
           ctx.User.Identity IsNot Nothing AndAlso
           ctx.User.Identity.IsAuthenticated Then
            Return ctx.User.Identity.Name
        End If
        Return "anonymous"
    End Function

    Public Function Info() As RequestUser
        Dim ctx = System.Web.HttpContext.Current
        If ctx Is Nothing Then Return Nothing
        If ctx.Items.Contains(CacheKey) Then Return TryCast(ctx.Items(CacheKey), RequestUser)

        Dim user As RequestUser = Nothing
        Dim email = CurrentUser.Email()
        If email <> "anonymous" Then user = Load(email, ctx.Request.Headers(AccountHeader))
        ctx.Items(CacheKey) = user
        Return user
    End Function

    ' The account the caller is working in. Throws 401 for anonymous callers
    ' and 403 for a user with no account (shouldn't happen after 09 backfill).
    Public Function AccountId() As String
        Dim u = Info()
        If u Is Nothing Then Throw New HttpResponseException(HttpStatusCode.Unauthorized)
        If String.IsNullOrEmpty(u.AccountId) Then Throw New HttpResponseException(HttpStatusCode.Forbidden)
        Return u.AccountId
    End Function

    ' Repository scope for the caller's account.
    Public Function Scope() As RepoScope
        Return RepoScope.ForAccount(AccountId())
    End Function

    Public Sub RequireAccountAdmin()
        Dim u = Info()
        If u Is Nothing Then Throw New HttpResponseException(HttpStatusCode.Unauthorized)
        If Not u.IsAccountAdmin Then Throw New HttpResponseException(HttpStatusCode.Forbidden)
    End Sub

    Public Sub RequireSystemAdmin()
        Dim u = Info()
        If u Is Nothing Then Throw New HttpResponseException(HttpStatusCode.Unauthorized)
        If Not u.IsSystemAdmin Then Throw New HttpResponseException(HttpStatusCode.Forbidden)
    End Sub

    Private Function Load(email As String, requestedAccount As String) As RequestUser
        Dim u As RequestUser = Nothing
        Using conn = Db.Open()
            Using cmd = conn.CreateCommand()
                cmd.CommandText = "
                    SELECT id, email, account_id, role, is_system_admin
                    FROM dbo.Users
                    WHERE email = @e AND is_active = 1"
                cmd.Parameters.Add(New SqlParameter("@e", SqlDbType.NVarChar, 320) With {.Value = email})
                Using rdr = cmd.ExecuteReader()
                    If Not rdr.Read() Then Return Nothing
                    u = New RequestUser With {
                        .Id = rdr.GetString(0),
                        .Email = rdr.GetString(1),
                        .HomeAccountId = If(rdr.IsDBNull(2), Nothing, rdr.GetString(2)),
                        .Role = If(rdr.IsDBNull(3), "user", rdr.GetString(3)),
                        .IsSystemAdmin = rdr.GetBoolean(4)
                    }
                    u.AccountId = u.HomeAccountId
                End Using
            End Using

            ' System admins can switch into any active account.
            If u.IsSystemAdmin AndAlso Not String.IsNullOrWhiteSpace(requestedAccount) AndAlso
               Not String.Equals(requestedAccount, u.HomeAccountId, StringComparison.OrdinalIgnoreCase) Then
                Using cmd = conn.CreateCommand()
                    cmd.CommandText = "SELECT id FROM dbo.Accounts WHERE id = @a AND is_active = 1"
                    cmd.Parameters.Add(New SqlParameter("@a", SqlDbType.Char, 36) With {.Value = requestedAccount.Trim()})
                    Dim found = TryCast(cmd.ExecuteScalar(), String)
                    If found Is Nothing Then
                        Throw New HttpResponseException(New HttpResponseMessage(HttpStatusCode.BadRequest) With {
                            .Content = New StringContent($"Unknown account in {AccountHeader} header.")
                        })
                    End If
                    u.AccountId = found
                End Using
            End If
        End Using
        Return u
    End Function

End Module


Public Class RequestUser
    Public Property Id As String
    Public Property Email As String
    ' The account the user belongs to.
    Public Property HomeAccountId As String
    ' The account this request acts in (differs from HomeAccountId only for
    ' a system admin who sent X-Account-Id).
    Public Property AccountId As String
    ' Role within the account: 'admin' or 'user'.
    Public Property Role As String
    ' PeriscopeIQ staff: can see all accounts and switch between them.
    Public Property IsSystemAdmin As Boolean

    Public ReadOnly Property IsAccountAdmin As Boolean
        Get
            Return IsSystemAdmin OrElse String.Equals(Role, "admin", StringComparison.OrdinalIgnoreCase)
        End Get
    End Property
End Class
