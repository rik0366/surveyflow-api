Imports System.Data
Imports System.Data.SqlClient
Imports Newtonsoft.Json.Linq

' Cross-entity ownership lookups used when a child row (invite, response)
' must land in the same account as its parent survey, and when respondents
' (who have no account) touch data through an unguessable token or id.
Public Module Ownership

    ' Account that owns the survey, or Nothing if the survey doesn't exist.
    Public Function SurveyAccountId(surveyId As String) As String
        If String.IsNullOrEmpty(surveyId) Then Return Nothing
        Using conn = Db.Open()
            Using cmd = conn.CreateCommand()
                cmd.CommandText = "SELECT account_id FROM dbo.Surveys WHERE id = @id"
                cmd.Parameters.Add(New SqlParameter("@id", SqlDbType.Char, 36) With {.Value = surveyId})
                Return TryCast(cmd.ExecuteScalar(), String)
            End Using
        End Using
    End Function

    ' True when the survey exists and belongs to the caller's account.
    Public Function SurveyInCallerAccount(surveyId As String) As Boolean
        Dim acct = SurveyAccountId(surveyId)
        Return acct IsNot Nothing AndAlso String.Equals(acct, CurrentUser.AccountId(), StringComparison.OrdinalIgnoreCase)
    End Function

    ' Copy only the listed keys from a request body. Used for respondent
    ' (anonymous) writes so they can't touch owner-only fields.
    Public Function Pick(data As JObject, ParamArray keys() As String) As JObject
        Dim out As New JObject
        If data Is Nothing Then Return out
        For Each k In keys
            Dim v = data(k)
            If v IsNot Nothing Then out(k) = v
        Next
        Return out
    End Function

    ' True when the email is on the global suppression list or the account's
    ' opt-out list. Case-insensitive via the server collation.
    Public Function IsSuppressed(accountId As String, email As String) As Boolean
        If String.IsNullOrWhiteSpace(email) Then Return False
        Using conn = Db.Open()
            Using cmd = conn.CreateCommand()
                cmd.CommandText = "
                    SELECT CASE WHEN
                        EXISTS (SELECT 1 FROM dbo.EmailSuppression WHERE email = @e)
                     OR EXISTS (SELECT 1 FROM dbo.EmailBlacklist   WHERE email = @e AND account_id = @a)
                    THEN 1 ELSE 0 END"
                cmd.Parameters.Add(New SqlParameter("@e", SqlDbType.NVarChar, 320) With {.Value = email.Trim()})
                cmd.Parameters.Add(New SqlParameter("@a", SqlDbType.Char, 36) With {.Value = If(CObj(accountId), DBNull.Value)})
                Return CInt(cmd.ExecuteScalar()) = 1
            End Using
        End Using
    End Function

End Module
