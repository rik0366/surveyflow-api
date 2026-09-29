' Thin wrapper around BCrypt.Net-Next so callers don't have to import the
' library directly and so we can swap implementations later (e.g. argon2)
' without touching controllers.
Public Module PasswordHasher

    Public Function Hash(password As String) As String
        If String.IsNullOrEmpty(password) Then
            Throw New ArgumentException("Password cannot be empty.", NameOf(password))
        End If
        Return BCrypt.Net.BCrypt.HashPassword(password)
    End Function

    Public Function Verify(password As String, hash As String) As Boolean
        If String.IsNullOrEmpty(password) OrElse String.IsNullOrEmpty(hash) Then Return False
        Try
            Return BCrypt.Net.BCrypt.Verify(password, hash)
        Catch
            ' Malformed hash, etc. — treat as failed verification.
            Return False
        End Try
    End Function

End Module
