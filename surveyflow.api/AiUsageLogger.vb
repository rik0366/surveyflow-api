Imports System.Data
Imports System.Data.SqlClient

' Records one row in dbo.AiUsage per OpenAI call. Best-effort: swallows
' exceptions so a logging failure can't break the actual API operation.
Public Module AiUsageLogger

    Public Sub Log(userEmail As String,
                   surveyId As String,
                   endpoint As String,
                   model As String,
                   promptTokens As Integer?,
                   completionTokens As Integer?,
                   totalTokens As Integer?,
                   success As Boolean)
        Try
            Using conn = Db.Open()
                Using cmd = conn.CreateCommand()
                    cmd.CommandText = "
                        INSERT INTO dbo.AiUsage
                            (id, user_email, survey_id, endpoint, model,
                             prompt_tokens, completion_tokens, total_tokens, success, account_id)
                        VALUES
                            (@id, @user, @sid, @ep, @model,
                             @pt, @ct, @tt, @ok,
                             -- Bill the survey's account; fall back to the user's.
                             COALESCE((SELECT account_id FROM dbo.Surveys WHERE id = @sid),
                                      (SELECT account_id FROM dbo.Users   WHERE email = @user)))"
                    cmd.Parameters.Add(New SqlParameter("@id",    SqlDbType.Char,     36)  With {.Value = Guid.NewGuid().ToString()})
                    cmd.Parameters.Add(New SqlParameter("@user",  SqlDbType.NVarChar, 320) With {.Value = If(String.IsNullOrEmpty(userEmail), CObj(DBNull.Value), userEmail)})
                    cmd.Parameters.Add(New SqlParameter("@sid",   SqlDbType.Char,     36)  With {.Value = If(String.IsNullOrEmpty(surveyId), CObj(DBNull.Value), surveyId)})
                    cmd.Parameters.Add(New SqlParameter("@ep",    SqlDbType.NVarChar, 100) With {.Value = endpoint})
                    cmd.Parameters.Add(New SqlParameter("@model", SqlDbType.NVarChar, 100) With {.Value = If(String.IsNullOrEmpty(model), CObj(DBNull.Value), model)})
                    cmd.Parameters.Add(New SqlParameter("@pt",    SqlDbType.Int)           With {.Value = If(promptTokens.HasValue,     CObj(promptTokens.Value),     DBNull.Value)})
                    cmd.Parameters.Add(New SqlParameter("@ct",    SqlDbType.Int)           With {.Value = If(completionTokens.HasValue, CObj(completionTokens.Value), DBNull.Value)})
                    cmd.Parameters.Add(New SqlParameter("@tt",    SqlDbType.Int)           With {.Value = If(totalTokens.HasValue,      CObj(totalTokens.Value),      DBNull.Value)})
                    cmd.Parameters.Add(New SqlParameter("@ok",    SqlDbType.Bit)           With {.Value = success})
                    cmd.ExecuteNonQuery()
                End Using
            End Using
        Catch
            ' Swallow. Usage logging is non-critical.
        End Try
    End Sub

End Module
