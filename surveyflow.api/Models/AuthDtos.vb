' Request payloads for AuthController. Kept simple — we use Newtonsoft.Json
' default binding so both PascalCase and snake_case input is tolerated.

Public Class LoginRequest
    Public Property Email As String
    Public Property Password As String
End Class

Public Class SeedAdminRequest
    Public Property Email As String
    Public Property Password As String
    Public Property FullName As String
End Class
