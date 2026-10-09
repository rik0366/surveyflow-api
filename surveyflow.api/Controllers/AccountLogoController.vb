Imports System.Data
Imports System.Data.SqlClient
Imports System.IO
Imports System.Linq
Imports System.Net
Imports System.Net.Http
Imports System.Net.Sockets
Imports System.Threading.Tasks
Imports System.Web.Hosting
Imports System.Web.Http
Imports Newtonsoft.Json.Linq

' Client logos, shown on the survey start page, the top of each survey page
' and in invite emails. Stored as Accounts.settings_json.logo_url.
'
' We always keep our own copy under ~/uploads (served as /api/uploads/...),
' even when the client gives us a link to the image on their site: a link we
' don't control breaks silently when they redesign, may be hotlink-blocked,
' and would let their server see every respondent's page view / email open.
'
' Old logo files are never deleted — invite emails already sent point at them.
'
' Routes (system admins only):
'   POST   /api/admin/accounts/{id}/logo   multipart image file, OR JSON { url } to copy from the web
'   DELETE /api/admin/accounts/{id}/logo
Public Class AccountLogoController
    Inherits ApiController

    Private Const MaxBytes As Integer = 2 * 1024 * 1024
    Private Const TooBig As String = "That image is larger than 2 MB. Please use a smaller logo."
    Private Const SvgMessage As String = "SVG logos aren't supported (most email programs can't show them). Please use a PNG or JPG."

    Private Shared ReadOnly Fetcher As HttpClient = CreateFetcher()

    Private Shared Function CreateFetcher() As HttpClient
        ' Redirects are followed by hand so every hop's host is checked.
        Dim client As New HttpClient(New HttpClientHandler With {.AllowAutoRedirect = False}) With {
            .Timeout = TimeSpan.FromSeconds(20)
        }
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; SurveyFlow-LogoFetch/1.0)")
        Return client
    End Function

    <HttpPost, Route("admin/accounts/{id}/logo")>
    Public Async Function SetLogo(id As String) As Task(Of IHttpActionResult)
        CurrentUser.RequireSystemAdmin()
        If Not AccountExists(id) Then Return NotFound()

        Dim bytes As Byte()
        If Request.Content.IsMimeMultipartContent() Then
            Dim provider = Await Request.Content.ReadAsMultipartAsync(New MultipartMemoryStreamProvider())
            Dim part = provider.Contents.FirstOrDefault(
                Function(c) c.Headers.ContentDisposition IsNot Nothing AndAlso
                           Not String.IsNullOrEmpty(c.Headers.ContentDisposition.FileName))
            If part Is Nothing Then Return BadRequest("Choose an image file to upload.")
            bytes = Await part.ReadAsByteArrayAsync()
        Else
            Dim raw = Await Request.Content.ReadAsStringAsync()
            Dim url As String = Nothing
            Try
                url = JObject.Parse(raw).Value(Of String)("url")
            Catch
            End Try
            If String.IsNullOrWhiteSpace(url) Then Return BadRequest("Send an image file, or { url } of an image on the web.")
            Dim fetched = Await FetchImage(url.Trim())
            If fetched.Item2 IsNot Nothing Then Return BadRequest(fetched.Item2)
            bytes = fetched.Item1
        End If

        If bytes.Length > MaxBytes Then Return BadRequest(TooBig)
        Dim ext = SniffExtension(bytes)
        If ext Is Nothing Then
            Return BadRequest(If(LooksLikeSvg(bytes), SvgMessage, "That isn't a PNG, JPG, GIF or WebP image."))
        End If

        Dim fileName = "logo-" & Guid.NewGuid().ToString("N") & ext
        Dim dir = HostingEnvironment.MapPath("~/uploads")
        Directory.CreateDirectory(dir)
        File.WriteAllBytes(Path.Combine(dir, fileName), bytes)

        Dim logoUrl = "/api/uploads/" & fileName
        SaveLogoUrl(id, logoUrl)
        Return Ok(New JObject From {{"logo_url", logoUrl}})
    End Function

    <HttpDelete, Route("admin/accounts/{id}/logo")>
    Public Function RemoveLogo(id As String) As IHttpActionResult
        CurrentUser.RequireSystemAdmin()
        If Not AccountExists(id) Then Return NotFound()
        SaveLogoUrl(id, Nothing)
        Return Ok(New JObject From {{"logo_url", Nothing}})
    End Function

    ' The account's logo URL (site-relative, e.g. /api/uploads/logo-….png) or Nothing.
    Public Shared Function LogoUrlFor(accountId As String) As String
        If String.IsNullOrEmpty(accountId) Then Return Nothing
        Using conn = Db.Open()
            Using cmd = conn.CreateCommand()
                cmd.CommandText = "SELECT JSON_VALUE(settings_json, '$.logo_url') FROM dbo.Accounts WHERE id = @id"
                cmd.Parameters.Add(New SqlParameter("@id", SqlDbType.Char, 36) With {.Value = accountId})
                Dim v = cmd.ExecuteScalar()
                Return If(v Is Nothing OrElse v Is DBNull.Value, Nothing, CStr(v))
            End Using
        End Using
    End Function

    ' JSON_MODIFY with a NULL value removes the key.
    Private Shared Sub SaveLogoUrl(id As String, logoUrl As String)
        Using conn = Db.Open()
            Using cmd = conn.CreateCommand()
                cmd.CommandText = "
                    UPDATE dbo.Accounts
                    SET settings_json = JSON_MODIFY(COALESCE(settings_json, N'{}'), '$.logo_url', @u),
                        updated_date = SYSUTCDATETIME()
                    WHERE id = @id"
                cmd.Parameters.Add(New SqlParameter("@id", SqlDbType.Char, 36) With {.Value = id})
                cmd.Parameters.Add(New SqlParameter("@u", SqlDbType.NVarChar, 400) With {.Value = If(CObj(logoUrl), DBNull.Value)})
                cmd.ExecuteNonQuery()
            End Using
        End Using
    End Sub

    Private Shared Function AccountExists(id As String) As Boolean
        If String.IsNullOrEmpty(id) OrElse id.Length > 36 Then Return False
        Using conn = Db.Open()
            Using cmd = conn.CreateCommand()
                cmd.CommandText = "SELECT COUNT(*) FROM dbo.Accounts WHERE id = @id"
                cmd.Parameters.Add(New SqlParameter("@id", SqlDbType.Char, 36) With {.Value = id})
                Return CInt(cmd.ExecuteScalar()) > 0
            End Using
        End Using
    End Function

    ' --- Fetching from a URL ----------------------------------------------

    Private Shared Function Fail(message As String) As Tuple(Of Byte(), String)
        Return Tuple.Create(Of Byte(), String)(Nothing, message)
    End Function

    ' Downloads an image from a public website. Only http(s), only public
    ' addresses (no localhost / internal network), at most 2 MB, at most 5
    ' redirects — each redirect target is checked the same way.
    Private Shared Async Function FetchImage(url As String) As Task(Of Tuple(Of Byte(), String))
        Dim current As Uri = Nothing
        If Not Uri.TryCreate(url, UriKind.Absolute, current) OrElse
           (current.Scheme <> Uri.UriSchemeHttp AndAlso current.Scheme <> Uri.UriSchemeHttps) Then
            Return Fail("Enter the full web address of the image, starting with https://")
        End If

        For hop = 0 To 5
            Dim addrs As IPAddress() = Nothing
            Try
                addrs = Await Dns.GetHostAddressesAsync(current.DnsSafeHost)
            Catch
            End Try
            If addrs Is Nothing OrElse addrs.Length = 0 Then Return Fail("Couldn't find the website " & current.Host & ".")
            If Not addrs.All(AddressOf IsPublicAddress) Then Return Fail("That address isn't a public website.")

            Dim resp As HttpResponseMessage
            Try
                resp = Await Fetcher.GetAsync(current, HttpCompletionOption.ResponseHeadersRead)
            Catch ex As Exception
                Return Fail("Couldn't download the image from " & current.Host & ".")
            End Try

            Using resp
                Dim code = CInt(resp.StatusCode)
                If code >= 300 AndAlso code < 400 AndAlso resp.Headers.Location IsNot Nothing Then
                    Dim loc = resp.Headers.Location
                    current = If(loc.IsAbsoluteUri, loc, New Uri(current, loc))
                    If current.Scheme <> Uri.UriSchemeHttp AndAlso current.Scheme <> Uri.UriSchemeHttps Then
                        Return Fail("That address redirects somewhere we can't download from.")
                    End If
                    Continue For
                End If
                If Not resp.IsSuccessStatusCode Then
                    Return Fail($"The website returned an error ({code}) for that address.")
                End If
                Dim len = resp.Content.Headers.ContentLength
                If len.HasValue AndAlso len.Value > MaxBytes Then Return Fail(TooBig)

                Using s = Await resp.Content.ReadAsStreamAsync(), ms As New MemoryStream()
                    Dim buf(81919) As Byte
                    Do
                        Dim n = Await s.ReadAsync(buf, 0, buf.Length)
                        If n = 0 Then Exit Do
                        ms.Write(buf, 0, n)
                        If ms.Length > MaxBytes Then Return Fail(TooBig)
                    Loop
                    Return Tuple.Create(Of Byte(), String)(ms.ToArray(), Nothing)
                End Using
            End Using
        Next
        Return Fail("That address redirects too many times.")
    End Function

    Private Shared Function IsPublicAddress(ip As IPAddress) As Boolean
        If ip.IsIPv4MappedToIPv6 Then ip = ip.MapToIPv4()
        If IPAddress.IsLoopback(ip) Then Return False
        Dim b = ip.GetAddressBytes()
        If ip.AddressFamily = AddressFamily.InterNetwork Then
            If b(0) = 0 OrElse b(0) = 10 OrElse b(0) = 127 Then Return False
            If b(0) = 172 AndAlso b(1) >= 16 AndAlso b(1) <= 31 Then Return False
            If b(0) = 192 AndAlso b(1) = 168 Then Return False
            If b(0) = 169 AndAlso b(1) = 254 Then Return False
            If b(0) = 100 AndAlso b(1) >= 64 AndAlso b(1) <= 127 Then Return False
            If b(0) >= 224 Then Return False
            Return True
        End If
        If ip.IsIPv6LinkLocal OrElse ip.IsIPv6SiteLocal OrElse ip.IsIPv6Multicast Then Return False
        If (b(0) And &HFE) = &HFC Then Return False ' fc00::/7 unique local
        Return True
    End Function

    ' --- Image type detection (by content, not file name) -----------------

    Private Shared Function SniffExtension(b As Byte()) As String
        If b.Length >= 8 AndAlso b(0) = &H89 AndAlso b(1) = &H50 AndAlso b(2) = &H4E AndAlso b(3) = &H47 Then Return ".png"
        If b.Length >= 3 AndAlso b(0) = &HFF AndAlso b(1) = &HD8 AndAlso b(2) = &HFF Then Return ".jpg"
        If b.Length >= 6 AndAlso b(0) = &H47 AndAlso b(1) = &H49 AndAlso b(2) = &H46 AndAlso b(3) = &H38 Then Return ".gif"
        If b.Length >= 12 AndAlso b(0) = &H52 AndAlso b(1) = &H49 AndAlso b(2) = &H46 AndAlso b(3) = &H46 AndAlso
           b(8) = &H57 AndAlso b(9) = &H45 AndAlso b(10) = &H42 AndAlso b(11) = &H50 Then Return ".webp"
        Return Nothing
    End Function

    Private Shared Function LooksLikeSvg(b As Byte()) As Boolean
        Dim head = Text.Encoding.UTF8.GetString(b, 0, Math.Min(b.Length, 1024))
        Return head.IndexOf("<svg", StringComparison.OrdinalIgnoreCase) >= 0
    End Function

End Class
