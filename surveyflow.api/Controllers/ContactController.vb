Imports System.Data
Imports System.Data.SqlClient
Imports System.Net
Imports System.Web.Http
Imports Newtonsoft.Json.Linq

' Address book contacts (legacy Address_Book), limited to the caller's
' account. Writes go through ContactService so manual edits and imports
' follow the same rules.
'
' Routes:
'   GET    /api/Contact?filter=&sort=&limit=     generic filter (SDK-style)
'   GET    /api/Contact/search?q=&group_id=&include_inactive=&sort=&page=&page_size=
'                                                → { items, total, page, page_size }
'   GET    /api/Contact/{id}                     → contact + groups[]
'   POST   /api/Contact                          body: contact fields + group_ids[]
'   PUT    /api/Contact/{id}                     partial; group_ids[] replaces the set
'   DELETE /api/Contact/{id}                     deactivate (soft delete)
'   POST   /api/Contact/deactivate               { ids: [] }
'   POST   /api/Contact/import                   { rows: [], group_id?, update_existing?, dry_run? }
Public Class ContactController
    Inherits ApiController

    Private Shared ReadOnly Repo As New JsonRepository(EntityRegistry.Contact)

    Private Shared ReadOnly SearchSorts As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase) From {
        {"last_name", "c.last_name, c.first_name"},
        {"first_name", "c.first_name, c.last_name"},
        {"email", "c.email"},
        {"company", "c.company, c.last_name"},
        {"id_number", "c.id_number"},
        {"created_date", "c.created_date"},
        {"updated_date", "c.updated_date"}
    }

    <HttpGet, Route("Contact")>
    Public Function List(Optional filter As String = "", Optional sort As String = "", Optional limit As Integer? = Nothing) As JArray
        Dim filterObj As JObject = Nothing
        If Not String.IsNullOrEmpty(filter) Then filterObj = JObject.Parse(filter)
        Return Repo.Find(filterObj, sort, limit, CurrentUser.Scope())
    End Function

    ' Paged search for the address-book grid. q matches name, email,
    ' company or ID number (contains). Hidden contacts are excluded (legacy
    ' behaviour) unless include_hidden=true.
    <HttpGet, Route("Contact/search")>
    Public Function Search(Optional q As String = Nothing, Optional group_id As String = Nothing,
                           Optional include_inactive As Boolean = False, Optional include_hidden As Boolean = False,
                           Optional sort As String = "last_name", Optional page As Integer = 1,
                           Optional page_size As Integer = 25) As IHttpActionResult
        Dim accountId = CurrentUser.AccountId()
        page = Math.Max(1, page)
        page_size = Math.Min(Math.Max(1, page_size), 1000)

        Dim desc = sort IsNot Nothing AndAlso sort.StartsWith("-")
        Dim sortKey = If(desc, sort.Substring(1), If(sort, "last_name"))
        Dim orderCols As String = Nothing
        If Not SearchSorts.TryGetValue(sortKey, orderCols) Then Return BadRequest($"Cannot sort by '{sortKey}'.")
        Dim orderBy = String.Join(", ", orderCols.Split(","c).Select(Function(c) c.Trim() & If(desc, " DESC", " ASC"))) & ", c.id"

        Dim items As New JArray
        Dim total = 0
        Using conn = Db.Open()
            Using cmd = conn.CreateCommand()
                cmd.CommandText = $"
                    SELECT c.id, c.first_name, c.last_name, c.email, c.id_number, c.company, c.custom_json,
                           c.is_active, c.is_hidden, c.created_date, c.updated_date,
                           COUNT(*) OVER () AS total
                    FROM dbo.Contacts c
                    WHERE c.account_id = @a
                      AND (@inactive = 1 OR c.is_active = 1)
                      AND (@hidden = 1 OR c.is_hidden = 0)
                      AND (@q IS NULL OR c.first_name LIKE @q ESCAPE '\' OR c.last_name LIKE @q ESCAPE '\'
                           OR c.email LIKE @q ESCAPE '\' OR c.company LIKE @q ESCAPE '\' OR c.id_number LIKE @q ESCAPE '\'
                           OR (c.first_name + ' ' + c.last_name) LIKE @q ESCAPE '\')
                      AND (@g IS NULL OR EXISTS (SELECT 1 FROM dbo.ContactGroupMembers m WHERE m.contact_id = c.id AND m.group_id = @g))
                    ORDER BY {orderBy}
                    OFFSET @skip ROWS FETCH NEXT @take ROWS ONLY"
                Dim term = ContactService.Clean(q)
                cmd.Parameters.Add(New SqlParameter("@a", SqlDbType.Char, 36) With {.Value = accountId})
                cmd.Parameters.Add(New SqlParameter("@inactive", SqlDbType.Bit) With {.Value = include_inactive})
                cmd.Parameters.Add(New SqlParameter("@hidden", SqlDbType.Bit) With {.Value = include_hidden})
                cmd.Parameters.Add(New SqlParameter("@q", SqlDbType.NVarChar, 400) With {
                    .Value = If(term Is Nothing, CObj(DBNull.Value), "%" & EscapeLike(term) & "%")
                })
                cmd.Parameters.Add(New SqlParameter("@g", SqlDbType.Char, 36) With {.Value = If(CObj(ContactService.Clean(group_id)), DBNull.Value)})
                cmd.Parameters.Add(New SqlParameter("@skip", SqlDbType.Int) With {.Value = (page - 1) * page_size})
                cmd.Parameters.Add(New SqlParameter("@take", SqlDbType.Int) With {.Value = page_size})
                Using rdr = cmd.ExecuteReader()
                    While rdr.Read()
                        total = rdr.GetInt32(11)
                        items.Add(New JObject From {
                            {"id", rdr.GetString(0)},
                            {"first_name", Str(rdr, 1)},
                            {"last_name", Str(rdr, 2)},
                            {"email", Str(rdr, 3)},
                            {"id_number", Str(rdr, 4)},
                            {"company", Str(rdr, 5)},
                            {"custom", If(rdr.IsDBNull(6), JValue.CreateNull(), JToken.Parse(rdr.GetString(6)))},
                            {"is_active", rdr.GetBoolean(7)},
                            {"is_hidden", rdr.GetBoolean(8)},
                            {"created_date", rdr.GetDateTime(9)},
                            {"updated_date", rdr.GetDateTime(10)}
                        })
                    End While
                End Using
            End Using
        End Using

        Return Ok(New JObject From {{"items", items}, {"total", total}, {"page", page}, {"page_size", page_size}})
    End Function

    <HttpGet, Route("Contact/{id}")>
    Public Function GetOne(id As String) As IHttpActionResult
        Dim row = Repo.GetById(id, CurrentUser.Scope())
        If row Is Nothing Then Return NotFound()
        row("groups") = LoadGroups(id)
        Return Ok(row)
    End Function

    <HttpPost, Route("Contact")>
    Public Function Create(<FromBody> data As JObject) As IHttpActionResult
        If data Is Nothing Then Return BadRequest("Request body is required.")
        Try
            Dim id = ContactService.Create(CurrentUser.AccountId(), data, CurrentUser.Email())
            Return GetOne(id)
        Catch ex As ContactService.ContactRuleException
            Return RuleError(ex)
        End Try
    End Function

    <HttpPut, Route("Contact/{id}")>
    Public Function Update(id As String, <FromBody> data As JObject) As IHttpActionResult
        If data Is Nothing Then Return BadRequest("Request body is required.")
        Try
            ContactService.Update(CurrentUser.AccountId(), id, data, CurrentUser.Email())
            Return GetOne(id)
        Catch ex As ContactService.ContactRuleException
            Return RuleError(ex)
        End Try
    End Function

    ' Soft delete: the contact disappears from the address book but its
    ' invites/responses (and history) stay intact. PUT is_active=true restores.
    <HttpDelete, Route("Contact/{id}")>
    Public Function Delete(id As String) As IHttpActionResult
        Return Update(id, New JObject From {{"is_active", False}})
    End Function

    <HttpPost, Route("Contact/deactivate")>
    Public Function DeactivateMany(<FromBody> data As JObject) As IHttpActionResult
        Dim ids = ContactService.ReadIdList(data, "ids")
        If ids Is Nothing OrElse ids.Count = 0 Then Return BadRequest("'ids' is required.")
        Dim done = 0
        For Each id In ids
            Try
                ContactService.Update(CurrentUser.AccountId(), id, New JObject From {{"is_active", False}}, CurrentUser.Email())
                done += 1
            Catch ex As ContactService.ContactRuleException
                ' Not in this account — skip.
            End Try
        Next
        Return Ok(New JObject From {{"deactivated", done}})
    End Function

    ' Bulk import. The browser reads the spreadsheet and maps its columns;
    ' rows arrive as { row?, first_name, last_name, email, id_number,
    ' company, custom: { field_key: value } }. Run with dry_run=true first
    ' to show the preview, then again to commit.
    <HttpPost, Route("Contact/import")>
    Public Function Import(<FromBody> data As JObject) As IHttpActionResult
        Dim rows = TryCast(data?("rows"), JArray)
        If rows Is Nothing OrElse rows.Count = 0 Then Return BadRequest("'rows' is required.")
        If rows.Count > ContactService.MaxImportRows Then Return BadRequest($"Import is limited to {ContactService.MaxImportRows:N0} rows per file.")

        Dim accountId = CurrentUser.AccountId()
        Dim groupId = ContactService.Clean(data.Value(Of String)("group_id"))
        Dim updateExisting = If(data.Value(Of Boolean?)("update_existing"), True)
        Dim dryRun = If(data.Value(Of Boolean?)("dry_run"), False)

        Try
            If groupId IsNot Nothing Then
                Using conn = Db.Open()
                    ContactService.CheckGroups(conn, Nothing, accountId, {groupId})
                End Using
            End If
            Return Ok(ContactService.Import(accountId, rows, groupId, updateExisting, dryRun, CurrentUser.Email()))
        Catch ex As ContactService.ContactRuleException
            Return RuleError(ex)
        End Try
    End Function


    ' --- helpers ----------------------------------------------------------

    Private Function RuleError(ex As ContactService.ContactRuleException) As IHttpActionResult
        Select Case ex.Reason
            Case "not_found"
                Return NotFound()
            Case "duplicate", "suppressed"
                Return Content(HttpStatusCode.Conflict, New JObject From {
                    {"reason", ex.Reason}, {"message", ex.Message}, {"existing_id", ex.ExistingId}
                })
            Case Else
                Return Content(HttpStatusCode.BadRequest, New JObject From {{"reason", ex.Reason}, {"message", ex.Message}})
        End Select
    End Function

    Private Shared Function LoadGroups(contactId As String) As JArray
        Dim out As New JArray
        Using conn = Db.Open()
            Using cmd = conn.CreateCommand()
                cmd.CommandText = "
                    SELECT g.id, g.name FROM dbo.ContactGroupMembers m
                    JOIN dbo.ContactGroups g ON g.id = m.group_id
                    WHERE m.contact_id = @c AND g.is_active = 1
                    ORDER BY g.name"
                cmd.Parameters.Add(New SqlParameter("@c", SqlDbType.Char, 36) With {.Value = contactId})
                Using rdr = cmd.ExecuteReader()
                    While rdr.Read()
                        out.Add(New JObject From {{"id", rdr.GetString(0)}, {"name", rdr.GetString(1)}})
                    End While
                End Using
            End Using
        End Using
        Return out
    End Function

    Private Shared Function Str(rdr As SqlDataReader, i As Integer) As JToken
        Return If(rdr.IsDBNull(i), JValue.CreateNull(), New JValue(rdr.GetString(i)))
    End Function

    Private Shared Function EscapeLike(s As String) As String
        Return s.Replace("\", "\\").Replace("%", "\%").Replace("_", "\_").Replace("[", "\[")
    End Function

End Class
