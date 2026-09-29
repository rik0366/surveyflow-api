Imports System.Configuration
Imports System.Net.Http
Imports System.Net.Http.Headers
Imports System.Text
Imports System.Threading.Tasks
Imports Newtonsoft.Json

' Sends transactional email through Resend's REST API
' (https://resend.com/docs/api-reference/emails/send-email).
'
' Reads the API key and From address from Web.config:
'   Resend:ApiKey       — bearer token, get one at https://resend.com/api-keys
'   Resend:FromAddress  — "Display Name <noreply@yourdomain.com>" (sender domain
'                         must be verified in Resend)
'
' HttpClient is a static singleton — recreating it per call leaks sockets.
Public Module ResendEmailService

    Private Const EmailsEndpoint As String = "https://api.resend.com/emails"
    Private ReadOnly Http As New HttpClient()


    Public Async Function SendAsync(toAddress As String,
                                    subject As String,
                                    html As String) As Task(Of String)

        Dim apiKey = ConfigurationManager.AppSettings("Resend:ApiKey")
        If String.IsNullOrEmpty(apiKey) Then
            Throw New InvalidOperationException(
                "Resend:ApiKey is not set in Web.config. Get a key from https://resend.com/api-keys.")
        End If

        Dim fromAddress = ConfigurationManager.AppSettings("Resend:FromAddress")
        If String.IsNullOrEmpty(fromAddress) Then
            Throw New InvalidOperationException(
                "Resend:FromAddress is not set in Web.config.")
        End If

        Dim payload = New With {
            .from    = fromAddress,
            .to      = New String() {toAddress},
            .subject = If(subject, ""),
            .html    = If(html, "")
        }
        Dim json = JsonConvert.SerializeObject(payload)

        Using req As New HttpRequestMessage(HttpMethod.Post, EmailsEndpoint)
            req.Headers.Authorization = New AuthenticationHeaderValue("Bearer", apiKey)
            req.Content = New StringContent(json, Encoding.UTF8, "application/json")

            Using resp = Await Http.SendAsync(req).ConfigureAwait(False)
                Dim body = Await resp.Content.ReadAsStringAsync().ConfigureAwait(False)
                If Not resp.IsSuccessStatusCode Then
                    Throw New HttpRequestException(
                        $"Resend returned HTTP {CInt(resp.StatusCode)}: {body}")
                End If
                Return body
            End Using
        End Using
    End Function

End Module
