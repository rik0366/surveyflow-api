Imports System.Web.Http

' Smoke-test endpoint. GET /api/health
'   - Confirms the API is running.
'   - Confirms the SurveyFlow database is reachable with the configured
'     connection string.
'
' Use it after deploying or after changing the connection string to verify
' end-to-end wiring before touching real data.
Public Class HealthController
    Inherits ApiController

    <HttpGet, AllowAnonymous>
    <Route("health")>
    Public Function Status() As Object
        Try
            Using conn = Db.Open()
                Using cmd = conn.CreateCommand()
                    cmd.CommandText = "SELECT 1"
                    cmd.ExecuteScalar()
                End Using
            End Using

            Return New With {
                .status = "ok",
                .database = "connected",
                .server_time_utc = DateTime.UtcNow
            }
        Catch ex As Exception
            Return New With {
                .status = "error",
                .database = "unreachable",
                .error = ex.Message,
                .server_time_utc = DateTime.UtcNow
            }
        End Try
    End Function
End Class
